import json
import os
import shutil
import tempfile
from datetime import datetime
from pathlib import Path

import numpy as np

from output_generations import create_staging_directory, new_run_id, publish_generation


def _write_array(path: Path, array: np.ndarray, dtype: str):
    arr = np.asarray(array)
    target_dtype = np.dtype(dtype)
    if arr.dtype != target_dtype:
        arr = arr.astype(target_dtype, copy=False)
    arr.tofile(path)


def _has_data(array: np.ndarray, default_value=0) -> bool:
    arr = np.asarray(array)
    return arr.size > 0 and bool(np.any(arr != default_value))


def _register_file(metadata_files: dict, key: str, filename: str, dtype: str, point_count: int):
    metadata_files[key] = {"filename": filename, "dtype": dtype, "shape": [point_count]}


def _write_json(path: Path, payload: dict):
    with path.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(payload, stream, indent=2, ensure_ascii=False, allow_nan=False)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def _write_metadata(path: Path, payload: dict):
    _write_json(path, payload)


def _write_removal_report(path: Path, payload: dict):
    _write_json(path, payload)


def write_results(output_dir: str, results: dict, params: dict, mode: str,
                  original_count: int, analysis_count: int, voxel_size: float = None,
                  coordinate_scale_to_mm: float = None, operation_id: str | None = None,
                  source_path: str | None = None, additional_artifacts: dict[str, str] | None = None):
    """Publish all outputs as an immutable generation, then atomically update its pointer."""
    output_root = Path(output_dir).expanduser().resolve()
    run_id = str(operation_id or new_run_id())
    stage = create_staging_directory(output_root, run_id)
    artifact_names = []
    try:
        point_count = len(results["remove_mask"])
        metadata_files = {}

        array_specs = (
            ("remove_mask", "remove_mask.bin", "|u1", "uint8", None),
            ("preview_mask", "preview_mask.bin", "|u1", "uint8", None),
            ("white_haze_candidate_mask", "white_haze_candidate_mask.bin", "|u1", "uint8", None),
        )
        for key, filename, dtype, public_dtype, default in array_specs:
            _write_array(stage / filename, results[key], dtype)
            _register_file(metadata_files, key, filename, public_dtype, point_count)
            artifact_names.append(filename)

        optional_specs = (
            ("sor_score", "sor_score.bin", "<f4", "float32", 0),
            ("density_score", "density_score.bin", "<f4", "float32", 0),
            ("radius_neighbor_count", "radius_neighbor_count.bin", "<i4", "int32", 0),
            ("cc_noise_score", "cc_noise_score.bin", "<f4", "float32", 0),
            ("white_haze_score", "white_haze_score.bin", "<f4", "float32", 0),
            ("cluster_id", "cluster_id.bin", "<i4", "int32", -1),
            ("reason", "reason.bin", "<i4", "int32", 0),
        )
        for key, filename, dtype, public_dtype, default in optional_specs:
            if _has_data(results[key], default_value=default):
                _write_array(stage / filename, results[key], dtype)
                _register_file(metadata_files, key, filename, public_dtype, point_count)
                artifact_names.append(filename)

        _write_array(stage / "preview_reason.bin", results["preview_reason"], "<i4")
        _register_file(metadata_files, "preview_reason", "preview_reason.bin", "int32", point_count)
        artifact_names.append("preview_reason.bin")

        for filename, source in (additional_artifacts or {}).items():
            if Path(filename).name != filename or not Path(source).is_file():
                raise ValueError(f"Invalid additional artifact: {filename!r}")
            shutil.copyfile(source, stage / filename)
            artifact_names.append(filename)

        metadata = {
            "operation_id": run_id,
            "point_count": point_count,
            "mode": mode,
            "dbscan_mode": results["dbscan_mode"],
            "dbscan_voxel_size": results["dbscan_voxel_size"],
            "dbscan_analysis_count": results["dbscan_analysis_count"],
            "voxel_size": voxel_size,
            "coordinate_unit": "mm",
            "coordinate_scale_to_mm": coordinate_scale_to_mm,
            "source_path": str(Path(source_path).expanduser().resolve()) if source_path else None,
            "scalar_units": {
                "sor_score": "mm",
                "cc_noise_score": "mm",
                "density_score": "1/mm",
                "white_haze_score": "dimensionless",
                "radius_neighbor_count": "count",
            },
            "files": metadata_files,
            "parameters": params,
        }
        _write_metadata(stage / "metadata.json", metadata)
        artifact_names.append("metadata.json")

        kept_count = int(np.sum(~results["remove_mask"]))
        removed_count = int(np.sum(results["remove_mask"]))
        report = {
            "operation_id": run_id,
            "timestamp": datetime.now().isoformat(),
            "mode": mode,
            "original_point_count": original_count,
            "analysis_point_count": analysis_count,
            "voxel_size": voxel_size,
            "coordinate_unit": "mm",
            "coordinate_scale_to_mm": coordinate_scale_to_mm,
            "downsample_ratio": float(analysis_count / original_count) if original_count > 0 else 0.0,
            "kept_point_count": kept_count,
            "removed_candidate_count": removed_count,
            "removed_by_sor": results["removed_by_sor_count"],
            "removed_by_ror": results["removed_by_ror_count"],
            "removed_by_low_density": results["removed_by_low_density_count"],
            "removed_by_cc_noise": results["removed_by_cc_noise_count"],
            "removed_by_small_cluster": results["removed_by_small_cluster_count"],
            "removed_by_white_haze": results["removed_by_white_haze_count"],
            "white_haze_candidate_count": results["white_haze_candidate_count"],
            "dbscan_timeout": results["dbscan_timeout"],
            "parameters_used": params,
        }
        _write_removal_report(stage / "removal_report.json", report)
        artifact_names.append("removal_report.json")

        published = publish_generation(output_root, stage, run_id, artifact_names,
                                       {"kind": "noise_filter", "point_count": point_count})
        return published
    finally:
        if stage.exists():
            shutil.rmtree(stage, ignore_errors=True)


def read_bin(path: str, dtype: str) -> np.ndarray:
    if dtype == "uint8":
        np_dtype = "|u1"
    elif dtype == "float32":
        np_dtype = "<f4"
    elif dtype == "int32":
        np_dtype = "<i4"
    else:
        raise ValueError(f"不明な dtype: {dtype}")
    return np.fromfile(path, dtype=np_dtype)
