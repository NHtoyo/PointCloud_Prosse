from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import sys
import tempfile
import traceback

import numpy as np

import pointcloud_io
from reference_sphere_algorithm import ReferenceSphereResult, estimate_reference_sphere


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Estimate a reference sphere from selected point-cloud data.")
    parser.add_argument("--input", required=True, help="Selected points in PLY or NPZ format")
    parser.add_argument("--output", required=True, help="Output JSON path")
    parser.add_argument("--knn-k", type=int, default=8)
    parser.add_argument("--connectivity-alpha", type=float, default=2.5)
    return parser


def _load_points(input_path: Path) -> np.ndarray:
    if input_path.suffix.lower() == ".npz":
        points, _ = pointcloud_io.load_npz(str(input_path))
    else:
        points, _ = pointcloud_io.load_ply(str(input_path))
    return np.asarray(points, dtype=np.float64)


def _result_payload(result: ReferenceSphereResult) -> dict:
    return {
        "method_name": result.method_name,
        "input_point_count": result.input_point_count,
        "component_point_count": result.component_point_count,
        "component_removed_count": result.component_removed_count,
        "knn_k": result.knn_k,
        "connectivity_alpha": result.connectivity_alpha,
        "median_knn_distance": result.median_knn_distance,
        "connectivity_epsilon": result.connectivity_epsilon,
        "fit_inlier_count": result.fit_inlier_count,
        "center": result.center.tolist(),
        "radius": result.radius,
        "diameter": result.diameter,
        "diameter_point1": result.diameter_point1.tolist(),
        "diameter_point2": result.diameter_point2.tolist(),
    }


def _write_json_atomic(output_path: Path, payload: dict) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(prefix=output_path.name + ".", suffix=".tmp", dir=output_path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as output_file:
            json.dump(payload, output_file, ensure_ascii=False, allow_nan=False, indent=2)
            output_file.write("\n")
            output_file.flush()
            os.fsync(output_file.fileno())
        os.replace(temporary_name, output_path)
    finally:
        if os.path.exists(temporary_name):
            os.unlink(temporary_name)


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    input_path = Path(args.input).expanduser().resolve()
    output_path = Path(args.output).expanduser().resolve()

    def report(progress: float, message: str) -> None:
        print(f"[Progress] {progress:.1f} {message}", flush=True)

    try:
        if not input_path.is_file():
            raise FileNotFoundError(f"Input point cloud does not exist: {input_path}")
        report(1.0, "選択点ファイルを読み込み中")
        points = _load_points(input_path)
        print(f"[ReferenceSphereInput] point_count={len(points)}", flush=True)
        report(4.0, "選択点の読み込み完了")

        result = estimate_reference_sphere(
            points,
            knn_k=args.knn_k,
            connectivity_alpha=args.connectivity_alpha,
            progress_callback=report,
        )
        report(98.0, "結果JSONを書き込み中")
        _write_json_atomic(output_path, _result_payload(result))

        print(f"[ReferenceSphere] method={result.method_name}", flush=True)
        print(f"[ReferenceSphere] component={result.component_point_count}/{result.input_point_count}", flush=True)
        print(f"[ReferenceSphere] fit_inliers={result.fit_inlier_count}", flush=True)
        print(f"[ReferenceSphere] center_data_space={result.center.tolist()}", flush=True)
        print(f"[ReferenceSphere] radius_data_space={result.radius:.12g}", flush=True)
        print(f"[ReferenceSphere] diameter_data_space={result.diameter:.12g}", flush=True)
        report(100.0, "リファレンス球直径推定が完了しました")
        return 0
    except Exception as exc:
        print(f"[ReferenceSphereError] {type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        traceback.print_exc(file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
