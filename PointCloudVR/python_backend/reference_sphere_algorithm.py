from __future__ import annotations

from dataclasses import dataclass
from typing import Callable

import numpy as np
from numpy.fft import fft, ifft
from pointcloud_components import largest_connected_component


ProgressCallback = Callable[[float, str], None] | None


@dataclass
class ReferenceSphereResult:
    input_point_count: int
    component_point_count: int
    component_removed_count: int
    knn_k: int
    connectivity_alpha: float
    median_knn_distance: float
    connectivity_epsilon: float
    fit_inlier_count: int
    method_name: str
    center: np.ndarray
    radius: float
    diameter: float
    diameter_point1: np.ndarray
    diameter_point2: np.ndarray
    component_indices: np.ndarray
    fit_inlier_indices: np.ndarray


def _smallest_nonnegative_eigenvalue_index(eigenvalues: np.ndarray) -> int:
    values = np.asarray(eigenvalues, dtype=np.float64)
    if values.ndim != 1:
        raise ValueError("T eigenvalues must be a one-dimensional array")
    if not np.all(np.isfinite(values)):
        raise ValueError("T eigenvalues contain NaN or inf")
    candidates = np.flatnonzero(values >= 0.0)
    if len(candidates) == 0:
        raise ValueError("T has no non-negative eigenvalue for sphere fitting")
    return int(candidates[np.argmin(values[candidates])])


def _sphere_parameters_from_algebraic(
    mean: np.ndarray,
    coefficients: np.ndarray,
    coordinate_scale: float,
) -> tuple[np.ndarray, float]:
    center_mean = np.asarray(mean, dtype=np.float64)
    a = np.asarray(coefficients, dtype=np.float64)
    if center_mean.shape != (3,) or a.shape != (5,):
        raise ValueError("invalid algebraic sphere parameters")
    if not np.all(np.isfinite(center_mean)) or not np.all(np.isfinite(a)):
        raise ValueError("algebraic sphere parameters contain NaN or inf")

    coefficient_scale = float(np.max(np.abs(a)))
    scale = float(coordinate_scale)
    if coefficient_scale <= 0.0 or not np.isfinite(scale) or scale <= 0.0:
        raise ValueError("sphere fit became degenerate")

    # A is homogeneous; test curvature after expressing its equation in units
    # of the observed cloud extent, not against an absolute coefficient cutoff.
    a = a / coefficient_scale
    with np.errstate(over="ignore", invalid="ignore"):
        dimensionless_coefficients = np.concatenate(
            (
                np.array([a[0] * scale * scale]),
                a[1:4] * scale,
                np.array([a[4]]),
            )
        )
    if not np.all(np.isfinite(dimensionless_coefficients)):
        raise ValueError("dimensionless sphere coefficients are not finite")
    coefficient_reference = float(np.max(np.abs(dimensionless_coefficients)))
    if coefficient_reference <= 0.0:
        raise ValueError("sphere fit became degenerate")
    curvature_resolution = np.finfo(np.float64).eps * coefficient_reference
    if abs(float(dimensionless_coefficients[0])) <= curvature_resolution:
        raise ValueError("sphere curvature is below float64 resolution for the input extent")
    b = dimensionless_coefficients
    if b[0] == 0.0:
        raise ValueError("sphere fit became degenerate")

    discriminant = float(
        b[1] * b[1] + b[2] * b[2] + b[3] * b[3] - 4.0 * b[0] * b[4]
    )
    if not np.isfinite(discriminant):
        raise ValueError("sphere discriminant is not finite")
    if discriminant < 0.0:
        raise ValueError(
            f"sphere radius became imaginary (discriminant={discriminant:.17g})"
        )

    radius_normalized = np.sqrt(discriminant) / (2.0 * abs(float(b[0])))
    center_normalized = -b[1:4] / (2.0 * b[0])
    with np.errstate(over="ignore", invalid="ignore"):
        center = center_mean + scale * center_normalized
        radius = scale * radius_normalized
    if not np.all(np.isfinite(center)) or not np.isfinite(radius) or radius <= 0.0:
        raise ValueError("invalid fitted sphere")
    return center, float(radius)


