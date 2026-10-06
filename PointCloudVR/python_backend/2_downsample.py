import sys
from pathlib import Path
import numpy as np
import json
import csv
import argparse
import os
import shutil
import tempfile
import open3d as o3d
import open3d.core as o3c
from coordinate_units import millimeters_to_data_length, validate_coordinate_scale_to_mm

# ==========================================
# Parameters Setup via argparse
# ==========================================
def parse_arguments(argv=None):
    parser = argparse.ArgumentParser(description="Label-aware Downsampling Script (CLI version)")
    parser.add_argument("--input", type=str, required=True, help="Input PLY file path (or folder containing PLY)")
    parser.add_argument("--output", type=str, required=True, help="Output directory for downsampled point clouds")
    parser.add_argument("--mode", type=int, choices=[1, 2, 3], default=1, 
                        help="1: Overall merge downsampling only, 2: Per-organ downsampling only, 3: Both")
    parser.add_argument("--voxel_size", type=float, default=5.0, help="Voxel size in mm (real scale)")
    parser.add_argument("--coordinate-scale-to-mm", type=float, required=True,
                        help="Multiply source data-space coordinates by this factor to get millimeters")
    parser.add_argument("--merged-output", type=str, default=None,
                        help="Exact output path for the merged PLY")
    return parser.parse_args(argv)

# Label mapping (Unity annotation categories)
LABEL_NAMES = {
    0: "unclassified",
    1: "stem",
    2: "leaf",
    3: "fruit",
    4: "flower",
    5: "support"
}

def ensure_output_dir(path: str):
    Path(path).mkdir(parents=True, exist_ok=True)


def has_calibrated_metadata(path: Path) -> bool:
    marker = b"comment pcwb_scale_calibrated true"
    with path.open("rb") as source:
        for line in source:
            if line.strip().lower() == b"end_header":
                break
            if line.strip().lower() == marker:
                return True
    return False


def preserve_calibrated_metadata(source_path: Path, output_path: Path) -> None:
    if not has_calibrated_metadata(source_path) or has_calibrated_metadata(output_path):
        return

    temporary_path = None
    inserted = False
    try:
        with tempfile.NamedTemporaryFile(
            mode="wb",
            prefix=f".{output_path.name}.",
            suffix=".pcwb-meta.tmp",
            dir=str(output_path.parent),
            delete=False,
        ) as destination:
            temporary_path = Path(destination.name)
            with output_path.open("rb") as output:
                for line in output:
                    if line.strip().lower() == b"end_header":
                        destination.write(b"comment pcwb_scale_calibrated true\n")
                        inserted = True
                    destination.write(line)
                    if inserted:
                        shutil.copyfileobj(output, destination, length=1024 * 1024)
                        break
            if not inserted:
                raise ValueError(f"PLY header is incomplete: {output_path}")
            destination.flush()
            os.fsync(destination.fileno())

        os.replace(temporary_path, output_path)
    finally:
        if temporary_path is not None and temporary_path.exists():
            temporary_path.unlink()

