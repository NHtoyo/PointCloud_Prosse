"""CLI wrapper: point-cloud I/O and reports stay outside the core algorithm."""

from __future__ import annotations

import argparse
import sys
import traceback
from pathlib import Path

import numpy as np
import pointcloud_io
from stem_diameter_algorithm import StemDiameterParams, StemDiameterStageError, analyze_stem
from stem_diameter_output import write_stem_diameter_error, write_stem_diameter_outputs
from output_generations import new_run_id, validate_run_id


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
    parser.add_argument("--run-id", help="Unique Unity operation ID for this immutable output generation")
    parser.add_argument("--source-point-cloud-path", help="Original Unity-loaded point-cloud path (distinct from analysis input)")
    parser.add_argument("--source-loaded-point-count", type=int,
                        help="Number of points in the original loaded Unity point cloud")
    parser.add_argument("--analysis-visible-point-count", type=int,
                        help="Number of visible points exported by Unity for this analysis")
    parser.add_argument("--analysis-visible-point-fingerprint",
                        help="SHA-256 fingerprint of the visible point XYZ values in data-space")
    parser.add_argument("--coordinate-scale-to-mm", type=float, required=True,
                        help="Multiply source coordinates by this factor to get millimeters")
    parser.add_argument("--measurement-interval-mm", type=float, default=10.0)
    parser.add_argument("--centerline-step-mm", type=float, default=5.0)
    parser.add_argument("--min-centerline-bin-points", type=int, default=30)
    parser.add_argument("--centerline-axis", choices=("pca", "y"), default="pca")
    parser.add_argument("--centerline-model", choices=("polyline", "spline"), default="polyline")
    parser.add_argument("--component-knn-k", type=int, default=8)
    parser.add_argument("--component-alpha", type=float, default=2.5)
    component_group = parser.add_mutually_exclusive_group()
    component_group.add_argument("--largest-component", dest="use_largest_component", action="store_true")
    component_group.add_argument("--no-largest-component", dest="use_largest_component", action="store_false")
    parser.set_defaults(use_largest_component=True)
    parser.add_argument("--local-axis-radius-mm", type=float, default=15.0)
    parser.add_argument("--slice-roi-radius-mm", type=float, default=30.0)
    parser.add_argument("--primary-slice-thickness-mm", type=float, default=5.0)
    parser.add_argument("--query-workers", type=int, default=-1,
                        help="cKDTree workers; -1 uses all available CPU cores")
    return parser


