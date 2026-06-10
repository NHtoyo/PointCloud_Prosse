import sys
import os
from pathlib import Path
import re
import json
import numpy as np
import csv
import argparse
import open3d as o3d

# ==========================================
# Parameters Setup via argparse
# ==========================================
def parse_arguments():
    parser = argparse.ArgumentParser(description="Downsampling Script (CLI version)")
    parser.add_argument("--input", type=str, default="input", help="Input directory containing point clouds")
    parser.add_argument("--output", type=str, default="downsample", help="Output directory for downsampled point clouds")
    parser.add_argument("--scale_json", type=str, default="config/scale_calibration_report.json", help="Path to scale report JSON")
    parser.add_argument("--mode", type=int, choices=[1, 2, 3], default=1, 
                        help="1: Overall merge downsampling only, 2: Per-organ & per-file downsampling only, 3: Both")
    parser.add_argument("--voxel_size", type=float, default=5.0, help="Voxel size in mm (real scale)")
    return parser.parse_args()

# ==========================================
# Constants and Configurations
# ==========================================
VOXEL_EST_METHOD = "knn"
TARGET_N_POINTS = 500_000
KNN_K = 16
KNN_MULTIPLIER = 1.01
KNN_MAX_SAMPLES = 50_000

ALLOW_EXTS = {".ply", ".pcd", ".xyz", ".txt"}
ORGAN_NAMES = {"leaf", "fruit", "stem"}

def ensure_output_dir(path: str):
    Path(path).mkdir(parents=True, exist_ok=True)

def read_scale_json(json_path: str):
    if not os.path.exists(json_path):
        print(f"Error: Scale file not found: {json_path}")
        print("Please run 1_scale_calibration.py first.")
        sys.exit(1)
        
    with open(json_path, "r", encoding="utf-8") as f:
        data = json.load(f)

    meters_per_unit = None
    mm_per_unit = None

    for k in ["meters_per_unit", "m_per_unit", "unit_to_m"]:
        if k in data and isinstance(data[k], (int, float)):
            meters_per_unit = float(data[k]); break
    for k in ["mm_per_unit", "scale_mm_per_unit", "unit_to_mm"]:
        if k in data and isinstance(data[k], (int, float)):
            mm_per_unit = float(data[k]); break

    if (meters_per_unit is None or mm_per_unit is None) and "SCALE" in data:
        s = str(data["SCALE"])
        m = re.search(r"([0-9]+(?:\.[0-9]+)?)\s*mm\s*/?\s*unit", s, flags=re.I)
        if m: mm_per_unit = float(m.group(1))

    if meters_per_unit is None and mm_per_unit is not None:
        meters_per_unit = mm_per_unit / 1000.0
    if mm_per_unit is None and meters_per_unit is not None:
        mm_per_unit = meters_per_unit * 1000.0

    if meters_per_unit is None or mm_per_unit is None:
        raise ValueError(
            f"Could not parse scale from JSON: {json_path}\n"
            "Expected keys: meters_per_unit / mm_per_unit / SCALE='xxx mm/unit'"
        )
    return meters_per_unit, mm_per_unit