def hyper_sphere_fit(points: np.ndarray) -> tuple[np.ndarray, float]:
    """Hyperaccurate algebraic sphere fit, preserving input coordinate space."""
    x = np.asarray(points, dtype=np.float64)
    if x.ndim != 2 or x.shape[1] != 3:
        raise ValueError("points must be an Nx3 array")
    if len(x) < 5:
        raise ValueError("at least 5 points are required")
    if not np.all(np.isfinite(x)):
        raise ValueError("points contain NaN or inf")

    with np.errstate(over="ignore", invalid="ignore"):
        mean = x.mean(axis=0)
        q = x - mean
        w = np.sum(q * q, axis=1)
    if not np.all(np.isfinite(mean)) or not np.all(np.isfinite(q)) or not np.all(np.isfinite(w)):
        raise ValueError("centered sphere coordinates are not finite")
    z = np.column_stack((w, q[:, 0], q[:, 1], q[:, 2], np.ones(len(x))))
    if not np.all(np.isfinite(z)):
        raise ValueError("sphere design matrix is not finite")
    _, singular_values, vt = np.linalg.svd(z, full_matrices=False)
    if not np.all(np.isfinite(singular_values)):
        raise ValueError("sphere design matrix SVD is not finite")
    if not np.all(np.isfinite(vt)):
        raise ValueError("sphere design matrix right singular vectors are not finite")
    v = vt.T
    if singular_values[0] <= 0:
        raise ValueError("degenerate sphere fitting problem")

    if singular_values[-1] / singular_values[0] <= 1e-12:
        a = v[:, -1]
    else:
        w_mean = float(np.mean(w))
        g = np.array(
            [
                [0.0, 0.0, 0.0, 0.0, 0.5],
                [0.0, 1.0, 0.0, 0.0, 0.0],
                [0.0, 0.0, 1.0, 0.0, 0.0],
                [0.0, 0.0, 0.0, 1.0, 0.0],
                [0.5, 0.0, 0.0, 0.0, -2.0 * w_mean],
            ],
            dtype=np.float64,
        )
        s = np.diag(singular_values)
        y = v @ s @ v.T
        t = y @ g @ y
        t = (t + t.T) * 0.5
        if not np.all(np.isfinite(t)):
            raise ValueError("sphere constraint matrix T is not finite")
        eigenvalues, eigenvectors = np.linalg.eigh(t)
        if not np.all(np.isfinite(eigenvectors)):
            raise ValueError("T eigenvectors contain NaN or inf")
        eig_idx = _smallest_nonnegative_eigenvalue_index(eigenvalues)
        a_star = eigenvectors[:, eig_idx]
        if np.any(singular_values <= 0.0):
            raise ValueError("sphere design matrix has a zero singular value")
        inverse_singular_values = 1.0 / singular_values
        if not np.all(np.isfinite(inverse_singular_values)):
            raise ValueError("sphere design matrix inverse singular values are not finite")
        inv_s = np.diag(inverse_singular_values)
        a = v @ inv_s @ v.T @ a_star

    if not np.all(np.isfinite(a)):
        raise ValueError("algebraic sphere coefficients contain NaN or inf")
    coordinate_scale = float(np.sqrt(np.max(w)))
    return _sphere_parameters_from_algebraic(mean, a, coordinate_scale)


def _logexp(x: float) -> float:
    return float(np.log1p(np.exp(x))) if x < 100.0 else float(x)


def _ilogexp(x: float) -> float:
    return float(np.log(np.expm1(x))) if x < 100.0 else float(x)


def _fft_gaussian_kernel(histogram: np.ndarray, width_bins: float) -> np.ndarray:
    histogram = np.asarray(histogram, dtype=np.float64)
    length = len(histogram)
    maximum_length = max(length, int(np.ceil(length + 3.0 * width_bins)))
    n = 1
    while n < maximum_length:
        n *= 2
    transformed = fft(histogram, n)
    frequency = np.fft.fftfreq(n)
    kernel = np.exp(-0.5 * (width_bins * 2.0 * np.pi * frequency) ** 2)
    return np.real(ifft(transformed * kernel, n))[:length]


