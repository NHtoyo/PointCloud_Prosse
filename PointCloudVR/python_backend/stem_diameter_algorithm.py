from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable, Optional
import math

import numpy as np
from scipy.interpolate import UnivariateSpline
from scipy.ndimage import median_filter
from scipy.spatial import cKDTree

from pointcloud_components import ConnectedComponentError, largest_connected_component


DiagnosticCallback = Callable[[str], None] | None


class StemDiameterStageError(ValueError):
    def __init__(self, stage: str, message: str, parameters=None, diagnostics=None):
        super().__init__(message)
        self.stage = stage
        self.parameters = parameters or {}
        self.diagnostics = diagnostics or {}


@dataclass
class StemDiameterParams:
    measurement_interval_mm: float = 10.0
    centerline_step_mm: float = 5.0
    centerline_axis_mode: str = "pca"
    centerline_model: str = "polyline"
    use_largest_component: bool = True
    component_knn_k: int = 8
    component_alpha: float = 2.5
    local_axis_radius_mm: float = 15.0
    slice_thicknesses_mm: tuple[float, ...] = (3.0, 5.0, 7.0)
    primary_slice_thickness_mm: float = 5.0
    angular_bins: int = 72
    radial_percentile: float = 90.0
    polar_median_filter_size: int = 5
    min_centerline_bin_points: int = 30
    min_local_axis_points: int = 30
    min_slice_points: int = 40
    min_occupied_angular_bins: int = 3
    slice_roi_radius_mm: float = 30.0
    radial_mad_sigma: float = 3.0
    radial_extra_mm: float = 1.5
    centerline_smoothing: float = 10.0
    centerline_dense_samples: int = 1000
    query_workers: int = -1
    query_batch_size: int = 32


@dataclass
class SliceResult:
    thickness_mm: float
    equivalent_diameter_mm: Optional[float]
    area_mm2: Optional[float]
    raw_point_count: int
    used_point_count: int
    outlier_fraction: Optional[float]
    angular_coverage: Optional[float]
    max_gap_deg: Optional[float]
    interpolated_fraction: Optional[float]
    shape_axis_ratio: Optional[float]
    circularity: Optional[float]
    contour_uv_mm: Optional[np.ndarray]
    calculation_status: str
    occupied_angular_bins: int = 0
    required_angular_bins: int = 0
    failure_detail: Optional[str] = None
    perimeter_mm: Optional[float] = None


@dataclass
class SectionResult:
    index: int
    position_mm: float
    center_xyz_mm: np.ndarray
    centerline_tangent_xyz: np.ndarray
    local_axis_xyz: Optional[np.ndarray]
    basis_u_xyz: Optional[np.ndarray]
    basis_v_xyz: Optional[np.ndarray]
    local_axis_angle_deg: Optional[float]
    local_pca_eigenvalues: Optional[np.ndarray]
    local_pca_linearity: Optional[float]
    slice_results: list[SliceResult]
    equivalent_diameter_mm: Optional[float]
    cross_section_area_mm2: Optional[float]
    diameter_3mm: Optional[float]
    diameter_5mm: Optional[float]
    diameter_7mm: Optional[float]
    slice_diameter_range_mm: Optional[float]
    slice_diameter_std_mm: Optional[float]
    calculation_status: str
    actual_local_axis_point_count: int = 0
    required_local_axis_point_count: int = 0
    failure_detail: Optional[str] = None
    perimeter_mm: Optional[float] = None


@dataclass
class StemDiameterAnalysisResult:
    parameters: StemDiameterParams
    centerline_length_mm: float
    centerline_support_points_mm: np.ndarray
    centerline_display_points_mm: np.ndarray
    sections: list[SectionResult]
    diagnostics: dict = field(default_factory=dict)
    warnings: list[str] = field(default_factory=list)


def _unit(v: np.ndarray) -> np.ndarray:
    n = float(np.linalg.norm(v))
    if n < 1e-12:
        raise ValueError("Cannot normalize a near-zero vector.")
    return v / n