def read_point_cloud_any(path: Path) -> o3d.geometry.PointCloud:
    ext = path.suffix.lower()
    if ext in {".ply", ".pcd", ".xyz"}:
        pcd = o3d.io.read_point_cloud(str(path))
        if pcd.is_empty():
            raise ValueError(f"Empty point cloud: {path}")
        return pcd
    if ext == ".txt":
        rows = []
        with open(path, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line: continue
                toks = line.split(",") if "," in line else line.split()
                try:
                    vals = [float(t) for t in toks]
                except:
                    continue
                if len(vals) < 3: continue
                rows.append(vals)
        if not rows:
            raise ValueError(f"No numeric rows in: {path}")
        arr = np.asarray(rows, dtype=np.float32)
        pts = arr[:, :3]
        pcd = o3d.geometry.PointCloud(o3d.utility.Vector3dVector(pts))
        if arr.shape[1] >= 6:
            rgb = arr[:, 3:6].astype(np.float32)
            if rgb.max() > 1.5: rgb = rgb / 255.0
            rgb = np.clip(rgb, 0.0, 1.0)
            pcd.colors = o3d.utility.Vector3dVector(rgb)
        return pcd
    raise ValueError(f"Unsupported extension: {path.suffix}")

def concat_pointclouds(pcd_list):
    all_pts, all_cols = [], []
    has_color = any(np.asarray(p.colors).shape[0] > 0 for p in pcd_list)
    for p in pcd_list:
        pts = np.asarray(p.points); all_pts.append(pts)
        if has_color:
            cols = np.asarray(p.colors)
            if cols.shape[0] == 0:
                all_cols.append(np.zeros((pts.shape[0], 3), dtype=np.float32))
            else:
                all_cols.append(cols)
    P = np.vstack(all_pts)
    merged = o3d.geometry.PointCloud(o3d.utility.Vector3dVector(P))
    if has_color:
        C = np.vstack(all_cols)
        merged.colors = o3d.utility.Vector3dVector(C)
    return merged

def estimate_voxel_size_knn_with_dists(
    pcd: o3d.geometry.PointCloud,
    k=16, multiplier=2.5, max_samples=50_000
):
    n = np.asarray(pcd.points).shape[0]
    idx = np.arange(n)
    if n > max_samples:
        idx = np.random.choice(n, size=max_samples, replace=False)
    sub = pcd.select_by_index(idx)

    tree = o3d.geometry.KDTreeFlann(sub)
    dists = []
    pts = np.asarray(sub.points)
    for i in range(pts.shape[0]):
        k_res, _, dist2 = tree.search_knn_vector_3d(pts[i], k+1)
        if k_res >= 2:
            d = np.sqrt(dist2[1:]).mean()
            dists.append(d)
    dists = np.asarray(dists, dtype=np.float32)
    if dists.size == 0:
        raise ValueError("kNN mean distance estimation failed (no distances).")

    mean_nn = float(np.mean(dists))
    std_nn  = float(np.std(dists))
    q25, q50, q75 = [float(np.percentile(dists, p)) for p in (25, 50, 75)]
    stats = {
        "k": int(k),
        "multiplier": float(multiplier),
        "n_samples": int(dists.size),
        "min_nn_unit": float(np.min(dists)),
        "q25_nn_unit": q25,
        "median_nn_unit": q50,
        "q75_nn_unit": q75,
        "max_nn_unit": float(np.max(dists)),
        "mean_nn_unit": mean_nn,
        "std_nn_unit": std_nn
    }
    voxel_unit = mean_nn * float(multiplier)
    return float(voxel_unit), stats, dists

def detect_organ_label(p: Path) -> str:
    parts_lower = [x.lower() for x in p.parts]
    for org in ORGAN_NAMES:
        if org in parts_lower:
            return org
    stem_lower = p.stem.lower()
    for org in ORGAN_NAMES:
        if org in stem_lower:
            return org
    return "other"

def safe_write_ply(path: Path, pcd: o3d.geometry.PointCloud):
    path.parent.mkdir(parents=True, exist_ok=True)
    ok = o3d.io.write_point_cloud(str(path), pcd, write_ascii=False, print_progress=False)
    if not ok:
        raise RuntimeError(f"Failed to write: {path}")

# ==========================================
# Main Execution Flow
# ==========================================
def main():
    args = parse_arguments()

    INPUT_DIR = args.input
    OUTPUT_DIR = args.output
    SCALE_JSON = args.scale_json
    
    # Mode toggles
    DO_OVERALL_MERGE = args.mode in [1, 3]
    SAVE_PER_ORGAN_MERGED_PLY = args.mode in [2, 3]
    SAVE_PER_FILE_DOWNSAMPLED_PLY = args.mode in [2, 3]
    
    VOXEL_REAL_OVERRIDE_MM = args.voxel_size

    if not os.path.exists(INPUT_DIR):
        print(f"Error: Input directory {INPUT_DIR} does not exist.")
        sys.exit(1)
        
    ensure_output_dir(OUTPUT_DIR)
    ensure_output_dir(os.path.join(OUTPUT_DIR, "by_organ"))
    ensure_output_dir(os.path.join(OUTPUT_DIR, "per_file"))

    meters_per_unit, mm_per_unit = read_scale_json(SCALE_JSON)
    print(f"[SCALE] {mm_per_unit:.6f} mm/unit  ({meters_per_unit:.9f} m/unit)")

    in_dir = Path(INPUT_DIR).resolve()
    out_dir = Path(OUTPUT_DIR).resolve()

    files = []
    for p in sorted(in_dir.rglob("*")):
        if not p.is_file(): continue
        if p.suffix.lower() not in ALLOW_EXTS: continue
        try:
            _ = p.resolve().relative_to(out_dir)
            continue
        except ValueError:
            pass
        files.append(p)

    if len(files) == 0:
        print(f"Warning: No point cloud files found in: {INPUT_DIR}")
        sys.exit(0)

    print(f"Found {len(files)} files.")
    
    pcds = []
    pre_counts = []
    organs = []

    # 10.0% to 40.0% progress for loading
    print("[Progress] 10.0 Loading point clouds...", flush=True)
    for i, p in enumerate(files):
        pct = 10.0 + 30.0 * (i / len(files))
        print(f"[Progress] {pct:.1f} Loading file {i+1}/{len(files)}: {p.name}", flush=True)
        pc = read_point_cloud_any(p)
        pcds.append(pc)
        pre_counts.append(int(np.asarray(pc.points).shape[0]))
        organs.append(detect_organ_label(p))

    total_pre = int(sum(pre_counts))
    print(f"Total points before merge: {total_pre:,}")

    # 40.0% to 50.0% progress for voxel estimation/calculation
    print("[Progress] 40.0 Calculating voxel size...", flush=True)
    voxel_unit_estimated = 1.0
    method_info = {}
    
    if VOXEL_REAL_OVERRIDE_MM is not None:
        voxel_real_m_override = float(VOXEL_REAL_OVERRIDE_MM) / 1000.0
        voxel_unit = voxel_real_m_override / meters_per_unit
        method_info["override_real_m"] = float(voxel_real_m_override)
        method_info["method"] = "override_mm"
    else:
        temp_merged = concat_pointclouds(pcds)
        voxel_unit, knn_stats, knn_dists_unit = estimate_voxel_size_knn_with_dists(
            temp_merged, k=KNN_K, multiplier=KNN_MULTIPLIER, max_samples=KNN_MAX_SAMPLES
        )
        method_info = {"method": "knn", "k": int(KNN_K), "multiplier": float(KNN_MULTIPLIER)}

    voxel_real_m = voxel_unit * meters_per_unit
    voxel_real_mm = voxel_real_m * 1000.0

    print(f"Voxel size (unit) = {voxel_unit:.6f}")
    print(f"Voxel size (real) = {voxel_real_mm:.3f} mm")

    merged_n = 0
    merged_ds_n = 0
    final_output_path = None
    
    # 50.0% to 80.0% progress for overall merge downsampling
    if DO_OVERALL_MERGE:
        print("[Progress] 50.0 Merging point clouds for overall downsampling...", flush=True)
        merged = concat_pointclouds(pcds)
        merged_n = int(np.asarray(merged.points).shape[0])
        
        print("[Progress] 60.0 Performing overall voxel downsampling...", flush=True)
        merged_ds = merged.voxel_down_sample(voxel_size=voxel_unit)
        
        if len(files) == 1:
            output_filename = f"{files[0].stem}.ply"
        else:
            output_filename = "merged_downsampled.ply"
            
        final_output_path = out_dir / output_filename
        
        print(f"[Progress] 70.0 Saving overall merged file...", flush=True)
        o3d.io.write_point_cloud(str(final_output_path), merged_ds, write_ascii=False, print_progress=False)
        
        merged_ds_n = int(np.asarray(merged_ds.points).shape[0])
        print(f"Saved merged PLY: {final_output_path}")
        print(f"Points: {merged_n} -> {merged_ds_n}")

    # 80.0% to 95.0% progress for per-file/per-organ downsampling
    post_counts = []
    organ_summary = []
    
    if SAVE_PER_FILE_DOWNSAMPLED_PLY or SAVE_PER_ORGAN_MERGED_PLY:
        print("[Progress] 80.0 Performing individual/organ downsampling...", flush=True)
        
        # Per file
        if SAVE_PER_FILE_DOWNSAMPLED_PLY:
            for idx, (p, org, pc) in enumerate(zip(files, organs, pcds)):
                pct = 80.0 + 10.0 * (idx / len(files))
                print(f"[Progress] {pct:.1f} Downsampling file {idx+1}/{len(files)}: {p.name}", flush=True)
                ds = pc.voxel_down_sample(voxel_size=voxel_unit)
                post_counts.append(int(np.asarray(ds.points).shape[0]))
                
                rel = p.relative_to(in_dir)
                out_path = out_dir / "per_file" / rel
                out_path = out_path.with_suffix(".ply")
                safe_write_ply(out_path, ds)
        else:
            post_counts = [0] * len(files)

        # Per organ
        if SAVE_PER_ORGAN_MERGED_PLY:
            print("[Progress] 90.0 Merging and downsampling organ parts...", flush=True)
            organ_to_indices = {}
            for i, org in enumerate(organs):
                organ_to_indices.setdefault(org, []).append(i)

            for org_idx, (org, idxs) in enumerate(organ_to_indices.items()):
                if org not in ORGAN_NAMES: continue
                pcs_org = [pcds[i] for i in idxs]
                merged_org = concat_pointclouds(pcs_org)
                merged_org_n = int(np.asarray(merged_org.points).shape[0])

                merged_org_ds = merged_org.voxel_down_sample(voxel_size=voxel_unit)
                merged_org_ds_n = int(np.asarray(merged_org_ds.points).shape[0])

                out_org = out_dir / "by_organ" / f"merged_{org}_downsampled.ply"
                safe_write_ply(out_org, merged_org_ds)

                organ_summary.append({
                    "organ": org,
                    "n_files": len(idxs),
                    "points_before_merge": merged_org_n,
                    "points_after_ds": merged_org_ds_n,
                    "output_ply": str(out_org),
                })
                print(f"[ORG] saved: {out_org}  ({merged_org_n:,} -> {merged_org_ds_n:,})")

    # 95.0% to 100.0% progress for generating reports
    print("[Progress] 95.0 Saving CSV and JSON reports...", flush=True)
    
    OUTPUT_COUNTS_CSV = str(out_dir / "per_leaf_counts.csv")
    OUTPUT_RUNLOG_JSON = str(out_dir / "downsample_runlog.json")

    # Save CSV report without pandas
    with open(OUTPUT_COUNTS_CSV, mode="w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["organ", "file", "points_before", "points_after_ds"])
        for org, file_p, pre, post in zip(organs, files, pre_counts, post_counts):
            file_rel = str(file_p.relative_to(in_dir))
            writer.writerow([org, file_rel, pre, post if post is not None else ""])

    runlog = {
        "scale": {"meters_per_unit": meters_per_unit, "mm_per_unit": mm_per_unit},
        "voxel": {
            "voxel_size_unit": float(voxel_unit),
            "voxel_size_m": float(voxel_real_m),
            "voxel_size_mm": float(voxel_real_mm),
            **method_info
        },
        "counts": {
            "total_pre": int(total_pre),
            "merged_pre": int(merged_n),
            "merged_post": int(merged_ds_n),
        },
        "outputs": {
            "merged_all_ply": str(final_output_path) if DO_OVERALL_MERGE else None,
            "per_file_dir": str(out_dir / "per_file") if SAVE_PER_FILE_DOWNSAMPLED_PLY else None,
            "per_organ_dir": str(out_dir / "by_organ") if SAVE_PER_ORGAN_MERGED_PLY else None,
            "organ_merged": organ_summary,
        }
    }
    
    with open(OUTPUT_RUNLOG_JSON, "w", encoding="utf-8") as f:
        json.dump(runlog, f, ensure_ascii=False, indent=2)
        
    print("[Progress] 100.0 Done!", flush=True)
    print("\nDownsampling completed successfully!")
    print(f"Report JSON: {OUTPUT_RUNLOG_JSON}")

if __name__ == "__main__":
    main()