def sskernel_density(
    values: np.ndarray,
    max_grid: int = 1000,
    max_iterations: int = 20,
) -> tuple[np.ndarray, np.ndarray, float]:
    """Return the Shimazaki-Shinomoto KDE; constants use a point-mass special case."""
    x = np.asarray(values, dtype=np.float64)
    if x.ndim != 1:
        raise ValueError("KDE values must be a one-dimensional array")
    if len(x) == 0:
        raise ValueError("empty data")
    if not np.all(np.isfinite(x)):
        raise ValueError("KDE values contain NaN or inf")
    if (
        isinstance(max_grid, (bool, np.bool_))
        or not isinstance(max_grid, (int, np.integer))
        or max_grid < 2
    ):
        raise ValueError("max_grid must be an integer greater than or equal to 2")
    if (
        isinstance(max_iterations, (bool, np.bool_))
        or not isinstance(max_iterations, (int, np.integer))
        or max_iterations < 0
    ):
        raise ValueError("max_iterations must be a non-negative integer")
    if len(x) == 1 or np.all(x == x[0]):
        # This is a discrete point-mass representation, not a continuous KDE.
        return np.array([x[0]]), np.array([1.0]), 0.0

    # KDE is translation invariant; shifting near zero prevents grid steps from
    # collapsing when residuals vary less than the ULP of their absolute offset.
    offset = float(np.min(x))
    with np.errstate(over="ignore", invalid="ignore"):
        shifted_x = x - offset
    if not np.all(np.isfinite(shifted_x)):
        raise ValueError("KDE data range is not finite")
    sorted_x = np.sort(shifted_x)
    differences = np.diff(sorted_x)
    differences = differences[differences > 0]
    total_range = float(np.max(shifted_x))
    if not np.isfinite(total_range) or total_range <= 0.0:
        raise ValueError("KDE data range must be finite and positive")
    if len(differences) == 0:
        raise ValueError("KDE has no positive sampling interval")
    sampling_resolution = float(differences[0])
    if not np.isfinite(sampling_resolution) or sampling_resolution <= 0.0:
        raise ValueError("KDE sampling interval must be finite and positive")

    # Cap the requested grid without forming an overflowing range / interval ratio.
    if sampling_resolution <= total_range / max_grid:
        grid_count = int(max_grid)
    else:
        grid_ratio = total_range / sampling_resolution
        if not np.isfinite(grid_ratio):
            raise ValueError("KDE grid-count ratio is not finite")
        grid_count = min(int(np.ceil(grid_ratio)), int(max_grid))
    grid_count = max(grid_count, 2)
    grid = np.linspace(0.0, total_range, grid_count)
    dt = total_range / (grid_count - 1)
    if not np.isfinite(dt) or dt <= 0:
        raise ValueError("KDE grid interval must be finite and positive")
    if not np.all(np.diff(grid) > 0.0):
        raise ValueError("KDE grid is not resolvable at float64 precision")
    edges = np.concatenate((grid - dt * 0.5, [grid[-1] + dt * 0.5]))
    if not np.all(np.isfinite(edges)) or not np.all(np.diff(edges) > 0.0):
        raise ValueError("KDE histogram edges are not finite and strictly increasing")
    histogram, _ = np.histogram(shifted_x, bins=edges)
    sample_count = int(histogram.sum())
    if sample_count != len(x):
        raise ValueError(
            f"KDE histogram retained {sample_count} of {len(x)} input samples"
        )
    with np.errstate(over="ignore", divide="ignore", invalid="ignore"):
        histogram_density = histogram.astype(np.float64) / sample_count / dt
    if not np.all(np.isfinite(histogram_density)):
        raise ValueError("KDE histogram density is not finite")

    def cost_function(width: float) -> tuple[float, np.ndarray]:
        if not np.isfinite(width) or width <= 0.0:
            raise ValueError("KDE bandwidth must be finite and positive")
        with np.errstate(over="ignore", divide="ignore", invalid="ignore"):
            width_bins = width / dt
        if not np.isfinite(width_bins) or width_bins <= 0.0:
            raise ValueError("KDE bandwidth in grid units is not finite and positive")
        smoothed = _fft_gaussian_kernel(histogram_density, width_bins)
        with np.errstate(over="ignore", divide="ignore", invalid="ignore"):
            cost = (
                np.sum(smoothed * smoothed) * dt
                - 2.0 * np.sum(smoothed * histogram_density) * dt
                + 2.0 / (np.sqrt(2.0 * np.pi) * width * sample_count)
            )
            objective = float(cost * sample_count * sample_count)
        if not np.isfinite(objective) or not np.all(np.isfinite(smoothed)):
            raise ValueError("KDE objective or density is not finite")
        return objective, smoothed

    with np.errstate(over="ignore", invalid="ignore"):
        width_min = 2.0 * dt
    width_max = total_range
    if (
        not np.isfinite(width_min)
        or not np.isfinite(width_max)
        or width_min <= 0.0
        or width_max <= 0.0
    ):
        raise ValueError("KDE bandwidth bounds must be finite and positive")
    if width_min > width_max:
        raise ValueError(
            f"KDE bandwidth interval is reversed: {width_min:.17g} > {width_max:.17g}"
        )

    if width_min == width_max:
        optimal_width = width_min
        best_cost, density = cost_function(optimal_width)
    else:
        log_min = _ilogexp(width_min)
        log_max = _ilogexp(width_max)
        if not np.isfinite(log_min) or not np.isfinite(log_max) or log_min >= log_max:
            raise ValueError("KDE bandwidth interval collapsed in log-exp coordinates")

        a = log_min
        b = log_max
        phi = (np.sqrt(5.0) + 1.0) / 2.0
        c1 = (phi - 1.0) * a + (2.0 - phi) * b
        c2 = (2.0 - phi) * a + (phi - 1.0) * b
        if not np.isfinite(c1) or not np.isfinite(c2) or c1 >= c2:
            raise ValueError("KDE golden-section candidates are not resolvable")
        width1 = _logexp(c1)
        width2 = _logexp(c2)
        f1, density1 = cost_function(width1)
        f2, density2 = cost_function(width2)
        if f1 <= f2:
            optimal_width, best_cost, density = width1, f1, density1
        else:
            optimal_width, best_cost, density = width2, f2, density2

        tolerance = 1e-5
        iteration = 1
        while abs(b - a) > tolerance * (abs(c1) + abs(c2)) and iteration <= max_iterations:
            if f1 < f2:
                b, c2 = c2, c1
                c1 = (phi - 1.0) * a + (2.0 - phi) * b
                f2 = f1
                width1 = _logexp(c1)
                f1, candidate_density = cost_function(width1)
                if f1 < best_cost:
                    optimal_width, best_cost, density = width1, f1, candidate_density
            else:
                a, c1 = c1, c2
                c2 = (2.0 - phi) * a + (phi - 1.0) * b
                f1 = f2
                width2 = _logexp(c2)
                f2, candidate_density = cost_function(width2)
                if f2 < best_cost:
                    optimal_width, best_cost, density = width2, f2, candidate_density
            iteration += 1

            # Stop only when float64 can no longer distinguish the interior points.
            if c1 == c2:
                break

    with np.errstate(over="ignore", invalid="ignore"):
        normalization = np.sum(density) * dt
    if not np.isfinite(normalization) or normalization <= 0.0:
        raise ValueError("KDE density cannot be normalized")
    with np.errstate(over="ignore", divide="ignore", invalid="ignore"):
        density = density / normalization
        output_grid = grid + offset
    if not np.all(np.isfinite(density)) or not np.all(np.isfinite(output_grid)):
        raise ValueError("normalized KDE output is not finite")
    return output_grid, density, float(optimal_width)