def main(argv=None):
    args = parse_arguments(argv)

    INPUT_PATH = Path(args.input).resolve()
    OUTPUT_DIR = Path(args.output).resolve()
    voxel_size_mm = args.voxel_size
    coordinate_scale_to_mm = args.coordinate_scale_to_mm

    # Resolve input PLY file
    input_file = None
    if INPUT_PATH.is_file() and INPUT_PATH.suffix.lower() == ".ply":
        input_file = INPUT_PATH
    elif INPUT_PATH.is_dir():
        # Find first PLY file in the directory
        ply_files = list(INPUT_PATH.glob("*.ply"))
        if ply_files:
            input_file = ply_files[0]
            print(f"Directory passed. Using first PLY file found: {input_file.name}")

    if not input_file or not input_file.exists():
        print(f"Error: Input PLY file not found or invalid: {args.input}")
        sys.exit(1)

    ensure_output_dir(str(OUTPUT_DIR))
    ensure_output_dir(str(OUTPUT_DIR / "by_organ"))

    if not np.isfinite(voxel_size_mm) or voxel_size_mm <= 0:
        raise ValueError("voxel_size must be a finite positive number in mm")
    coordinate_scale_to_mm = validate_coordinate_scale_to_mm(coordinate_scale_to_mm)
    voxel_size_data = millimeters_to_data_length(voxel_size_mm, coordinate_scale_to_mm)
    print(f"Voxel size: {voxel_size_mm:.2f} mm ({voxel_size_data:.9g} data-space)")

    # Load point cloud with attributes using Open3D Tensor API
    print(f"[Progress] 10.0 Loading point cloud: {input_file.name}...", flush=True)
    pcd = o3d.t.io.read_point_cloud(str(input_file))
    
    if pcd.point.positions.shape[0] == 0:
        print(f"Error: Empty point cloud in {input_file}")
        sys.exit(1)

    total_pre = pcd.point.positions.shape[0]
    print(f"Loaded {total_pre:,} points.")

    # Check and add default label if missing
    if "label" not in pcd.point:
        print("Warning: 'label' property not found. Initializing with label 0 (unclassified).")
        default_labels = np.zeros((total_pre, 1), dtype=np.int32)
        pcd.point["label"] = o3c.Tensor(default_labels, device=pcd.device)

    # Extract labels to numpy for grouping
    labels = pcd.point["label"].numpy().flatten()
    unique_labels = np.unique(labels)
    print(f"Found labels in cloud: {unique_labels} ({[LABEL_NAMES.get(l, f'label_{l}') for l in unique_labels]})")

    source_is_calibrated = has_calibrated_metadata(input_file)
    merged_ds_n = 0
    final_output_path = None

    # Mode 1: Overall merge downsampling
    if args.mode in [1, 3]:
        print("[Progress] 40.0 Downsampling entire point cloud...", flush=True)
        # Tensor voxel downsample automatically downsamples custom attributes (like label)
        merged_ds = pcd.voxel_down_sample(voxel_size=voxel_size_data)
        merged_ds_n = merged_ds.point.positions.shape[0]
        
        if args.merged_output:
            final_output_path = Path(args.merged_output).expanduser().resolve()
        else:
            final_output_path = OUTPUT_DIR / f"{input_file.stem}_downsampled.ply"
        ensure_output_dir(str(final_output_path.parent))
        
        print(f"[Progress] 60.0 Saving overall merged file...", flush=True)
        o3d.t.io.write_point_cloud(str(final_output_path), merged_ds)
        if source_is_calibrated:
            preserve_calibrated_metadata(input_file, final_output_path)
        print(f"Saved merged PLY: {final_output_path.name} ({total_pre:,} -> {merged_ds_n:,} points)")

    # Mode 2: Per-organ downsampling based on label attributes
    organ_summary = []
    if args.mode in [2, 3]:
        print("[Progress] 70.0 Performing per-label downsampling...", flush=True)
        
        for idx, label_val in enumerate(unique_labels):
            pct = 70.0 + 20.0 * (idx / len(unique_labels))
            label_name = LABEL_NAMES.get(label_val, f"label_{label_val}")
            print(f"[Progress] {pct:.1f} Processing label {label_val} ({label_name})...", flush=True)
            
            # Create mask for this label
            mask = (labels == label_val)
            indices = np.where(mask)[0]
            
            if len(indices) == 0:
                continue
                
            # Convert to Tensor index list for Open3D selection
            t_indices = o3c.Tensor(indices, dtype=o3c.Dtype.Int64, device=pcd.device)
            part_pcd = pcd.select_by_index(t_indices)
            part_pre_n = len(indices)
            
            # Apply downsampling on the sub-cloud
            part_ds = part_pcd.voxel_down_sample(voxel_size=voxel_size_data)
            part_post_n = part_ds.point.positions.shape[0]
            
            out_org = OUTPUT_DIR / "by_organ" / f"{input_file.stem}_{label_name}_downsampled.ply"
            o3d.t.io.write_point_cloud(str(out_org), part_ds)
            if source_is_calibrated:
                preserve_calibrated_metadata(input_file, out_org)
            
            organ_summary.append({
                "label_id": int(label_val),
                "organ": label_name,
                "points_before": int(part_pre_n),
                "points_after_ds": int(part_post_n),
                "output_ply": str(out_org)
            })
            print(f"[Organ] Saved label {label_val} ({label_name}): {out_org.name} ({part_pre_n:,} -> {part_post_n:,} points)")

    # Save CSV and JSON reports
    print("[Progress] 95.0 Saving CSV and JSON reports...", flush=True)
    
    OUTPUT_COUNTS_CSV = OUTPUT_DIR / f"{input_file.stem}_downsample_report.csv"
    OUTPUT_RUNLOG_JSON = OUTPUT_DIR / f"{input_file.stem}_downsample_runlog.json"

    # CSV Report
    with open(OUTPUT_COUNTS_CSV, mode="w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["label_id", "organ_name", "points_before", "points_after_ds"])
        
        # Write rows for organ sub-clouds if processed
        for item in organ_summary:
            writer.writerow([item["label_id"], item["organ"], item["points_before"], item["points_after_ds"]])

    # JSON Runlog
    runlog = {
        "source_file": str(input_file),
        "voxel": {
            "voxel_size_mm": float(voxel_size_mm),
            "voxel_size_data": float(voxel_size_data),
            "coordinate_scale_to_mm": float(coordinate_scale_to_mm),
            "coordinate_basis": "data-space",
            "source_scale_calibrated": source_is_calibrated
        },
        "counts": {
            "total_pre": int(total_pre),
            "merged_post": int(merged_ds_n) if args.mode in [1, 3] else 0,
        },
        "outputs": {
            "merged_ply": str(final_output_path) if args.mode in [1, 3] else None,
            "by_organ_dir": str(OUTPUT_DIR / "by_organ") if args.mode in [2, 3] else None,
            "organ_details": organ_summary
        }
    }
    
    with open(OUTPUT_RUNLOG_JSON, "w", encoding="utf-8") as f:
        json.dump(runlog, f, ensure_ascii=False, indent=2)
        
    print("[Progress] 100.0 Done!", flush=True)
    print("\nDownsampling completed successfully!")
    print(f"CSV Report: {OUTPUT_COUNTS_CSV.name}")
    print(f"JSON Report: {OUTPUT_RUNLOG_JSON.name}")

if __name__ == "__main__":
    main()
