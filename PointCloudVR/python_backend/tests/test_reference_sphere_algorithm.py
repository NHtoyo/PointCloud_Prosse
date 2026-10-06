import ast
import inspect
import unittest
from pathlib import Path

import numpy as np

from reference_sphere_algorithm import (
    estimate_reference_sphere,
    hyper_sphere_fit,
    largest_connected_component,
    maalek_robust_sphere_fit,
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
        fitted_center, fitted_radius = hyper_sphere_fit(points)
        np.testing.assert_allclose(fitted_center, center, atol=1e-10)
        self.assertAlmostEqual(fitted_radius, 0.0258, places=10)

    def test_largest_component_drops_separated_small_cluster(self):
        sphere = fibonacci_sphere(count=900)
        satellite = np.array([[2.0 + i * 0.0005, 2.0, 2.0] for i in range(10)])
        points = np.vstack((sphere, satellite))
        component, indices, _, _ = largest_connected_component(points, knn_k=8, alpha=2.5)
        self.assertEqual(len(component), len(sphere))
        self.assertEqual(len(indices), len(sphere))
        self.assertTrue(np.all(indices < len(sphere)))

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