def mode_sskernel(values: np.ndarray) -> float:
    grid, density, _ = sskernel_density(values)
    return float(grid[np.argmax(density)])


def maalek_robust_sphere_fit(
    points: np.ndarray,
    robust_threshold: float = 2.5,
    max_iterations: int = 50,
    progress_callback: ProgressCallback = None,
) -> tuple[np.ndarray, float, np.ndarray]:
    all_points = np.asarray(points, dtype=np.float64)
    if all_points.ndim != 2 or all_points.shape[1] != 3:
        raise ValueError("points must be an Nx3 array")
    if len(all_points) < 5:
        raise ValueError("at least 5 points are required")
    if not np.all(np.isfinite(all_points)):
        raise ValueError("points contain NaN or inf")

    current_indices = np.arange(len(all_points), dtype=np.int64)
    for iteration in range(max_iterations):
        current_points = all_points[current_indices]
        center, radius = hyper_sphere_fit(current_points)
        radial_squared = np.sum((current_points - center) ** 2, axis=1)
        radial_mode = mode_sskernel(radial_squared)
        radial_median = np.median(radial_squared)
        madn = np.median(np.abs(radial_squared - radial_median)) / 0.67449
        if not np.isfinite(madn) or madn <= np.finfo(np.float64).eps:
            keep = np.ones(len(current_indices), dtype=bool)
        else:
            keep = np.abs(radial_squared - radial_mode) / madn <= robust_threshold
        new_indices = current_indices[keep]
        if len(new_indices) < 5:
            break
        if np.array_equal(new_indices, current_indices):
            current_indices = new_indices
            break
        current_indices = new_indices
        if progress_callback is not None:
            progress_callback(50.0 + 40.0 * (iteration + 1) / max_iterations,
                              f"robust sphere fitting: iteration {iteration + 1}")

    center, radius = hyper_sphere_fit(all_points[current_indices])
    all_radial_residuals = np.linalg.norm(all_points - center, axis=1) - radius
    concentrated_residuals = all_radial_residuals[current_indices]
    sigma = float(np.std(concentrated_residuals, ddof=1)) if len(concentrated_residuals) > 1 else 0.0
    if np.isfinite(sigma) and sigma > 0:
        final_indices = np.where(np.abs(all_radial_residuals) / sigma <= robust_threshold)[0]
    else:
        final_indices = current_indices
    if len(final_indices) >= 5:
        final_center, final_radius = hyper_sphere_fit(all_points[final_indices])
    else:
        final_center, final_radius = center, radius
        final_indices = current_indices
    if progress_callback is not None:
        progress_callback(95.0, "robust sphere fitting completed")
    return final_center, float(final_radius), final_indices


