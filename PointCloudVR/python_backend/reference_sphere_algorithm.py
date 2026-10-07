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


def hyper_sphere_fit(points: np.ndarray) -> tuple[np.ndarray, float]:
    """Hyperaccurate algebraic sphere fit, preserving input coordinate space."""
    x = np.asarray(points, dtype=np.float64)
    if x.ndim != 2 or x.shape[1] != 3:
        raise ValueError("points must be an Nx3 array")
    if len(x) < 5:
        raise ValueError("at least 5 points are required")
    if not np.all(np.isfinite(x)):
        raise ValueError("points contain NaN or inf")

    mean = x.mean(axis=0)
    q = x - mean
    w = np.sum(q * q, axis=1)
    z = np.column_stack((w, q[:, 0], q[:, 1], q[:, 2], np.ones(len(x))))
    _, singular_values, vt = np.linalg.svd(z, full_matrices=False)
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
        eigenvalues, eigenvectors = np.linalg.eigh(t)
        tolerance = 1e-12 * max(1.0, float(np.max(np.abs(eigenvalues))))
        candidates = np.where(eigenvalues >= -tolerance)[0]
        if len(candidates) == 0:
            eig_idx = int(np.argmin(np.abs(eigenvalues)))
        else:
            eig_idx = candidates[np.argmin(eigenvalues[candidates])]
        a_star = eigenvectors[:, eig_idx]
        inv_s = np.diag(1.0 / singular_values)
        a = v @ inv_s @ v.T @ a_star

    a1, a2, a3, a4, a5 = a
    if abs(a1) < 1e-15:
        raise ValueError("sphere fit became degenerate")
    discriminant = a2 * a2 + a3 * a3 + a4 * a4 - 4.0 * a1 * a5
    if discriminant < 0:
        if discriminant > -1e-12:
            discriminant = 0.0
        else:
            raise ValueError("sphere radius became imaginary")
    radius = np.sqrt(discriminant) / (2.0 * abs(a1))
    center = mean - np.array([a2, a3, a4], dtype=np.float64) / (2.0 * a1)
    if not np.all(np.isfinite(center)) or not np.isfinite(radius) or radius <= 0:
        raise ValueError("invalid fitted sphere")
    return center, float(radius)


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
    x = np.asarray(values, dtype=np.float64)
    x = x[np.isfinite(x)]
    if len(x) == 0:
        raise ValueError("empty data")
    if len(x) == 1 or np.ptp(x) == 0:
        return np.array([x[0]]), np.array([1.0]), 0.0

    # KDE is translation invariant; shifting near zero prevents grid steps from
    # collapsing when residuals vary less than the ULP of their absolute offset.
    offset = float(np.min(x))
    shifted_x = x - offset
    sorted_x = np.sort(shifted_x)
    differences = np.diff(sorted_x)
    differences = differences[differences > 0]
    total_range = float(np.ptp(shifted_x))
    if total_range <= 0:
        return np.array([offset]), np.array([1.0]), 0.0
    sampling_resolution = total_range / max_grid if len(differences) == 0 else float(differences[0])
    grid_count = min(int(np.ceil(total_range / sampling_resolution)), max_grid)
    grid_count = max(grid_count, 50)
    grid = np.linspace(0.0, total_range, grid_count)
    dt = float(np.min(np.diff(grid)))
    if not np.isfinite(dt) or dt <= 0:
        return np.array([offset]), np.array([1.0]), 0.0
    edges = np.concatenate((grid - dt * 0.5, [grid[-1] + dt * 0.5]))
    histogram, _ = np.histogram(shifted_x, bins=edges)
    sample_count = int(histogram.sum())
    if sample_count == 0:
        raise ValueError("KDE histogram contains no samples")
    histogram_density = histogram.astype(np.float64) / sample_count / dt

    def cost_function(width: float) -> tuple[float, np.ndarray]:
        smoothed = _fft_gaussian_kernel(histogram_density, width / dt)
        cost = (
            np.sum(smoothed * smoothed) * dt
            - 2.0 * np.sum(smoothed * histogram_density) * dt
            + 2.0 / (np.sqrt(2.0 * np.pi) * width * sample_count)
        )
        return float(cost * sample_count * sample_count), smoothed

    width_min = 2.0 * dt
    width_max = total_range
    a = _ilogexp(width_min)
    b = _ilogexp(width_max)
    phi = (np.sqrt(5.0) + 1.0) / 2.0
    c1 = (phi - 1.0) * a + (2.0 - phi) * b
    c2 = (2.0 - phi) * a + (phi - 1.0) * b
    f1, _ = cost_function(_logexp(c1))
    f2, _ = cost_function(_logexp(c2))
    tolerance = 1e-5
    optimal_width = None
    density = None
    iteration = 1
    while abs(b - a) > tolerance * (abs(c1) + abs(c2)) and iteration <= max_iterations:
        if f1 < f2:
            b, c2 = c2, c1
            c1 = (phi - 1.0) * a + (2.0 - phi) * b
            f2 = f1
            f1, density = cost_function(_logexp(c1))
            optimal_width = _logexp(c1)
        else:
            a, c1 = c1, c2
            c2 = (2.0 - phi) * a + (phi - 1.0) * b
            f1 = f2
            f2, density = cost_function(_logexp(c2))
            optimal_width = _logexp(c2)
        iteration += 1

    if optimal_width is None:
        optimal_width = (width_min + width_max) * 0.5
        _, density = cost_function(optimal_width)
    normalization = np.sum(density) * dt
    if normalization > 0:
        density = density / normalization
    return grid + offset, density, float(optimal_width)


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