def _principal_axis(points: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    center = np.mean(points, axis=0)
    scatter = np.zeros((3, 3), dtype=np.float64)
    chunk_size = 1 << 18
    for start in range(0, len(points), chunk_size):
        q = points[start:start + chunk_size] - center
        scatter += q.T @ q
    cov = scatter / max(len(points) - 1, 1)
    eigvals, eigvecs = np.linalg.eigh(cov)
    order = np.argsort(eigvals)[::-1]
    eigvals = eigvals[order]
    axis = eigvecs[:, order[0]]
    return _unit(axis), eigvals


def _build_centerline_support(points_mm: np.ndarray, projection: np.ndarray,
                              params: StemDiameterParams, diagnostic_callback: DiagnosticCallback):
    step = params.centerline_step_mm
    proj_min = float(projection.min())
    span = float(projection.max() - proj_min)
    bin_count = max(1, int(math.ceil(span / step)))
    bin_index = np.floor((projection - proj_min) / step).astype(np.int64)
    np.clip(bin_index, 0, bin_count - 1, out=bin_index)
    order = np.argsort(bin_index, kind="quicksort")
    sorted_bins = bin_index[order]
    starts = np.r_[0, np.flatnonzero(np.diff(sorted_bins)) + 1]
    ends = np.r_[starts[1:], len(sorted_bins)]
    support_t, support_points = [], []
    bin_counts = ends - starts
    valid_bin_mask = bin_counts >= params.min_centerline_bin_points

    for start, end in zip(starts, ends):
        if int(end - start) < params.min_centerline_bin_points:
            continue
        current_bin = int(sorted_bins[start])
        support_points.append(np.median(points_mm[order[start:end]], axis=0))
        support_t.append(proj_min + (current_bin + 0.5) * step)

    if len(support_t) < 4:
        diagnostics = {
            "axis_mode": params.centerline_axis_mode,
            "input_points": int(len(points_mm)),
            "projection_min_mm": proj_min,
            "projection_span_mm": span,
            "total_bins": int(bin_count),
            "occupied_bins": int(len(starts)),
            "valid_support_bins": int(len(support_t)),
            "rejected_low_count_bins": int(len(starts) - np.count_nonzero(valid_bin_mask)),
            "required_support_points": 4,
            "min_centerline_bin_points": int(params.min_centerline_bin_points),
            "centerline_step_mm": float(step),
            "min_bin_point_count": int(np.min(bin_counts)) if len(bin_counts) else 0,
            "median_bin_point_count": float(np.median(bin_counts)) if len(bin_counts) else 0.0,
            "max_bin_point_count": int(np.max(bin_counts)) if len(bin_counts) else 0,
        }
        _emit_stage(diagnostic_callback, "FAIL", "CENTERLINE_BINNING", diagnostics)
        raise StemDiameterStageError(
            "CENTERLINE_BINNING",
            f"CENTERLINE_BINNING failed: only {len(support_t)} valid support bins were produced; "
            f"at least 4 are required. axis_mode={params.centerline_axis_mode}, "
            f"centerline_step_mm={step}, min_centerline_bin_points={params.min_centerline_bin_points}, "
            f"input_points={len(points_mm)}, total_bins={bin_count}.",
            _params_snapshot(params), diagnostics,
        )

    support_t = np.asarray(support_t, dtype=float)
    support_points = np.asarray(support_points, dtype=float)
    order = np.argsort(support_t)
    diagnostics = {
        "input_points": int(len(points_mm)),
        "projection_min_mm": proj_min,
        "projection_span_mm": span,
        "total_bins": int(bin_count),
        "occupied_bins": int(len(starts)),
        "valid_support_bins": int(len(support_t)),
        "rejected_low_count_bins": int(len(starts) - np.count_nonzero(valid_bin_mask)),
        "min_bin_point_count": int(np.min(bin_counts)) if len(bin_counts) else 0,
        "median_bin_point_count": float(np.median(bin_counts)) if len(bin_counts) else 0.0,
        "max_bin_point_count": int(np.max(bin_counts)) if len(bin_counts) else 0,
        "centerline_step_mm": float(step),
        "min_centerline_bin_points": int(params.min_centerline_bin_points),
        "support_point_count": int(len(support_t)),
    }
    _emit_stage(diagnostic_callback, "OK", "CENTERLINE_BINNING", diagnostics)
    return support_t[order], support_points[order], diagnostics


def _emit_stage(callback: DiagnosticCallback, state: str, stage: str, fields: dict):
    if callback is None:
        return
    ordered = " ".join(f"{key}={_log_value(value)}" for key, value in fields.items())
    callback(f"[StemDiameter][STAGE][{state}] {stage}" + (f" {ordered}" if ordered else ""))


def _log_value(value):
    if value is None:
        return "null"
    if isinstance(value, (float, np.floating)):
        return f"{float(value):.8g}"
    if isinstance(value, (list, tuple, np.ndarray)):
        return "[" + ",".join(_log_value(item) for item in value) + "]"
    return str(value).replace(" ", "_")


def _params_snapshot(params: StemDiameterParams):
    return {
        "centerline_axis_mode": params.centerline_axis_mode,
        "centerline_model": params.centerline_model,
        "use_largest_component": bool(params.use_largest_component),
        "component_knn_k": int(params.component_knn_k),
        "component_alpha": float(params.component_alpha),
        "centerline_step_mm": float(params.centerline_step_mm),
        "min_centerline_bin_points": int(params.min_centerline_bin_points),
    }


def _estimate_centerline_axis(points_mm: np.ndarray, mode: str):
    if mode == "pca":
        center = np.mean(points_mm, axis=0)
        axis, eigenvalues = _principal_axis(points_mm)
        projection = np.empty(len(points_mm), dtype=np.float64)
        chunk_size = 1 << 18
        for start in range(0, len(points_mm), chunk_size):
            end = min(start + chunk_size, len(points_mm))
            projection[start:end] = (points_mm[start:end] - center) @ axis
        return axis, projection, {"eigenvalues_mm2": eigenvalues, "principal_axis_xyz": axis}
    axis = np.array([0.0, 1.0, 0.0], dtype=np.float64)
    return axis, points_mm[:, 1].copy(), {"axis_xyz": axis}


def _polyline_segment_diagnostics(points_mm: np.ndarray):
    segment_lengths = np.linalg.norm(np.diff(points_mm, axis=0), axis=1)
    if len(segment_lengths) == 0 or not np.all(np.isfinite(segment_lengths)):
        raise ValueError("polyline must contain at least two finite support points")
    if np.any(segment_lengths <= 1e-12):
        raise ValueError("polyline contains a zero-length segment")
    median = float(np.median(segment_lengths))
    maximum = float(np.max(segment_lengths))
    ratio = maximum / median if median > 0 else float("inf")
    return segment_lengths, {
        "segment_count": int(len(segment_lengths)),
        "min_segment_length_mm": float(np.min(segment_lengths)),
        "median_segment_length_mm": median,
        "max_segment_length_mm": maximum,
        "max_median_segment_ratio": ratio,
    }


def _sample_polyline(points_mm: np.ndarray, params: StemDiameterParams):
    segment_lengths, diagnostics = _polyline_segment_diagnostics(points_mm)
    cumulative = np.r_[0.0, np.cumsum(segment_lengths)]
    total = float(cumulative[-1])
    if not np.isfinite(total) or total <= 1e-9:
        raise ValueError("polyline length is numerically zero")
    sample_s = np.arange(0.0, total + 1e-9, params.measurement_interval_mm)
    if len(sample_s) == 0:
        sample_s = np.array([0.0])
    segment_index = np.searchsorted(cumulative[1:], sample_s, side="right")
    segment_index = np.minimum(segment_index, len(segment_lengths) - 1)
    fraction = (sample_s - cumulative[segment_index]) / segment_lengths[segment_index]
    centers = points_mm[segment_index] + fraction[:, None] * (
        points_mm[segment_index + 1] - points_mm[segment_index]
    )
    tangents = (points_mm[segment_index + 1] - points_mm[segment_index]) / segment_lengths[segment_index, None]
    diagnostics["centerline_length_mm"] = total
    return sample_s, centers, tangents, total, diagnostics


def _fit_centerline(support_t, support_points_mm, params):
    s = float(params.centerline_smoothing)
    sx = UnivariateSpline(support_t, support_points_mm[:, 0], s=s)
    sy = UnivariateSpline(support_t, support_points_mm[:, 1], s=s)
    sz = UnivariateSpline(support_t, support_points_mm[:, 2], s=s)
    return sx, sy, sz


def _eval_curve(splines, t):
    sx, sy, sz = splines
    if np.ndim(t):
        return np.column_stack([sx(t), sy(t), sz(t)])
    return np.array([sx(t), sy(t), sz(t)], dtype=float)


def _eval_tangent(splines, t):
    sx, sy, sz = splines
    d = np.array([sx.derivative()(t), sy.derivative()(t), sz.derivative()(t)], dtype=float)
    return _unit(d)


def _arc_length_sampling(splines, t_min, t_max, params):
    fine_t = np.linspace(t_min, t_max, params.centerline_dense_samples)
    curve_mm = _eval_curve(splines, fine_t)
    seg = np.linalg.norm(np.diff(curve_mm, axis=0), axis=1)
    arc = np.r_[0.0, np.cumsum(seg)]
    total = float(arc[-1])
    sample_s = np.arange(0.0, total + 1e-9, params.measurement_interval_mm)
    sample_t = np.interp(sample_s, arc, fine_t)
    return sample_s, sample_t, fine_t, curve_mm, total


def _make_section_basis(axis):
    ref = np.array([0.0, 0.0, 1.0])
    if abs(float(np.dot(ref, axis))) > 0.9:
        ref = np.array([1.0, 0.0, 0.0])
    u = _unit(np.cross(axis, ref))
    v = _unit(np.cross(axis, u))
    return u, v


def _circular_interpolate(values):
    values = np.asarray(values, dtype=float)
    n = len(values)
    valid = np.where(np.isfinite(values))[0]
    if len(valid) < 2:
        return values.copy()
    x = valid.astype(float)
    y = values[valid]
    x_ext = np.r_[x - n, x, x + n]
    y_ext = np.r_[y, y, y]
    return np.interp(np.arange(n, dtype=float), x_ext, y_ext)


def _max_circular_gap_bins(occupied):
    occupied = np.asarray(occupied, dtype=bool)
    n = len(occupied)
    if not np.any(occupied):
        return n
    if np.all(occupied):
        return 0
    empty = ~occupied
    doubled = np.r_[empty, empty]
    best = cur = 0
    for x in doubled:
        if x:
            cur += 1
            best = max(best, cur)
        else:
            cur = 0
    return min(best, n)


def _polygon_area(poly):
    x = poly[:, 0]
    y = poly[:, 1]
    return 0.5 * abs(float(np.dot(x, np.roll(y, -1)) - np.dot(y, np.roll(x, -1))))


def _polygon_perimeter(poly):
    q = np.vstack([poly, poly[0]])
    return float(np.sum(np.linalg.norm(np.diff(q, axis=0), axis=1)))


def _shape_axis_ratio(poly):
    if len(poly) < 3:
        return None
    q = poly - np.mean(poly, axis=0)
    vals = np.linalg.eigvalsh(np.cov(q.T))
    vals = np.sort(vals)
    if vals[-1] <= 1e-12:
        return None
    return float(math.sqrt(max(vals[0], 0.0) / vals[-1]))


def _slice_measurement(points_mm, center_mm, axis, basis_u, basis_v, thickness_mm, params):
    rel = points_mm - center_mm
    axial = rel @ axis
    slab = np.abs(axial) <= thickness_mm * 0.5
    q = rel[slab]
    raw_count = int(len(q))

    if raw_count < params.min_slice_points:
        return SliceResult(
            thickness_mm, None, None, raw_count, 0, None, None, None, None, None, None, None,
            "insufficient_points", 0, params.min_occupied_angular_bins,
            f"raw_point_count={raw_count}; required_point_count={params.min_slice_points}",
        )

    uv = np.column_stack([q @ basis_u, q @ basis_v])
    radii = np.linalg.norm(uv, axis=1)

    med = float(np.median(radii))
    mad = float(np.median(np.abs(radii - med))) + 1e-12
    tol = params.radial_mad_sigma * 1.4826 * mad + params.radial_extra_mm
    keep = np.abs(radii - med) <= tol
    uv_used = uv[keep]
    used_count = int(len(uv_used))

    if used_count < params.min_slice_points:
        return SliceResult(
            thickness_mm, None, None, raw_count, used_count,
            1.0 - used_count / max(raw_count, 1),
            None, None, None, None, None, None,
            "insufficient_points_after_prefilter", 0, params.min_occupied_angular_bins,
            f"used_point_count={used_count}; required_point_count={params.min_slice_points}",
        )

    theta = np.mod(np.arctan2(uv_used[:, 1], uv_used[:, 0]), 2.0 * np.pi)
    r = np.linalg.norm(uv_used, axis=1)
    n_bins = params.angular_bins
    bin_idx = np.floor(theta / (2.0 * np.pi) * n_bins).astype(int)
    bin_idx = np.clip(bin_idx, 0, n_bins - 1)

    counts = np.bincount(bin_idx, minlength=n_bins)
    occupied_indices = np.flatnonzero(counts)
    representative_r = np.full(n_bins, np.nan, dtype=float)
    if len(occupied_indices):
        # Sort radius within each angular bin once. This is equivalent to NumPy's
        # linear percentile interpolation, without 72 Python-level percentile calls.
        order = np.lexsort((r, bin_idx))
        ordered_r = r[order]
        starts = np.cumsum(counts) - counts
        ranks = (counts[occupied_indices] - 1) * (params.radial_percentile / 100.0)
        lower_rank = np.floor(ranks).astype(np.int64)
        upper_rank = np.ceil(ranks).astype(np.int64)
        fraction = ranks - lower_rank
        lower = ordered_r[starts[occupied_indices] + lower_rank]
        upper = ordered_r[starts[occupied_indices] + upper_rank]
        representative_r[occupied_indices] = lower + (upper - lower) * fraction

    occupied = counts > 0
    occupied_count = int(np.count_nonzero(occupied))

    if occupied_count < params.min_occupied_angular_bins:
        return SliceResult(
            thickness_mm, None, None, raw_count, used_count,
            1.0 - used_count / max(raw_count, 1),
            occupied_count / n_bins, 360.0, 1.0 - occupied_count / n_bins,
            None, None, None, "insufficient_angular_support", occupied_count,
            params.min_occupied_angular_bins,
            f"occupied_angular_bins={occupied_count}; required_angular_bins={params.min_occupied_angular_bins}",
        )

    coverage = occupied_count / n_bins
    max_gap_deg = _max_circular_gap_bins(occupied) * (360.0 / n_bins)
    interpolated_fraction = 1.0 - coverage

    filled_r = _circular_interpolate(representative_r)
    k = int(params.polar_median_filter_size)
    if k > 1:
        if k % 2 == 0:
            k += 1
        filled_r = median_filter(filled_r, size=k, mode="wrap")

    angles = (np.arange(n_bins) + 0.5) / n_bins * 2.0 * np.pi
    contour = np.column_stack([filled_r * np.cos(angles), filled_r * np.sin(angles)])

    area = _polygon_area(contour)
    perimeter = _polygon_perimeter(contour)
    if area <= 0 or not np.isfinite(area) or perimeter <= 0 or not np.isfinite(perimeter):
        return SliceResult(
            thickness_mm, None, None, raw_count, used_count,
            1.0 - used_count / max(raw_count, 1),
            coverage, max_gap_deg, interpolated_fraction,
            None, None, contour, "numerical_failure", occupied_count,
            params.min_occupied_angular_bins,
            f"invalid_contour_area_mm2={area}; perimeter_mm={perimeter}",
            perimeter_mm=float(perimeter) if np.isfinite(perimeter) else None,
        )

    deq = 2.0 * math.sqrt(area / math.pi)
    circularity = float(4.0 * math.pi * area / (perimeter * perimeter))

    return SliceResult(
        thickness_mm=float(thickness_mm),
        equivalent_diameter_mm=float(deq),
        area_mm2=float(area),
        raw_point_count=raw_count,
        used_point_count=used_count,
        outlier_fraction=float(1.0 - used_count / max(raw_count, 1)),
        angular_coverage=float(coverage),
        max_gap_deg=float(max_gap_deg),
        interpolated_fraction=float(interpolated_fraction),
        shape_axis_ratio=_shape_axis_ratio(contour),
        circularity=circularity,
        contour_uv_mm=contour,
        calculation_status="ok",
        occupied_angular_bins=occupied_count,
        required_angular_bins=params.min_occupied_angular_bins,
        perimeter_mm=float(perimeter),
    )


def analyze_stem(
    points_xyz,
    params=None,
    progress_callback: Optional[Callable[[float, str], None]] = None,
    diagnostic_callback: DiagnosticCallback = None,
):
    """Analyze points_xyz, which must already be expressed in millimeters.

    The prototype's numerical sequence is retained. A single spatial tree limits
    each local PCA and slice to nearby points instead of rescanning the full cloud
    for every section. This function performs no coordinate-scale conversion.
    """
    if params is None:
        params = StemDiameterParams()

    def report_progress(value, message):
        if progress_callback is not None:
            progress_callback(float(value), message)

    def emit(state, stage, fields):
        _emit_stage(diagnostic_callback, state, stage, fields)

    points_mm = np.asarray(points_xyz, dtype=np.float64)
    point_count = len(points_mm) if points_mm.ndim > 0 else 0
    params_json = _params_snapshot(params)
    emit("START", "INPUT", {"input_point_count": point_count})
    if points_mm.ndim != 2 or points_mm.shape[1] != 3:
        diagnostics = {"input_shape": list(points_mm.shape)}
        emit("FAIL", "INPUT", diagnostics)
        raise StemDiameterStageError("INPUT", "points_xyz must have shape (N, 3) and be in millimeters.",
                                     params_json, diagnostics)
    if len(points_mm) < 100:
        diagnostics = {"input_point_count": len(points_mm), "required_point_count": 100}
        emit("FAIL", "INPUT", diagnostics)
        raise StemDiameterStageError("INPUT", f"INPUT failed: {len(points_mm)} points; at least 100 are required.",
                                     params_json, diagnostics)
    if not np.all(np.isfinite(points_mm)):
        diagnostics = {"input_point_count": len(points_mm), "finite": False}
        emit("FAIL", "INPUT", diagnostics)
        raise StemDiameterStageError("INPUT", "points_xyz contains NaN or inf.", params_json, diagnostics)

    bounds_min = np.min(points_mm, axis=0)
    bounds_max = np.max(points_mm, axis=0)
    bounds_span = bounds_max - bounds_min
    bounding_diagonal = float(np.linalg.norm(bounds_span))
    input_diagnostics = {
        "input_point_count": int(len(points_mm)),
        "xyz_min_mm": bounds_min,
        "xyz_max_mm": bounds_max,
        "xyz_span_mm": bounds_span,
    }
    emit("OK", "INPUT", input_diagnostics)

    if params.centerline_axis_mode not in ("pca", "y"):
        raise StemDiameterStageError("INPUT", "centerline_axis_mode must be 'pca' or 'y'.", params_json)
    if params.centerline_model not in ("polyline", "spline"):
        raise StemDiameterStageError("INPUT", "centerline_model must be 'polyline' or 'spline'.", params_json)
    if params.measurement_interval_mm <= 0 or params.centerline_step_mm <= 0:
        raise StemDiameterStageError("INPUT", "measurement_interval_mm and centerline_step_mm must be > 0.", params_json)
    if params.local_axis_radius_mm <= 0 or params.slice_roi_radius_mm < params.local_axis_radius_mm:
        raise StemDiameterStageError("INPUT", "slice_roi_radius_mm must be >= local_axis_radius_mm > 0.", params_json)
    if not params.slice_thicknesses_mm or any(h <= 0 for h in params.slice_thicknesses_mm):
        raise StemDiameterStageError("INPUT", "slice_thicknesses_mm must contain positive values.", params_json)
    if params.primary_slice_thickness_mm not in params.slice_thicknesses_mm:
        raise StemDiameterStageError("INPUT", "primary_slice_thickness_mm must be present in slice_thicknesses_mm.", params_json)
    if params.angular_bins < 4 or params.query_batch_size < 1:
        raise StemDiameterStageError("INPUT", "angular_bins must be >= 4 and query_batch_size must be >= 1.", params_json)
    if params.query_workers == 0 or params.query_workers < -1:
        raise StemDiameterStageError("INPUT", "query_workers must be -1 or a positive worker count.", params_json)
    if not 0.0 <= params.radial_percentile <= 100.0:
        raise StemDiameterStageError("INPUT", "radial_percentile must be between 0 and 100.", params_json)
    if params.centerline_smoothing < 0 or params.centerline_dense_samples < 2:
        raise StemDiameterStageError("INPUT", "centerline_smoothing must be >= 0 and centerline_dense_samples >= 2.", params_json)
    if min(params.min_centerline_bin_points, params.min_local_axis_points,
           params.min_slice_points, params.min_occupied_angular_bins) < 1:
        raise StemDiameterStageError("INPUT", "Minimum support-point counts must be positive.", params_json)
    if params.min_occupied_angular_bins > params.angular_bins:
        raise StemDiameterStageError("INPUT", "min_occupied_angular_bins cannot exceed angular_bins.", params_json)

    diagnostics = {"input": input_diagnostics}
    warnings = []
    component_input_count = int(len(points_mm))
    median_knn_distance_mm = None
    connectivity_epsilon_mm = None
    emit("START", "CONNECTED_COMPONENT", {
        "input_points": component_input_count,
        "enabled": bool(params.use_largest_component),
        "K": params.component_knn_k,
        "alpha": params.component_alpha,
    })
    try:
        if params.component_knn_k < 1 or params.component_knn_k >= component_input_count:
            raise ConnectedComponentError(
                "knn_k must satisfy 1 <= K < input point count",
                input_point_count=component_input_count,
                knn_k=int(params.component_knn_k), alpha=float(params.component_alpha),
            )
        if not np.isfinite(params.component_alpha) or not 0 < params.component_alpha <= 10:
            raise ConnectedComponentError(
                "alpha must satisfy 0 < alpha <= 10",
                input_point_count=component_input_count,
                knn_k=int(params.component_knn_k), alpha=float(params.component_alpha),
            )
        if params.use_largest_component:
            component_result = largest_connected_component(
                points_mm, knn_k=params.component_knn_k, alpha=params.component_alpha,
                return_diagnostics=True,
            )
            analysis_points_mm = component_result.points
            median_knn_distance_mm = component_result.median_knn_distance
            connectivity_epsilon_mm = component_result.epsilon
            component_count = component_result.component_count
            component_point_count = int(len(component_result.indices))
        else:
            analysis_points_mm = points_mm
            component_count = None
            component_point_count = component_input_count
    except (ConnectedComponentError, TypeError, ValueError) as exc:
        error = exc if isinstance(exc, ConnectedComponentError) else None
        component_diagnostics = {
            "enabled": bool(params.use_largest_component),
            "input_points": component_input_count,
            "K": params.component_knn_k,
            "alpha": params.component_alpha,
            "median_knn_distance_mm": None if error is None else error.median_knn_distance,
            "epsilon_mm": None if error is None else error.epsilon,
            "component_count": None if error is None else error.component_count,
            "largest_component_points": None if error is None else error.largest_component_point_count,
        }
        emit("FAIL", "CONNECTED_COMPONENT", component_diagnostics)
        raise StemDiameterStageError("CONNECTED_COMPONENT", str(exc), params_json, component_diagnostics) from exc
    component_removed_count = component_input_count - component_point_count
    component_diagnostics = {
        "enabled": bool(params.use_largest_component),
        "input_points": component_input_count,
        "kept_points": component_point_count,
        "removed_points": component_removed_count,
        "removed_fraction": component_removed_count / max(component_input_count, 1),
        "K": int(params.component_knn_k),
        "alpha": float(params.component_alpha),
        "median_knn_distance_mm": median_knn_distance_mm,
        "epsilon_mm": connectivity_epsilon_mm,
        "component_count": component_count,
        "largest_component_points": component_point_count,
    }
    diagnostics["connected_component"] = component_diagnostics
    emit("OK", "CONNECTED_COMPONENT", component_diagnostics)

    emit("START", "AXIS_ESTIMATION", {"axis_mode": params.centerline_axis_mode,
                                          "input_points": len(analysis_points_mm)})
    try:
        centerline_axis, projection, axis_diagnostics = _estimate_centerline_axis(
            analysis_points_mm, params.centerline_axis_mode
        )
    except Exception as exc:
        axis_failure = {"axis_mode": params.centerline_axis_mode, "input_points": len(analysis_points_mm)}
        emit("FAIL", "AXIS_ESTIMATION", axis_failure)
        raise StemDiameterStageError("AXIS_ESTIMATION", str(exc), params_json, axis_failure) from exc
    axis_diagnostics = {"axis_mode": params.centerline_axis_mode, **axis_diagnostics}
    diagnostics["axis_estimation"] = axis_diagnostics
    emit("OK", "AXIS_ESTIMATION", axis_diagnostics)

    emit("START", "CENTERLINE_BINNING", {"axis_mode": params.centerline_axis_mode,
                                           "input_points": len(analysis_points_mm),
                                           "centerline_step_mm": params.centerline_step_mm,
                                           "min_centerline_bin_points": params.min_centerline_bin_points})
    support_t, support_points_mm, binning_diagnostics = _build_centerline_support(
        analysis_points_mm, projection, params, diagnostic_callback
    )
    diagnostics["centerline_binning"] = binning_diagnostics
    report_progress(0.05, "中心線支持点を生成中...")

    emit("START", "CENTERLINE_MODEL", {"model": params.centerline_model,
                                         "support_point_count": len(support_points_mm)})
    model_diagnostics = {"model": params.centerline_model, "support_point_count": len(support_points_mm)}
    try:
        splines = None
        if params.centerline_model == "polyline":
            display_points_mm = support_points_mm.copy()
            if display_points_mm[0, 1] < display_points_mm[-1, 1]:
                display_points_mm = display_points_mm[::-1].copy()
                support_points_mm = support_points_mm[::-1].copy()
            _, segment_diagnostics = _polyline_segment_diagnostics(display_points_mm)
            model_diagnostics.update(segment_diagnostics)
            ratio = segment_diagnostics["max_median_segment_ratio"]
            if ratio > 3.0:
                warning = ("unusually long centerline segment detected: "
                           f"max={segment_diagnostics['max_segment_length_mm']:.6g} mm, "
                           f"median={segment_diagnostics['median_segment_length_mm']:.6g} mm, "
                           f"max/median={ratio:.6g}")
                warnings.append(warning)
                if diagnostic_callback is not None:
                    diagnostic_callback("[StemDiameter][WARNING] " + warning.replace(" ", "_"))
        else:
            splines = _fit_centerline(support_t, support_points_mm, params)
            model_diagnostics["smoothing"] = float(params.centerline_smoothing)
    except Exception as exc:
        emit("FAIL", "CENTERLINE_MODEL", model_diagnostics)
        raise StemDiameterStageError("CENTERLINE_MODEL", str(exc), params_json, model_diagnostics) from exc
    diagnostics["centerline_model"] = model_diagnostics
    emit("OK", "CENTERLINE_MODEL", model_diagnostics)

    emit("START", "ARC_LENGTH_SAMPLING", {"model": params.centerline_model,
                                            "measurement_interval_mm": params.measurement_interval_mm})
    try:
        if params.centerline_model == "polyline":
            sample_s, centers_mm, sample_tangents, total_length_mm, arc_diagnostics = _sample_polyline(
                display_points_mm, params
            )
            centerline_display_points_mm = display_points_mm
        else:
            sample_s, sample_t, _, curve_mm, total_length_mm = _arc_length_sampling(
                splines, float(np.min(support_t)), float(np.max(support_t)), params
            )
            if not np.isfinite(total_length_mm) or total_length_mm <= 1e-9:
                raise ValueError(f"centerline length is invalid: {total_length_mm}")
            if curve_mm[0, 1] < curve_mm[-1, 1]:
                sample_t = sample_t[::-1]
                curve_mm = curve_mm[::-1]
                support_points_mm = support_points_mm[::-1].copy()
                sample_s = np.arange(len(sample_t), dtype=float) * params.measurement_interval_mm
            centers_mm = _eval_curve(splines, sample_t)
            sample_tangents = np.vstack([_eval_tangent(splines, float(value)) for value in sample_t])
            centerline_display_points_mm = curve_mm
            arc_diagnostics = {"centerline_length_mm": float(total_length_mm)}
        if len(centers_mm) == 0 or not np.all(np.isfinite(centers_mm)):
            raise ValueError("arc-length sampling produced no finite section centers")
        arc_diagnostics.update({
            "measurement_interval_mm": float(params.measurement_interval_mm),
            "section_count": int(len(centers_mm)),
        })
    except Exception as exc:
        arc_failure = {"model": params.centerline_model,
                       "support_point_count": len(support_points_mm),
                       "measurement_interval_mm": params.measurement_interval_mm}
        emit("FAIL", "ARC_LENGTH_SAMPLING", arc_failure)
        raise StemDiameterStageError("ARC_LENGTH_SAMPLING", str(exc), params_json, arc_failure) from exc
    diagnostics["arc_length_sampling"] = arc_diagnostics
    emit("OK", "ARC_LENGTH_SAMPLING", arc_diagnostics)

    if total_length_mm > bounding_diagonal * 3.0:
        warning = ("centerline length is unusually large relative to input cloud: "
                   f"centerline={total_length_mm:.6g} mm, bbox_diagonal={bounding_diagonal:.6g} mm")
        warnings.append(warning)
        if diagnostic_callback is not None:
            diagnostic_callback("[StemDiameter][WARNING] " + warning.replace(" ", "_"))

    emit("START", "SPATIAL_INDEX", {"indexed_point_count": len(analysis_points_mm),
                                     "slice_roi_radius_mm": params.slice_roi_radius_mm})
    try:
        tree = cKDTree(analysis_points_mm, compact_nodes=True, balanced_tree=True)
    except Exception as exc:
        spatial_failure = {"indexed_point_count": len(analysis_points_mm),
                           "slice_roi_radius_mm": params.slice_roi_radius_mm}
        emit("FAIL", "SPATIAL_INDEX", spatial_failure)
        raise StemDiameterStageError("SPATIAL_INDEX", str(exc), params_json, spatial_failure) from exc
    spatial_diagnostics = {"indexed_point_count": int(len(analysis_points_mm)),
                           "slice_roi_radius_mm": float(params.slice_roi_radius_mm)}
    diagnostics["spatial_index"] = spatial_diagnostics
    emit("OK", "SPATIAL_INDEX", spatial_diagnostics)

    sections = []
    section_count = len(centers_mm)
    batch_size = max(1, int(params.query_batch_size))
    local_axis_ok_count = 0
    insufficient_local_axis_count = 0
    insufficient_points_count = 0
    insufficient_after_prefilter_count = 0
    insufficient_angular_support_count = 0
    numerical_failure_count = 0
    primary_slice = float(params.primary_slice_thickness_mm)
    emit("START", "LOCAL_AXIS", {"section_count": section_count,
                                  "min_local_axis_points": params.min_local_axis_points})
    emit("START", "SLICE_MEASUREMENT", {"section_count": section_count,
                                         "radial_mad_sigma": params.radial_mad_sigma})
    report_progress(0.14, "近傍探索用の空間インデックスを構築しました。断面を解析中...")

    for batch_start in range(0, section_count, batch_size):
        batch_end = min(batch_start + batch_size, section_count)
        try:
            nearby_indices = tree.query_ball_point(
                centers_mm[batch_start:batch_end],
                r=float(params.slice_roi_radius_mm),
                workers=int(params.query_workers),
                return_sorted=False,
            )
        except Exception as exc:
            query_failure = {"section_start": batch_start, "section_end": batch_end,
                             "indexed_point_count": len(analysis_points_mm),
                             "slice_roi_radius_mm": params.slice_roi_radius_mm}
            emit("FAIL", "SPATIAL_INDEX", query_failure)
            raise StemDiameterStageError("SPATIAL_INDEX", str(exc), params_json, query_failure) from exc

        for local_i, raw_indices in enumerate(nearby_indices):
            i = batch_start + local_i
            position_mm = float(sample_s[i])
            center_mm = centers_mm[i]
            tangent = sample_tangents[i].copy()
            if i + 1 < section_count:
                direction = centers_mm[i + 1] - center_mm
            elif i > 0:
                direction = center_mm - centers_mm[i - 1]
            else:
                direction = tangent
            if np.dot(tangent, direction) < 0:
                tangent = -tangent

            candidate_indices = np.asarray(raw_indices, dtype=np.int64)
            nearby_points = analysis_points_mm[candidate_indices]
            local_rel = nearby_points - center_mm
            local_dist2 = np.einsum("ij,ij->i", local_rel, local_rel)
            local_points = nearby_points[local_dist2 <= params.local_axis_radius_mm ** 2]
            actual_local_count = int(len(local_points))

            if actual_local_count < params.min_local_axis_points:
                insufficient_local_axis_count += 1
                sections.append(SectionResult(
                    i, position_mm, center_mm, tangent,
                    None, None, None, None, None, None, [],
                    None, None, None, None, None, None, None,
                    "insufficient_local_axis_points",
                    actual_local_count, int(params.min_local_axis_points),
                    f"actual_local_axis_point_count={actual_local_count}; "
                    f"required_local_axis_point_count={params.min_local_axis_points}",
                ))
                continue

            try:
                local_axis, eigvals = _principal_axis(local_points)
                if np.dot(local_axis, tangent) < 0:
                    local_axis = -local_axis
                cosang = float(np.clip(np.dot(local_axis, tangent), -1.0, 1.0))
                axis_angle = float(np.degrees(np.arccos(cosang)))
                linearity = None if eigvals[0] <= 1e-12 else float((eigvals[0] - eigvals[1]) / eigvals[0])
                basis_u, basis_v = _make_section_basis(local_axis)
            except Exception as exc:
                numerical_failure_count += 1
                sections.append(SectionResult(
                    i, position_mm, center_mm, tangent,
                    None, None, None, None, None, None, [],
                    None, None, None, None, None, None, None,
                    "numerical_failure", actual_local_count, int(params.min_local_axis_points), str(exc),
                ))
                continue
            local_axis_ok_count += 1

            slice_results = []
            for thickness in params.slice_thicknesses_mm:
                try:
                    item = _slice_measurement(
                        nearby_points, center_mm, local_axis, basis_u, basis_v,
                        float(thickness), params,
                    )
                except Exception as exc:
                    item = SliceResult(
                        float(thickness), None, None, len(nearby_points), 0, None,
                        None, None, None, None, None, None, "numerical_failure",
                        0, int(params.min_occupied_angular_bins), str(exc),
                    )
                slice_results.append(item)

            primary_result = next(
                (item for item in slice_results if math.isclose(item.thickness_mm, primary_slice)), None
            )
            if primary_result is not None:
                if primary_result.calculation_status == "insufficient_points":
                    insufficient_points_count += 1
                elif primary_result.calculation_status == "insufficient_points_after_prefilter":
                    insufficient_after_prefilter_count += 1
                elif primary_result.calculation_status == "insufficient_angular_support":
                    insufficient_angular_support_count += 1
                elif primary_result.calculation_status == "numerical_failure":
                    numerical_failure_count += 1

            by_thickness = {round(r.thickness_mm, 6): r for r in slice_results}

            def diameter_at(thickness):
                result = by_thickness.get(round(thickness, 6))
                return None if result is None else result.equivalent_diameter_mm

            valid_diameters = [item.equivalent_diameter_mm for item in slice_results
                               if item.equivalent_diameter_mm is not None]
            diameter_range = float(np.max(valid_diameters) - np.min(valid_diameters)) if len(valid_diameters) >= 2 else None
            diameter_std = float(np.std(valid_diameters)) if len(valid_diameters) >= 2 else None
            sections.append(SectionResult(
                index=i,
                position_mm=position_mm,
                center_xyz_mm=center_mm,
                centerline_tangent_xyz=tangent,
                local_axis_xyz=local_axis,
                basis_u_xyz=basis_u,
                basis_v_xyz=basis_v,
                local_axis_angle_deg=axis_angle,
                local_pca_eigenvalues=eigvals,
                local_pca_linearity=linearity,
                slice_results=slice_results,
                equivalent_diameter_mm=None if primary_result is None else primary_result.equivalent_diameter_mm,
                cross_section_area_mm2=None if primary_result is None else primary_result.area_mm2,
                perimeter_mm=None if primary_result is None else primary_result.perimeter_mm,
                diameter_3mm=diameter_at(3.0),
                diameter_5mm=diameter_at(5.0),
                diameter_7mm=diameter_at(7.0),
                slice_diameter_range_mm=diameter_range,
                slice_diameter_std_mm=diameter_std,
                calculation_status=(
                    "ok" if primary_result and primary_result.equivalent_diameter_mm is not None
                    else ("no_primary_slice_result" if primary_result is None else primary_result.calculation_status)
                ),
                actual_local_axis_point_count=actual_local_count,
                required_local_axis_point_count=int(params.min_local_axis_points),
                failure_detail=None if primary_result is None or primary_result.calculation_status == "ok"
                else primary_result.failure_detail,
            ))

        report_progress(0.15 + 0.75 * batch_end / max(section_count, 1),
                        f"断面を解析中... {batch_end}/{section_count}")

    local_axis_summary = {
        "section_count": int(section_count),
        "local_axis_ok_count": int(local_axis_ok_count),
        "insufficient_local_axis_count": int(insufficient_local_axis_count),
        "min_local_axis_points": int(params.min_local_axis_points),
    }
    slice_summary = {
        "section_count": int(section_count),
        "primary_slice_thickness_mm": primary_slice,
        "valid_primary_slice_count": int(sum(s.calculation_status == "ok" for s in sections)),
        "insufficient_points_count": int(insufficient_points_count),
        "insufficient_after_prefilter_count": int(insufficient_after_prefilter_count),
        "insufficient_angular_support_count": int(insufficient_angular_support_count),
        "numerical_failure_count": int(numerical_failure_count),
        "radial_mad_sigma": float(params.radial_mad_sigma),
    }
    diagnostics["local_axis"] = local_axis_summary
    diagnostics["slice_measurement"] = slice_summary
    emit("OK", "LOCAL_AXIS", local_axis_summary)
    emit("OK", "SLICE_MEASUREMENT", slice_summary)
    return StemDiameterAnalysisResult(
        parameters=params,
        centerline_length_mm=float(total_length_mm),
        centerline_support_points_mm=support_points_mm,
        centerline_display_points_mm=centerline_display_points_mm,
        sections=sections,
        diagnostics=diagnostics,
        warnings=warnings,
    )