def estimate_reference_sphere(
    points: np.ndarray,
    knn_k: int = 8,
    connectivity_alpha: float = 2.5,
    progress_callback: ProgressCallback = None,
) -> ReferenceSphereResult:
    """Estimate a sphere from Nx3 coordinates without file or unit conversions."""
    selected_points = np.asarray(points, dtype=np.float64)
    if selected_points.ndim != 2 or selected_points.shape[1] != 3:
        raise ValueError("points must be an Nx3 array")
    if len(selected_points) < 5:
        raise ValueError("at least 5 selected points are required")
    if not np.all(np.isfinite(selected_points)):
        raise ValueError("points contain NaN or inf")

    component_points, component_indices, median_knn_distance, epsilon = largest_connected_component(
        selected_points, knn_k=knn_k, alpha=connectivity_alpha,
        progress_callback=progress_callback,
    )
    if progress_callback is not None:
        progress_callback(50.0, "robust sphere fitting")
    center, radius, component_fit_indices = maalek_robust_sphere_fit(
        component_points, robust_threshold=2.5, progress_callback=progress_callback,
    )
    fit_inlier_indices = component_indices[component_fit_indices]
    diameter = 2.0 * radius
    direction = np.array([1.0, 0.0, 0.0], dtype=np.float64)
    point1 = center - radius * direction
    point2 = center + radius * direction
    return ReferenceSphereResult(
        input_point_count=len(selected_points),
        component_point_count=len(component_points),
        component_removed_count=len(selected_points) - len(component_points),
        knn_k=int(knn_k),
        connectivity_alpha=float(connectivity_alpha),
        median_knn_distance=float(median_knn_distance),
        connectivity_epsilon=float(epsilon),
        fit_inlier_count=len(component_fit_indices),
        method_name="maalek_lichti_reproduction",
        center=np.asarray(center, dtype=np.float64),
        radius=float(radius),
        diameter=float(diameter),
        diameter_point1=np.asarray(point1, dtype=np.float64),
        diameter_point2=np.asarray(point2, dtype=np.float64),
        component_indices=np.asarray(component_indices, dtype=np.int64),
        fit_inlier_indices=np.asarray(fit_inlier_indices, dtype=np.int64),
    )
