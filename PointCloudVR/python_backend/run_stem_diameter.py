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


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Estimate a plant main-stem diameter profile.")
    parser.add_argument("--input", required=True, help="Input PLY or NPZ point cloud")
    parser.add_argument("--output_dir", required=True, help="Directory for JSON, CSV and PNG outputs")
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

        _progress(0.01, f"点群を読み込み中: {input_path.name}")
        if input_path.suffix.lower() == ".npz":
            points, _ = pointcloud_io.load_npz(str(input_path))
        else:
            points, _ = pointcloud_io.load_ply(str(input_path))
        points = np.asarray(points, dtype=np.float64)
        print(f"[StemDiameter] points={len(points):,}", flush=True)

        params = StemDiameterParams(
            measurement_interval_mm=args.measurement_interval_mm,
            centerline_step_mm=args.centerline_step_mm,
            local_axis_radius_mm=args.local_axis_radius_mm,
            slice_roi_radius_mm=args.slice_roi_radius_mm,
            primary_slice_thickness_mm=args.primary_slice_thickness_mm,
            query_workers=args.query_workers,
        )
        result = analyze_stem(points, params, _progress)
        _progress(0.93, "JSON、CSV、品質グラフを書き出し中...")
        write_stem_diameter_outputs(args.output_dir, result, str(input_path), len(points))
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
