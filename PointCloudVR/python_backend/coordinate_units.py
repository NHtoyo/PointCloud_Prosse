"""Conversions used only at Python I/O boundaries between data-space and mm."""

from __future__ import annotations

import numpy as np


def validate_coordinate_scale_to_mm(scale: float) -> float:
    value = float(scale)
    if not np.isfinite(value) or value <= 0.0:
        raise ValueError("coordinate_scale_to_mm must be a finite positive number.")
    return value


def millimeters_to_data_length(length_mm: float, coordinate_scale_to_mm: float) -> float:
    scale = validate_coordinate_scale_to_mm(coordinate_scale_to_mm)
    length = float(length_mm)
    if not np.isfinite(length):
        raise ValueError("length_mm must be finite.")
    return length / scale


def convert_points_to_mm_in_place(points: np.ndarray, coordinate_scale_to_mm: float) -> np.ndarray:
    scale = validate_coordinate_scale_to_mm(coordinate_scale_to_mm)
    points_mm = np.asarray(points)
    if points_mm.ndim != 2 or points_mm.shape[1] != 3:
        raise ValueError(f"Point coordinates must be an Nx3 array; shape={points_mm.shape}")
    if not np.issubdtype(points_mm.dtype, np.floating):
        points_mm = points_mm.astype(np.float32)
    elif not points_mm.flags.writeable:
        points_mm = points_mm.copy()
    points_mm *= scale
    if not np.all(np.isfinite(points_mm)):
        raise ValueError("Converted point coordinates contain NaN or inf.")
    return points_mm


def convert_points_from_mm_to_data(points_mm: np.ndarray, coordinate_scale_to_mm: float) -> np.ndarray:
    scale = validate_coordinate_scale_to_mm(coordinate_scale_to_mm)
    return np.asarray(points_mm) / scale
