"""CLI wrapper: point-cloud I/O and reports stay outside the core algorithm."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np
import pointcloud_io
from stem_diameter_algorithm import StemDiameterParams, analyze_stem
from stem_diameter_output import write_stem_diameter_outputs

def _progress(value: float, message: str) -> None:
    print(f"[Progress] {value * 100.0:.1f} {message}", flush=True)


def _convert_points_to_mm(points_raw: np.ndarray, coordinate_scale_to_mm: float) -> np.ndarray:
    scale = float(coordinate_scale_to_mm)
    if not np.isfinite(scale) or scale <= 0.0:
        raise ValueError("coordinate_scale_to_mm must be a finite positive number.")
    points_mm = np.asarray(points_raw, dtype=np.float64) * scale
    if not np.all(np.isfinite(points_mm)):
        raise ValueError("Converted point coordinates contain NaN or inf.")
    return points_mm


def _format_xyz(values: np.ndarray) -> str:
    return "(" + ", ".join(f"{float(value):.6g}" for value in values) + ")"


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Estimate a plant main-stem diameter profile.")
    parser.add_argument("--input", required=True, help="Input PLY or NPZ point cloud")
    parser.add_argument("--output_dir", required=True, help="Directory for JSON, CSV and PNG outputs")
    parser.add_argument("--coordinate-scale-to-mm", type=float, required=True,
                        help="Multiply source coordinates by this factor to get millimeters")
    parser.add_argument("--measurement-interval-mm", type=float, default=10.0)
    parser.add_argument("--centerline-step-mm", type=float, default=5.0)
    parser.add_argument("--local-axis-radius-mm", type=float, default=15.0)
    parser.add_argument("--slice-roi-radius-mm", type=float, default=30.0)
    parser.add_argument("--primary-slice-thickness-mm", type=float, default=5.0)
    parser.add_argument("--query-workers", type=int, default=-1,
                        help="cKDTree workers; -1 uses all available CPU cores")
    return parser


def main(argv=None) -> int:
    args = build_parser().parse_args(argv)
    try:
        input_path = Path(args.input).expanduser().resolve()
        if not input_path.is_file():
            raise FileNotFoundError(f"Input point cloud not found: {input_path}")

        _progress(0.01, f"点群を読み込み中: {input_path}")
        if input_path.suffix.lower() == ".npz":
            loaded_points, _ = pointcloud_io.load_npz(str(input_path))
        else:
            loaded_points, _ = pointcloud_io.load_ply(str(input_path))
        points_raw = np.asarray(loaded_points, dtype=np.float64)
        if points_raw.ndim != 2 or points_raw.shape[1] != 3 or len(points_raw) == 0:
            raise ValueError(f"Input point coordinates must be a non-empty Nx3 array; shape={points_raw.shape}")
        if not np.all(np.isfinite(points_raw)):
            raise ValueError("Input point coordinates contain NaN or inf.")

        raw_xyz_span = np.ptp(points_raw, axis=0)
        points_mm = _convert_points_to_mm(points_raw, args.coordinate_scale_to_mm)
        mm_xyz_span = np.ptp(points_mm, axis=0)
        print(f"[StemDiameter] points={len(points_raw):,}", flush=True)
        print(f"[StemDiameterInput] raw_xyz_span={_format_xyz(raw_xyz_span)}", flush=True)
        print(f"[StemDiameterInput] coordinate_scale_to_mm={args.coordinate_scale_to_mm:.9g}", flush=True)
        print(f"[StemDiameterInput] mm_xyz_span={_format_xyz(mm_xyz_span)}", flush=True)

        params = StemDiameterParams(
            measurement_interval_mm=args.measurement_interval_mm,
            centerline_step_mm=args.centerline_step_mm,
            local_axis_radius_mm=args.local_axis_radius_mm,
            slice_roi_radius_mm=args.slice_roi_radius_mm,
            primary_slice_thickness_mm=args.primary_slice_thickness_mm,
            query_workers=args.query_workers,
        )
        result = analyze_stem(points_mm, params, _progress)
        _progress(0.93, "JSON、CSV、品質グラフを書き出し中...")
        write_stem_diameter_outputs(args.output_dir, result, str(input_path), len(points_mm))
        valid = [s.equivalent_diameter_mm for s in result.sections if s.equivalent_diameter_mm is not None]
        print("================ Stem diameter result ================", flush=True)
        print(f"centerline_length_mm={result.centerline_length_mm:.3f}", flush=True)
        print(f"sections={len(result.sections)} valid_primary={len(valid)}", flush=True)
        if valid:
            print(f"median_primary_diameter_mm={float(np.median(valid)):.4f}", flush=True)
        print(f"output_dir={Path(args.output_dir).resolve()}", flush=True)
        _progress(1.0, "茎径プロファイル解析が完了しました。")
        return 0
    except Exception as exc:
        print(f"[StemDiameterError] {type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