def main(argv=None) -> int:
    args = build_parser().parse_args(argv)
    output_dir = Path(args.output_dir).expanduser().resolve()
    run_id = validate_run_id(args.run_id) if args.run_id else new_run_id()
    stage = "INPUT"
    input_diagnostics = {}
    parameters = {
        "coordinate_scale_to_mm": args.coordinate_scale_to_mm,
        "centerline_axis_mode": args.centerline_axis,
        "centerline_model": args.centerline_model,
        "use_largest_component": args.use_largest_component,
        "component_knn_k": args.component_knn_k,
        "component_alpha": args.component_alpha,
        "centerline_step_mm": args.centerline_step_mm,
        "min_centerline_bin_points": args.min_centerline_bin_points,
    }

    def diagnostic(message: str) -> None:
        nonlocal stage
        marker = "[StemDiameter][STAGE][START] "
        if message.startswith(marker):
            stage = message[len(marker):].split(" ", 1)[0]
        print(message, flush=True)

    try:
        diagnostic(f"[StemDiameter][STAGE][START] INPUT input_path={args.input}")
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
        if args.source_loaded_point_count is not None and args.source_loaded_point_count < len(points_raw):
            raise ValueError("source_loaded_point_count cannot be smaller than the analysis input point count.")
        if args.analysis_visible_point_count is not None and args.analysis_visible_point_count != len(points_raw):
            raise ValueError(
                "analysis_visible_point_count must match the number of points loaded from the analysis input."
            )

        raw_xyz_span = np.ptp(points_raw, axis=0)
        points_mm = _convert_points_to_mm(points_raw, args.coordinate_scale_to_mm)
        mm_xyz_span = np.ptp(points_mm, axis=0)
        input_diagnostics = {
            "input_path": str(input_path),
            "input_point_count": int(len(points_mm)),
            "xyz_min_mm": np.min(points_mm, axis=0).tolist(),
            "xyz_max_mm": np.max(points_mm, axis=0).tolist(),
            "xyz_span_mm": mm_xyz_span.tolist(),
            "coordinate_scale_to_mm": float(args.coordinate_scale_to_mm),
        }
        print(f"[StemDiameter] points={len(points_raw):,}", flush=True)
        print(f"[StemDiameterInput] raw_xyz_span={_format_xyz(raw_xyz_span)}", flush=True)
        print(f"[StemDiameterInput] coordinate_scale_to_mm={args.coordinate_scale_to_mm:.9g}", flush=True)
        print(f"[StemDiameterInput] mm_xyz_span={_format_xyz(mm_xyz_span)}", flush=True)
        diagnostic("[StemDiameter][STAGE][OK] INPUT " + " ".join(
            f"{key}={value}" for key, value in input_diagnostics.items()
        ))

        params = StemDiameterParams(
            measurement_interval_mm=args.measurement_interval_mm,
            centerline_step_mm=args.centerline_step_mm,
            min_centerline_bin_points=args.min_centerline_bin_points,
            centerline_axis_mode=args.centerline_axis,
            centerline_model=args.centerline_model,
            use_largest_component=args.use_largest_component,
            component_knn_k=args.component_knn_k,
            component_alpha=args.component_alpha,
            local_axis_radius_mm=args.local_axis_radius_mm,
            slice_roi_radius_mm=args.slice_roi_radius_mm,
            primary_slice_thickness_mm=args.primary_slice_thickness_mm,
            query_workers=args.query_workers,
        )
        stage = "ANALYSIS"
        result = analyze_stem(points_mm, params, _progress, diagnostic)
        result.diagnostics["input"].update(input_diagnostics)

        stage = "OUTPUT"
        diagnostic(f"[StemDiameter][STAGE][START] OUTPUT output_dir={output_dir}")
        _progress(0.93, "JSON、CSV、品質グラフを書き出し中...")
        published = write_stem_diameter_outputs(
            output_dir, result, str(input_path), len(points_mm),
            args.source_point_cloud_path, args.source_loaded_point_count,
            args.analysis_visible_point_count, args.analysis_visible_point_fingerprint, run_id,
        )
        generation_dir = Path(published["generation_directory"])
        valid = [s.equivalent_diameter_mm for s in result.sections if s.equivalent_diameter_mm is not None]
        output_diagnostics = {
            "json_path": str(generation_dir / "stem_diameter.json"),
            "csv_path": str(generation_dir / "stem_diameter.csv"),
            "png_paths": [str(generation_dir / "diameter_profile.png"), str(generation_dir / "quality_profile.png")],
            "run_id": run_id,
            "valid_section_count": len(valid),
        }
        diagnostic("[StemDiameter][STAGE][OK] OUTPUT " + " ".join(
            f"{key}={value}" for key, value in output_diagnostics.items()
        ))
        print("================ Stem diameter result ================", flush=True)
        print(f"centerline_length_mm={result.centerline_length_mm:.3f}", flush=True)
        print(f"sections={len(result.sections)} valid_primary={len(valid)}", flush=True)
        print(f"max_segment_length_mm={result.diagnostics['centerline_model'].get('max_segment_length_mm', 'n/a')}", flush=True)
        print(f"median_segment_length_mm={result.diagnostics['centerline_model'].get('median_segment_length_mm', 'n/a')}", flush=True)
        if valid:
            print(f"median_primary_diameter_mm={float(np.median(valid)):.4f}", flush=True)
        print(f"output_dir={output_dir}", flush=True)
        print(f"[ResultGeneration] run_id={run_id}", flush=True)
        _progress(1.0, "茎径プロファイル解析が完了しました。")
        return 0
    except Exception as exc:
        error_stage = exc.stage if isinstance(exc, StemDiameterStageError) else stage
        if error_stage == "ANALYSIS":
            error_stage = "UNKNOWN_ANALYSIS_STAGE"
        error_parameters = exc.parameters if isinstance(exc, StemDiameterStageError) else parameters
        error_diagnostics = exc.diagnostics if isinstance(exc, StemDiameterStageError) else input_diagnostics
        diagnostic(f"[StemDiameter][STAGE][FAIL] {error_stage} message={str(exc).replace(' ', '_')}")
        report = {
            "run_id": run_id,
            "stage": error_stage,
            "message": str(exc),
            "parameters": error_parameters,
            "diagnostics": error_diagnostics,
            "exception_type": type(exc).__name__,
        }
        try:
            error_path = write_stem_diameter_error(output_dir, report, run_id)
            diagnostic(f"[StemDiameter][ERROR_JSON] {error_path}")
        except Exception as report_exc:
            print(f"[StemDiameterErrorReportFailure] {type(report_exc).__name__}: {report_exc}",
                  file=sys.stderr, flush=True)
        print(f"[StemDiameterError] stage={error_stage} {type(exc).__name__}: {exc}",
              file=sys.stderr, flush=True)
        traceback.print_exc(file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
