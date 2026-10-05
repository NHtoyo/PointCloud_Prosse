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


def _write_json(path: Path, result, input_path: str, point_count: int):
    def vector(value):
        if value is None:
            return None
        return {"x": float(value[0]), "y": float(value[1]), "z": float(value[2])}

    def section_payload(section):
        payload = _json_value(section)
        for field in ("center_xyz_units", "centerline_tangent_xyz", "local_axis_xyz", "basis_u_xyz", "basis_v_xyz"):
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
        "schema_version": 1,
        "algorithm": "prototype-derived local-PCA cross-section profile",
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "input_path": str(Path(input_path).resolve()),
        "point_count": int(point_count),
        "scale_mm_per_unit": float(result.scale_mm_per_unit),
        "centerline_length_mm": float(result.centerline_length_mm),
        "parameters": _json_value(result.parameters),
        "centerline": {
            "support_points_xyz_units": [vector(p) for p in result.centerline_support_points_units],
            "display_points_xyz_units": [vector(p) for p in result.centerline_display_points_units],
        },
        "sections": [section_payload(section) for section in result.sections],
    }
    with path.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(payload, stream, ensure_ascii=False, allow_nan=False, separators=(",", ":"))


def _write_csv(path: Path, result):
    fields = [
        "index", "position_mm", "calculation_status", "equivalent_diameter_mm",
        "cross_section_area_mm2", "diameter_3mm", "diameter_5mm", "diameter_7mm",
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
                    "raw_point_count", "used_point_count", "outlier_fraction",
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
    for field, label in (("diameter_3mm", "3 mm slab"), ("diameter_5mm", "5 mm slab (primary)"),
                         ("diameter_7mm", "7 mm slab")):
        ax.plot(x, _profile_values(result, field), label=label, linewidth=1.4)
    ax.set_xlabel("Distance from top [mm]")
    ax.set_ylabel("Equivalent diameter [mm]")
    ax.set_title("Stem diameter profile")
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


def write_stem_diameter_outputs(output_dir, result, input_path: str, point_count: int):
    """Regenerate only the four named analysis outputs; preserve other user files."""
    target = Path(output_dir)
    target.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".stem-diameter-", dir=target) as staging:
        stage = Path(staging)
        _write_json(stage / "stem_diameter.json", result, input_path, point_count)
        _write_csv(stage / "stem_diameter.csv", result)
        _write_plots(stage, result)
        for filename in OUTPUT_NAMES:
            os.replace(stage / filename, target / filename)
