"""File output for the independent stem-diameter numerical algorithm."""

from __future__ import annotations

import csv
import json
import math
import os
import tempfile
from dataclasses import asdict, is_dataclass
from datetime import datetime, timezone
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np


OUTPUT_NAMES = (
    "stem_diameter.json",
    "stem_diameter.csv",
    "diameter_profile.png",
    "quality_profile.png",
)


def _json_value(value):
    if is_dataclass(value):
        return _json_value(asdict(value))
    if isinstance(value, dict):
        return {str(key): _json_value(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_value(item) for item in value]
    if isinstance(value, np.ndarray):
        return _json_value(value.tolist())
    if isinstance(value, np.generic):
        return _json_value(value.item())
    if isinstance(value, float) and not math.isfinite(value):
        return None
    return value


def _write_json(path: Path, result, input_path: str, point_count: int,
                source_point_cloud_path: str | None = None,
                source_loaded_point_count: int | None = None,
                analysis_visible_point_count: int | None = None,
                analysis_visible_point_fingerprint: str | None = None):
    def vector(value):
        if value is None:
            return None
        return {"x": float(value[0]), "y": float(value[1]), "z": float(value[2])}

    def section_payload(section):
        payload = _json_value(section)
        for field in ("center_xyz_mm", "centerline_tangent_xyz", "local_axis_xyz", "basis_u_xyz", "basis_v_xyz"):
            payload[field] = vector(getattr(section, field))
        eigenvalues = section.local_pca_eigenvalues
        payload["local_pca_eigenvalues"] = (
            None if eigenvalues is None else {
                "lambda1": float(eigenvalues[0]),
                "lambda2": float(eigenvalues[1]),
                "lambda3": float(eigenvalues[2]),
            }
        )
        for slice_payload, item in zip(payload["slice_results"], section.slice_results):
            contour = item.contour_uv_mm
            slice_payload["contour_uv_mm"] = (
                None if contour is None else [
                    {"u_mm": float(point[0]), "v_mm": float(point[1])}
                    for point in contour
                ]
            )
        return payload

    payload = {
        "schema_version": 2,
        "algorithm": "prototype-derived local-PCA cross-section profile",
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "input_path": str(Path(input_path).resolve()),
        "point_count": int(point_count),
        "analysis_input_point_count": int(point_count),
        "analysis_visible_point_count": int(
            point_count if analysis_visible_point_count is None else analysis_visible_point_count
        ),
        "centerline_length_mm": float(result.centerline_length_mm),
        "centerline_axis_mode": result.parameters.centerline_axis_mode,
        "centerline_model": result.parameters.centerline_model,
        "parameters": _json_value(result.parameters),
        "use_largest_component": bool(result.parameters.use_largest_component),
        "component_knn_k": int(result.parameters.component_knn_k),
        "component_alpha": float(result.parameters.component_alpha),
        "component_input_point_count": int(result.diagnostics["connected_component"]["input_points"]),
        "component_point_count": int(result.diagnostics["connected_component"]["kept_points"]),
        "component_removed_count": int(result.diagnostics["connected_component"]["removed_points"]),
        "median_knn_distance_mm": result.diagnostics["connected_component"]["median_knn_distance_mm"],
        "connectivity_epsilon_mm": result.diagnostics["connected_component"]["epsilon_mm"],
        "diagnostics": _json_value(result.diagnostics),
        "warnings": list(result.warnings),
        "centerline": {
            "support_points_xyz_mm": [vector(p) for p in result.centerline_support_points_mm],
            "display_points_xyz_mm": [vector(p) for p in result.centerline_display_points_mm],
        },
        "sections": [section_payload(section) for section in result.sections],
    }
    if source_point_cloud_path:
        source_path = Path(source_point_cloud_path).expanduser().resolve()
        payload["source_point_cloud_path"] = str(source_path)
        payload["source_point_cloud_filename"] = source_path.name
    if source_loaded_point_count is not None:
        payload["source_loaded_point_count"] = int(source_loaded_point_count)
    if analysis_visible_point_fingerprint:
        payload["analysis_visible_point_fingerprint"] = str(analysis_visible_point_fingerprint)
    with path.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(payload, stream, ensure_ascii=False, allow_nan=False, separators=(",", ":"))


def _write_csv(path: Path, result):
    fields = [
        "index", "position_mm", "calculation_status", "equivalent_diameter_mm",
        "cross_section_area_mm2", "perimeter_mm", "diameter_3mm", "diameter_5mm", "diameter_7mm",
        "slice_diameter_range_mm", "slice_diameter_std_mm", "raw_point_count",
        "used_point_count", "outlier_fraction", "angular_coverage", "max_gap_deg",
        "interpolated_fraction", "shape_axis_ratio", "circularity",
        "local_axis_angle_deg", "local_pca_linearity",
    ]
    with path.open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        for section in result.sections:
            primary = next(
                (item for item in section.slice_results
                 if math.isclose(item.thickness_mm, result.parameters.primary_slice_thickness_mm)),
                None,
            )
            values = {field: getattr(section, field, None) for field in fields}
            if primary is not None:
                for field in (
                    "perimeter_mm", "raw_point_count", "used_point_count", "outlier_fraction",
                    "angular_coverage", "max_gap_deg", "interpolated_fraction",
                    "shape_axis_ratio", "circularity",
                ):
                    values[field] = getattr(primary, field)
            writer.writerow(values)


def _profile_values(result, field):
    return np.asarray([
        np.nan if getattr(section, field) is None else getattr(section, field)
        for section in result.sections
    ], dtype=np.float64)


def _write_plots(directory: Path, result):
    x = np.asarray([section.position_mm for section in result.sections], dtype=np.float64)
    fig, ax = plt.subplots(figsize=(9, 4.8), constrained_layout=True)
    ax.plot(x, _profile_values(result, "diameter_5mm"),
            label="Equivalent diameter (5 mm slab)", linewidth=1.6)
    ax.set_xlabel("Distance from top [mm]")
    ax.set_ylabel("Equivalent diameter [mm]")
    params = result.parameters
    component_text = (
        f"Largest component K={params.component_knn_k}, alpha={params.component_alpha:g}"
        if params.use_largest_component else "Largest component disabled"
    )
    ax.set_title(f"{params.centerline_axis_mode.upper()} / {params.centerline_model} / {component_text}")
    ax.grid(True, alpha=0.25)
    ax.legend()
    fig.savefig(directory / "diameter_profile.png", dpi=160)
    plt.close(fig)

    primary = result.parameters.primary_slice_thickness_mm
    quality = {"angular_coverage": [], "max_gap_deg": [], "outlier_fraction": []}
    for section in result.sections:
        item = next((s for s in section.slice_results if math.isclose(s.thickness_mm, primary)), None)
        for field in quality:
            value = None if item is None else getattr(item, field)
            quality[field].append(np.nan if value is None else value)

    fig, axes = plt.subplots(3, 1, figsize=(9, 7), sharex=True, constrained_layout=True)
    axes[0].plot(x, quality["angular_coverage"], color="#16864a")
    axes[0].set_ylabel("Coverage")
    axes[0].set_ylim(0, 1.05)
    axes[1].plot(x, quality["max_gap_deg"], color="#d17a00")
    axes[1].set_ylabel("Max gap [deg]")
    axes[2].plot(x, quality["outlier_fraction"], color="#2563a6")
    axes[2].set_ylabel("Radial outlier fraction")
    axes[2].set_xlabel("Distance from top [mm]")
    for axis in axes:
        axis.grid(True, alpha=0.25)
    fig.suptitle("Section quality descriptors (not an accept/reject decision)")
    fig.savefig(directory / "quality_profile.png", dpi=160)
    plt.close(fig)


def write_stem_diameter_outputs(output_dir, result, input_path: str, point_count: int,
                                source_point_cloud_path: str | None = None,
                                source_loaded_point_count: int | None = None,
                                analysis_visible_point_count: int | None = None,
                                analysis_visible_point_fingerprint: str | None = None):
    """Regenerate only the four named analysis outputs; preserve other user files."""
    target = Path(output_dir)
    target.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".stem-diameter-", dir=target) as staging:
        stage = Path(staging)
        _write_json(
            stage / "stem_diameter.json", result, input_path, point_count,
            source_point_cloud_path, source_loaded_point_count, analysis_visible_point_count,
            analysis_visible_point_fingerprint,
        )
        _write_csv(stage / "stem_diameter.csv", result)
        _write_plots(stage, result)
        for filename in OUTPUT_NAMES:
            os.replace(stage / filename, target / filename)


def write_stem_diameter_error(output_dir, payload: dict):
    """Atomically write a diagnostic report for a failed analysis run."""
    target = Path(output_dir)
    target.mkdir(parents=True, exist_ok=True)
    fd, temporary_name = tempfile.mkstemp(prefix=".stem-diameter-error-", suffix=".tmp", dir=target)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(_json_value(payload), stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary_name, target / "stem_diameter_error.json")
    finally:
        if os.path.exists(temporary_name):
            os.unlink(temporary_name)
