import ast
import inspect
from pathlib import Path
import unittest
from unittest.mock import patch

import numpy as np

import reference_sphere_algorithm as reference_sphere
from reference_sphere_algorithm import (
    estimate_reference_sphere,
    hyper_sphere_fit,
    largest_connected_component,
    maalek_robust_sphere_fit,
    sskernel_density,
)


def fibonacci_sphere(count=1200, center=(0.12, -0.74, 0.03), radius=0.0258):
    indices = np.arange(count, dtype=np.float64)
    z = 1.0 - 2.0 * (indices + 0.5) / count
    angle = np.pi * (3.0 - np.sqrt(5.0)) * indices
    radial = np.sqrt(1.0 - z * z)
    directions = np.column_stack((radial * np.cos(angle), radial * np.sin(angle), z))
    return np.asarray(center, dtype=np.float64) + radius * directions


class ReferenceSphereAlgorithmTests(unittest.TestCase):
    def test_algorithm_module_has_no_io_or_unity_imports(self):
        source_path = Path(inspect.getsourcefile(estimate_reference_sphere))
        tree = ast.parse(source_path.read_text(encoding="utf-8"))
        imports = []
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                imports.extend(alias.name for alias in node.names)
            elif isinstance(node, ast.ImportFrom):
                imports.append(node.module or "")
        forbidden = ("open3d", "pathlib", "argparse", "json", "subprocess", "pointcloud_io", "UnityEngine")
        self.assertFalse([name for name in imports if any(item.lower() in name.lower() for item in forbidden)])

    def test_known_complete_sphere_recovers_center_and_radius(self):
        center = np.array([0.12, -0.74, 0.03])
        points = fibonacci_sphere(center=center, radius=0.0258)
        q = points - points.mean(axis=0)
        w = np.sum(q * q, axis=1)
        z = np.column_stack((w, q, np.ones(len(points))))
        singular_values = np.linalg.svd(z, compute_uv=False)
        self.assertLessEqual(singular_values[-1] / singular_values[0], 1e-12)
        with patch.object(
            reference_sphere.np.linalg, "eigh", wraps=np.linalg.eigh
        ) as eigensolver:
            fitted_center, fitted_radius = hyper_sphere_fit(points)
        eigensolver.assert_not_called()
        np.testing.assert_allclose(fitted_center, center, atol=1e-10)
        self.assertAlmostEqual(fitted_radius, 0.0258, places=10)

    def test_noisy_sphere_uses_non_singular_eigenvalue_branch(self):
        rng = np.random.default_rng(20261012)
        expected_center = np.array([0.12, -0.74, 0.03])
        expected_radius = 0.0258
        directions = rng.normal(size=(1200, 3))
        directions /= np.linalg.norm(directions, axis=1, keepdims=True)
        radii = expected_radius + rng.normal(0.0, expected_radius * 0.025, 1200)
        points = expected_center + directions * radii[:, None]
        q = points - points.mean(axis=0)
        w = np.sum(q * q, axis=1)
        z = np.column_stack((w, q, np.ones(len(points))))
        design_singular_values = np.linalg.svd(z, compute_uv=False)
        self.assertGreater(design_singular_values[-1] / design_singular_values[0], 1e-12)

        original_svd = np.linalg.svd
        original_eigenvalue_selector = (
            reference_sphere._smallest_nonnegative_eigenvalue_index
        )
        observed_singular_ratios = []
        observed_eigenvalue_selections = []

        def record_svd(matrix, *args, **kwargs):
            result = original_svd(matrix, *args, **kwargs)
            observed_singular_ratios.append(result[1][-1] / result[1][0])
            return result

        def record_eigenvalue_selection(eigenvalues):
            selected_index = original_eigenvalue_selector(eigenvalues)
            observed_eigenvalue_selections.append(
                (np.asarray(eigenvalues).copy(), selected_index)
            )
            return selected_index

        with patch.object(reference_sphere.np.linalg, "svd", side_effect=record_svd):
            with patch.object(
                reference_sphere,
                "_smallest_nonnegative_eigenvalue_index",
                side_effect=record_eigenvalue_selection,
            ) as select_eigenvalue:
                fitted_center, fitted_radius = hyper_sphere_fit(points)

        self.assertEqual(len(observed_singular_ratios), 1)
        self.assertGreater(observed_singular_ratios[0], 1e-12)
        select_eigenvalue.assert_called_once()
        self.assertEqual(len(observed_eigenvalue_selections), 1)
        eigenvalues, selected_index = observed_eigenvalue_selections[0]
        eligible = np.flatnonzero(eigenvalues >= 0.0)
        self.assertTrue(np.any(eigenvalues < 0.0))
        self.assertGreater(len(eligible), 0)
        self.assertEqual(
            selected_index,
            int(eligible[np.argmin(eigenvalues[eligible])]),
        )
        self.assertTrue(np.all(np.isfinite(fitted_center)))
        self.assertTrue(np.isfinite(fitted_radius))
        self.assertLess(np.linalg.norm(fitted_center - expected_center), expected_radius * 0.01)
        self.assertLess(abs(fitted_radius - expected_radius), expected_radius * 0.02)

        result = estimate_reference_sphere(points)
        self.assertEqual(result.component_point_count, len(points))
        self.assertGreaterEqual(result.fit_inlier_count, 5)
        self.assertLess(np.linalg.norm(result.center - expected_center), expected_radius * 0.01)
        self.assertLess(abs(result.radius - expected_radius), expected_radius * 0.02)

        scaled_offset = np.array([1000.0, -2000.0, 3000.0])
        scaled_center, scaled_radius = hyper_sphere_fit(points * 100.0 + scaled_offset)
        np.testing.assert_allclose(
            scaled_center,
            expected_center * 100.0 + scaled_offset,
            rtol=0.0,
            atol=0.01,
        )
        self.assertAlmostEqual(scaled_radius / 100.0, fitted_radius, delta=1e-6)

    def test_fit_is_stable_across_coordinate_scales(self):
        unit_sphere = fibonacci_sphere()
        base_center = np.array([0.12, -0.74, 0.03])
        for scale in (1e-9, 1.0, 2e9):
            with self.subTest(scale=scale):
                fitted_center, fitted_radius = hyper_sphere_fit(unit_sphere * scale)
                np.testing.assert_allclose(
                    fitted_center, base_center * scale, rtol=2e-9, atol=1e-22
                )
                self.assertAlmostEqual(
                    fitted_radius / scale, 0.0258, delta=2e-10
                )

    def test_fit_handles_translation_until_float64_resolves_no_sphere(self):
        centered_points = fibonacci_sphere(center=(0.0, 0.0, 0.0))
        translated_center = np.array([1e6, -2e6, 3e6])
        center, radius = hyper_sphere_fit(centered_points + translated_center)
        np.testing.assert_allclose(center, translated_center, rtol=0.0, atol=1e-8)
        self.assertAlmostEqual(radius, 0.0258, delta=1e-10)

        # At this offset, float64 quantization collapses the input sphere to one
        # coordinate; the fitting code must report degeneracy rather than invent a fit.
        unresolvable_offset = np.array([1e15, -2e15, 3e15])
        quantized_points = centered_points + unresolvable_offset
        self.assertEqual(len(np.unique(quantized_points, axis=0)), 1)
        with self.assertRaisesRegex(ValueError, "degenerate"):
            hyper_sphere_fit(quantized_points)

    def test_only_smallest_nonnegative_eigenvalue_is_eligible(self):
        self.assertEqual(
            reference_sphere._smallest_nonnegative_eigenvalue_index(
                np.array([-1e-30, 0.0, 2.0])
            ),
            1,
        )
        self.assertEqual(
            reference_sphere._smallest_nonnegative_eigenvalue_index(
                np.array([-1e-300, 1e-30, 2e-30])
            ),
            1,
        )
        for eigenvalues in (
            np.array([-2.0, -1e-30]),
            np.array([np.nan, 1.0]),
            np.array([np.inf, 1.0]),
        ):
            with self.subTest(eigenvalues=eigenvalues), self.assertRaises(ValueError):
                reference_sphere._smallest_nonnegative_eigenvalue_index(eigenvalues)

    def test_negative_discriminant_is_not_clamped_to_zero(self):
        coefficients = np.array([1.0, 0.0, 0.0, 0.0, 1e-13])
        with self.assertRaisesRegex(ValueError, "discriminant"):
            reference_sphere._sphere_parameters_from_algebraic(
                np.zeros(3), coefficients, 1.0
            )

    def test_algebraic_fit_is_invariant_to_coefficient_gauge(self):
        mean = np.array([3.0, -1.0, 2.0])
        relative_center = np.array([0.5, -0.25, 1.0])
        radius = 0.75
        coefficients = np.r_[
            1.0,
            -2.0 * relative_center,
            np.dot(relative_center, relative_center) - radius**2,
        ]
        for coefficient_scale in (1e-100, 1.0, 1e100):
            with self.subTest(coefficient_scale=coefficient_scale):
                center, fitted_radius = (
                    reference_sphere._sphere_parameters_from_algebraic(
                        mean, coefficients * coefficient_scale, 2.0
                    )
                )
                np.testing.assert_allclose(center, mean + relative_center, atol=1e-14)
                self.assertAlmostEqual(fitted_radius, radius, places=14)

    def test_coplanar_points_are_rejected_as_degenerate(self):
        points = np.array(
            [[x, y, 0.0] for x in (-1.0, 0.0, 1.0) for y in (-1.0, 0.0, 1.0)],
            dtype=np.float64,
        )
        with self.assertRaises(ValueError):
            hyper_sphere_fit(points)

    def test_sskernel_retains_the_lowest_cost_evaluated_candidate(self):
        rng = np.random.default_rng(98)
        values = np.r_[rng.normal(0.0, 1.0, 12), rng.normal(3.0, 0.4, 4)]
        evaluated = []
        original_kernel = reference_sphere._fft_gaussian_kernel

        def record_candidate(histogram_density, width_bins):
            smoothed = original_kernel(histogram_density, width_bins)
            evaluated.append((float(width_bins), smoothed.copy()))
            return smoothed

        with patch.object(
            reference_sphere,
            "_fft_gaussian_kernel",
            side_effect=record_candidate,
        ):
            grid, density, bandwidth = sskernel_density(values)

        dt = float((grid[-1] - grid[0]) / (len(grid) - 1))
        shifted = values - np.min(values)
        shifted_grid = grid - np.min(values)
        edges = np.r_[shifted_grid - dt * 0.5, shifted_grid[-1] + dt * 0.5]
        histogram, _ = np.histogram(shifted, bins=edges)
        histogram_density = histogram.astype(np.float64) / len(values) / dt
        objectives = []
        for width_bins, smoothed in evaluated:
            candidate_bandwidth = width_bins * dt
            cost = (
                np.sum(smoothed * smoothed) * dt
                - 2.0 * np.sum(smoothed * histogram_density) * dt
                + 2.0 / (np.sqrt(2.0 * np.pi) * candidate_bandwidth * len(values))
            )
            objectives.append(float(cost * len(values) ** 2))

        best_index = int(np.argmin(objectives))
        self.assertGreater(len(evaluated), 2)
        self.assertLess(objectives[best_index], objectives[-1])
        self.assertAlmostEqual(bandwidth, evaluated[best_index][0] * dt, places=14)
        expected_density = evaluated[best_index][1]
        expected_density /= np.sum(expected_density) * dt
        np.testing.assert_allclose(density, expected_density, rtol=1e-13, atol=1e-13)

    def test_sskernel_rejects_reversed_two_grid_bandwidth_interval(self):
        with patch.object(reference_sphere, "_fft_gaussian_kernel") as kernel:
            with self.assertRaisesRegex(ValueError, "interval is reversed"):
                sskernel_density(np.array([0.0, 1.0]))
        kernel.assert_not_called()

    def test_sskernel_evaluates_equal_three_grid_interval_once(self):
        values = np.array([0.0, 0.4, 1.0])
        original_kernel = reference_sphere._fft_gaussian_kernel
        evaluated_widths = []

        def record_width(histogram, width_bins):
            evaluated_widths.append(float(width_bins))
            return original_kernel(histogram, width_bins)

        with patch.object(
            reference_sphere, "_fft_gaussian_kernel", side_effect=record_width
        ):
            grid, density, bandwidth = sskernel_density(values)

        self.assertEqual(len(grid), 3)
        self.assertEqual(bandwidth, 1.0)
        self.assertEqual(evaluated_widths, [2.0])
        self.assertTrue(np.all(np.isfinite(density)))
        self.assertAlmostEqual(np.sum(density) * 0.5, 1.0, places=14)

    def test_sskernel_four_grid_distribution_uses_golden_search(self):
        values = np.array([0.0, 0.3, 0.6, 1.0])
        evaluated_widths = []
        original_kernel = reference_sphere._fft_gaussian_kernel

        def record_width(histogram, width_bins):
            evaluated_widths.append(float(width_bins))
            return original_kernel(histogram, width_bins)

        with patch.object(
            reference_sphere, "_fft_gaussian_kernel", side_effect=record_width
        ):
            grid, density, bandwidth = sskernel_density(values)

        dt = (grid[-1] - grid[0]) / (len(grid) - 1)
        self.assertEqual(len(grid), 4)
        self.assertGreater(2.0 * dt, 0.0)
        self.assertLess(2.0 * dt, grid[-1] - grid[0])
        self.assertGreater(len(evaluated_widths), 2)
        self.assertTrue(2.0 * dt <= bandwidth <= grid[-1] - grid[0])
        self.assertAlmostEqual(np.sum(density) * dt, 1.0, places=13)

    def test_sskernel_constant_values_use_point_mass_special_case(self):
        grid, density, bandwidth = sskernel_density(np.array([2.5, 2.5, 2.5]))
        np.testing.assert_array_equal(grid, np.array([2.5]))
        np.testing.assert_array_equal(density, np.array([1.0]))
        self.assertEqual(bandwidth, 0.0)

    def test_sskernel_handles_repeated_values_and_narrow_ranges(self):
        repeated = np.repeat(np.array([0.0, 0.25, 0.5, 0.75, 1.0]), [4, 3, 2, 5, 6])
        narrow = np.arange(5, dtype=np.float64) * 1e-12
        for values in (repeated, narrow):
            with self.subTest(value_range=float(np.ptp(values))):
                grid, density, bandwidth = sskernel_density(values)
                dt = (grid[-1] - grid[0]) / (len(grid) - 1)
                self.assertGreaterEqual(len(grid), 4)
                self.assertTrue(np.isfinite(bandwidth) and bandwidth > 0.0)
                self.assertTrue(np.all(np.isfinite(density)))
                self.assertAlmostEqual(np.sum(density) * dt, 1.0, places=12)

    def test_sskernel_is_translation_stable_and_rejects_invalid_inputs(self):
        values = np.array([0.0, 0.3, 0.6, 1.0])
        base_grid, base_density, base_bandwidth = sskernel_density(values)
        shifted_grid, shifted_density, shifted_bandwidth = sskernel_density(
            values + 1e8
        )
        np.testing.assert_allclose(
            shifted_grid - 1e8, base_grid, rtol=0.0, atol=1e-8
        )
        np.testing.assert_allclose(shifted_density, base_density, atol=1e-12)
        self.assertEqual(shifted_bandwidth, base_bandwidth)

        invalid_inputs = (
            np.array([]),
            np.array([0.0, np.nan]),
            np.array([0.0, np.inf]),
            np.array([[0.0, 1.0]]),
        )
        for invalid in invalid_inputs:
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                sskernel_density(invalid)
        with self.assertRaisesRegex(ValueError, "data range is not finite"):
            sskernel_density(np.array([-1e308, 1e308]))
        for kwargs in ({"max_grid": 1}, {"max_iterations": -1}):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                sskernel_density(values, **kwargs)

    def test_sskernel_rejects_unrepresentable_cost_at_extreme_scale(self):
        with self.assertRaisesRegex(ValueError, "objective or density is not finite"):
            sskernel_density(np.arange(5, dtype=np.float64) * 1e-200)

    def test_largest_component_drops_separated_small_cluster(self):
        sphere = fibonacci_sphere(count=900)
        satellite = np.array([[2.0 + i * 0.0005, 2.0, 2.0] for i in range(10)])
        points = np.vstack((sphere, satellite))
        component, indices, _, _ = largest_connected_component(points, knn_k=8, alpha=2.5)
        self.assertEqual(len(component), len(sphere))
        self.assertEqual(len(indices), len(sphere))
        self.assertTrue(np.all(indices < len(sphere)))

    def test_partial_sphere_recovers_known_diameter(self):
        points = fibonacci_sphere(count=2400, center=(0.4, -0.2, 0.1), radius=0.03)
        points = points[points[:, 2] >= 0.1]
        result = estimate_reference_sphere(points)
        self.assertGreater(len(points), 1000)
        np.testing.assert_allclose(result.center, [0.4, -0.2, 0.1], atol=1e-9)
        self.assertAlmostEqual(result.radius, 0.03, delta=1e-9)
        self.assertAlmostEqual(result.diameter, 0.06, delta=1e-7)

    def test_largest_of_two_separated_spheres_is_selected(self):
        larger = fibonacci_sphere(count=900, center=(0.0, 0.0, 0.0), radius=0.0258)
        smaller = fibonacci_sphere(count=500, center=(2.0, 0.0, 0.0), radius=0.04)
        result = estimate_reference_sphere(np.vstack((larger, smaller)))
        self.assertEqual(result.component_point_count, len(larger))
        self.assertAlmostEqual(result.diameter, 0.0516, delta=1e-7)

    def test_robust_fit_resists_few_nearby_radial_outliers(self):
        sphere = fibonacci_sphere(count=1600, center=(0.0, 0.0, 0.0), radius=1.0)
        directions = np.array([[1, 1, 1], [-1, 1, -1], [1, -1, -1], [-1, -1, 1]], dtype=np.float64)
        directions /= np.linalg.norm(directions, axis=1, keepdims=True)
        points = np.vstack((sphere, directions * 1.12))
        _, radius, inliers = maalek_robust_sphere_fit(points)
        self.assertAlmostEqual(radius, 1.0, delta=0.02)
        self.assertLessEqual(len(inliers), len(points))

    def test_invalid_k_and_alpha_raise_value_error(self):
        points = fibonacci_sphere(count=80)
        for k in (0, len(points), 1.5):
            with self.subTest(k=k), self.assertRaises(ValueError):
                estimate_reference_sphere(points, knn_k=k)
        for alpha in (0.0, -1.0, float("nan"), float("inf"), 10.0001):
            with self.subTest(alpha=alpha), self.assertRaises(ValueError):
                estimate_reference_sphere(points, connectivity_alpha=alpha)

    def test_maximum_connectivity_alpha_is_valid(self):
        result = estimate_reference_sphere(fibonacci_sphere(count=80), connectivity_alpha=10.0)
        self.assertEqual(result.connectivity_alpha, 10.0)

    def test_insufficient_points_raise_value_error(self):
        with self.assertRaises(ValueError):
            estimate_reference_sphere(np.zeros((4, 3)))

    def test_non_finite_coordinates_raise_value_error(self):
        points = fibonacci_sphere(count=20)
        points[0, 1] = np.nan
        with self.assertRaises(ValueError):
            estimate_reference_sphere(points)

    def test_result_diameter_and_endpoints_are_consistent(self):
        expected_center = np.array([0.12, -0.74, 0.03])
        result = estimate_reference_sphere(fibonacci_sphere(center=expected_center, radius=0.0258))
        np.testing.assert_allclose((result.diameter_point1 + result.diameter_point2) * 0.5,
                                   result.center, atol=1e-12)
        self.assertAlmostEqual(np.linalg.norm(result.diameter_point2 - result.diameter_point1),
                               result.diameter, places=12)
        self.assertAlmostEqual(result.diameter, 2.0 * result.radius, places=12)
        np.testing.assert_allclose(result.center, expected_center, atol=1e-9)
        self.assertEqual(result.method_name, "maalek_lichti_reproduction")
        self.assertEqual(result.fit_inlier_count, len(result.fit_inlier_indices))

    def test_results_remain_in_input_data_space(self):
        scale = 0.0017
        expected_center = np.array([0.12, -0.74, 0.03]) * scale
        expected_radius = 0.0258 * scale
        points = fibonacci_sphere(center=expected_center, radius=expected_radius)
        result = estimate_reference_sphere(points)
        np.testing.assert_allclose(result.center, expected_center, atol=1e-12)
        self.assertAlmostEqual(result.radius, expected_radius, delta=1e-12)


if __name__ == "__main__":
    unittest.main()
