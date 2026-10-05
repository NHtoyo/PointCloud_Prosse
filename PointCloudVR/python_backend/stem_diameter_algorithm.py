from __future__ import annotations

from dataclasses import dataclass
from typing import Callable, Optional
import math

import numpy as np
from scipy.interpolate import UnivariateSpline
from scipy.ndimage import median_filter
from scipy.spatial import cKDTree


@dataclass
class StemDiameterParams:
    measurement_interval_mm: float = 10.0
    centerline_step_mm: float = 5.0
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


@dataclass
class SectionResult:
    index: int
    position_mm: float
    center_xyz_units: np.ndarray
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


@dataclass
class StemDiameterAnalysisResult:
    scale_mm_per_unit: float
    parameters: StemDiameterParams
    centerline_length_mm: float
    centerline_support_points_units: np.ndarray
    centerline_display_points_units: np.ndarray
    sections: list[SectionResult]


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


def _build_centerline(points_mm: np.ndarray, params: StemDiameterParams):
    global_center = np.mean(points_mm, axis=0)
    global_axis, _ = _principal_axis(points_mm)
    proj = np.empty(len(points_mm), dtype=np.float64)
    chunk_size = 1 << 18
    for start in range(0, len(points_mm), chunk_size):
        end = min(start + chunk_size, len(points_mm))
        proj[start:end] = (points_mm[start:end] - global_center) @ global_axis

    step = params.centerline_step_mm
    proj_min = float(proj.min())
    span = float(proj.max() - proj_min)
    bin_count = max(1, int(math.ceil(span / step)))
    bin_index = np.floor((proj - proj_min) / step).astype(np.int32)
    np.clip(bin_index, 0, bin_count - 1, out=bin_index)
    order = np.argsort(bin_index, kind="quicksort")
    sorted_bins = bin_index[order]
    starts = np.r_[0, np.flatnonzero(np.diff(sorted_bins)) + 1]
    ends = np.r_[starts[1:], len(sorted_bins)]
    support_t, support_points = [], []

    for start, end in zip(starts, ends):
        if int(end - start) < params.min_centerline_bin_points:
            continue
        current_bin = int(sorted_bins[start])
        support_points.append(np.median(points_mm[order[start:end]], axis=0))
        support_t.append(proj_min + (current_bin + 0.5) * step)

    if len(support_t) < 4:
        raise ValueError("Too few centerline support points.")

    support_t = np.asarray(support_t, dtype=float)
    support_points = np.asarray(support_points, dtype=float)
    order = np.argsort(support_t)
    return support_t[order], support_points[order], global_axis


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
        return SliceResult(thickness_mm, None, None, raw_count, 0, None, None, None, None, None, None, None, "insufficient_points")

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
            "insufficient_points_after_prefilter",
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
            None, None, None, "insufficient_angular_support",
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
    if area <= 0 or not np.isfinite(area):
        return SliceResult(
            thickness_mm, None, None, raw_count, used_count,
            1.0 - used_count / max(raw_count, 1),
            coverage, max_gap_deg, interpolated_fraction,
            None, None, contour, "numerical_failure",
        )

    deq = 2.0 * math.sqrt(area / math.pi)
    perimeter = _polygon_perimeter(contour)
    circularity = None if perimeter <= 0 else float(4.0 * math.pi * area / (perimeter * perimeter))

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
    )


def analyze_stem(
    points_xyz,
    scale_mm_per_unit,
    params=None,
    progress_callback: Optional[Callable[[float, str], None]] = None,
):
    """Analyze a stem point cloud while keeping all distances in physical millimeters.

    The prototype's numerical sequence is retained. A single spatial tree limits
    each local PCA and slice to nearby points instead of rescanning the full cloud
    for every section.
    """
    if params is None:
        params = StemDiameterParams()

    points_xyz = np.asarray(points_xyz, dtype=np.float64)
    scale_mm_per_unit = float(scale_mm_per_unit)
    if points_xyz.ndim != 2 or points_xyz.shape[1] != 3:
        raise ValueError("points_xyz must have shape (N, 3).")
    if len(points_xyz) < 100:
        raise ValueError("Too few points.")
    if not np.all(np.isfinite(points_xyz)):
        raise ValueError("points_xyz contains NaN or inf.")
    if not np.isfinite(scale_mm_per_unit) or scale_mm_per_unit <= 0:
        raise ValueError("scale_mm_per_unit must be finite and > 0.")
    if params.measurement_interval_mm <= 0 or params.centerline_step_mm <= 0:
        raise ValueError("measurement_interval_mm and centerline_step_mm must be > 0.")
    if params.local_axis_radius_mm <= 0 or params.slice_roi_radius_mm < params.local_axis_radius_mm:
        raise ValueError("slice_roi_radius_mm must be >= local_axis_radius_mm > 0.")
    if not params.slice_thicknesses_mm or any(h <= 0 for h in params.slice_thicknesses_mm):
        raise ValueError("slice_thicknesses_mm must contain positive values.")
    if params.primary_slice_thickness_mm not in params.slice_thicknesses_mm:
        raise ValueError("primary_slice_thickness_mm must be present in slice_thicknesses_mm.")
    if params.angular_bins < 4 or params.query_batch_size < 1:
        raise ValueError("angular_bins must be >= 4 and query_batch_size must be >= 1.")
    if params.query_workers == 0 or params.query_workers < -1:
        raise ValueError("query_workers must be -1 or a positive worker count.")
    if not 0.0 <= params.radial_percentile <= 100.0:
        raise ValueError("radial_percentile must be between 0 and 100.")
    if params.centerline_smoothing < 0 or params.centerline_dense_samples < 2:
        raise ValueError("centerline_smoothing must be >= 0 and centerline_dense_samples >= 2.")
    if min(params.min_centerline_bin_points, params.min_local_axis_points,
           params.min_slice_points, params.min_occupied_angular_bins) < 1:
        raise ValueError("Minimum support-point counts must be positive.")
    if params.min_occupied_angular_bins > params.angular_bins:
        raise ValueError("min_occupied_angular_bins cannot exceed angular_bins.")

    def report_progress(value, message):
        if progress_callback is not None:
            progress_callback(float(value), message)

    report_progress(0.05, "主茎の大まかな軸と中心線支持点を推定中...")
    points_mm = points_xyz * scale_mm_per_unit
    del points_xyz
    support_t, support_points_mm, _ = _build_centerline(points_mm, params)
    splines = _fit_centerline(support_t, support_points_mm, params)
    report_progress(0.12, "中心線を弧長パラメータ化中...")
    sample_s, sample_t, _, curve_mm, total_length_mm = _arc_length_sampling(
        splines, support_t.min(), support_t.max(), params
    )

    # Unity convention: the endpoint with larger Y is the top and position 0.
    if curve_mm[0, 1] < curve_mm[-1, 1]:
        sample_t = sample_t[::-1]
        curve_mm = curve_mm[::-1]
        support_points_mm = support_points_mm[::-1]
        sample_s = np.arange(len(sample_t), dtype=float) * params.measurement_interval_mm

    centers_mm = _eval_curve(splines, sample_t)
    sections = []
    report_progress(0.14, "近傍探索用の空間インデックスを構築中...")
    tree = cKDTree(points_mm, compact_nodes=True, balanced_tree=True)
    section_count = len(sample_t)
    batch_size = max(1, int(params.query_batch_size))

    for batch_start in range(0, section_count, batch_size):
        batch_end = min(batch_start + batch_size, section_count)
        nearby_indices = tree.query_ball_point(
            centers_mm[batch_start:batch_end],
            r=float(params.slice_roi_radius_mm),
            workers=int(params.query_workers),
            return_sorted=False,
        )

        for local_i, raw_indices in enumerate(nearby_indices):
            i = batch_start + local_i
            position_mm = sample_s[i]
            center_mm = centers_mm[i]
            tangent = _eval_tangent(splines, float(sample_t[i]))
            if i + 1 < section_count:
                direction = centers_mm[i + 1] - center_mm
            elif i > 0:
                direction = center_mm - centers_mm[i - 1]
            else:
                direction = tangent
            if np.dot(tangent, direction) < 0:
                tangent = -tangent

            candidate_indices = np.asarray(raw_indices, dtype=np.int64)
            nearby_points = points_mm[candidate_indices]
            local_rel = nearby_points - center_mm
            local_dist2 = np.einsum("ij,ij->i", local_rel, local_rel)
            local_points = nearby_points[local_dist2 <= params.local_axis_radius_mm ** 2]

            if len(local_points) < params.min_local_axis_points:
                sections.append(
                    SectionResult(
                        i, float(position_mm), center_mm / scale_mm_per_unit, tangent,
                        None, None, None, None, None, None, [],
                        None, None, None, None, None, None, None,
                        "insufficient_local_axis_points",
                    )
                )
                continue

            # A local PCA axis avoids slanted cuts on curved stems; the centerline
            # tangent still determines where to measure and orients the PCA axis.
            local_axis, eigvals = _principal_axis(local_points)
            if np.dot(local_axis, tangent) < 0:
                local_axis = -local_axis
            cosang = float(np.clip(np.dot(local_axis, tangent), -1.0, 1.0))
            axis_angle = float(np.degrees(np.arccos(cosang)))
            linearity = None if eigvals[0] <= 1e-12 else float((eigvals[0] - eigvals[1]) / eigvals[0])
            basis_u, basis_v = _make_section_basis(local_axis)

            slice_results = [
                _slice_measurement(
                    nearby_points, center_mm, local_axis, basis_u, basis_v,
                    float(thickness), params
                )
                for thickness in params.slice_thicknesses_mm
            ]
            by_thickness = {round(r.thickness_mm, 6): r for r in slice_results}

            def diameter_at(thickness):
                result = by_thickness.get(round(thickness, 6))
                return None if result is None else result.equivalent_diameter_mm

            primary = by_thickness.get(round(params.primary_slice_thickness_mm, 6))
            valid_diameters = [
                result.equivalent_diameter_mm for result in slice_results
                if result.equivalent_diameter_mm is not None
            ]
            diameter_range = (
                float(np.max(valid_diameters) - np.min(valid_diameters))
                if len(valid_diameters) >= 2 else None
            )
            diameter_std = float(np.std(valid_diameters)) if len(valid_diameters) >= 2 else None

            sections.append(
                SectionResult(
                    index=i,
                    position_mm=float(position_mm),
                    center_xyz_units=center_mm / scale_mm_per_unit,
                    centerline_tangent_xyz=tangent,
                    local_axis_xyz=local_axis,
                    basis_u_xyz=basis_u,
                    basis_v_xyz=basis_v,
                    local_axis_angle_deg=axis_angle,
                    local_pca_eigenvalues=eigvals,
                    local_pca_linearity=linearity,
                    slice_results=slice_results,
                    equivalent_diameter_mm=None if primary is None else primary.equivalent_diameter_mm,
                    cross_section_area_mm2=None if primary is None else primary.area_mm2,
                    diameter_3mm=diameter_at(3.0),
                    diameter_5mm=diameter_at(5.0),
                    diameter_7mm=diameter_at(7.0),
                    slice_diameter_range_mm=diameter_range,
                    slice_diameter_std_mm=diameter_std,
                    calculation_status=(
                        "ok" if primary and primary.equivalent_diameter_mm is not None
                        else ("no_primary_slice_result" if primary is None else primary.calculation_status)
                    ),
                )
            )

        report_progress(
            0.15 + 0.75 * batch_end / max(section_count, 1),
            f"断面を解析中... {batch_end}/{section_count}",
        )

    return StemDiameterAnalysisResult(
        scale_mm_per_unit=scale_mm_per_unit,
        parameters=params,
        centerline_length_mm=float(total_length_mm),
        centerline_support_points_units=support_points_mm / scale_mm_per_unit,
        centerline_display_points_units=curve_mm / scale_mm_per_unit,
        sections=sections,
    )
