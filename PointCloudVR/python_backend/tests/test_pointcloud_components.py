import unittest

import numpy as np
from scipy.sparse import csr_matrix
from scipy.sparse.csgraph import connected_components
from scipy.spatial import cKDTree

from pointcloud_components import (
    ConnectedComponentError,
    connected_component_labels,
    select_connected_component,
)


def line_component(count, y=0.0, x_offset=0.0):
    return np.column_stack((x_offset + np.arange(count) * 0.1,
                            np.full(count, y), np.zeros(count))).astype(np.float32)


class PointCloudComponentsTests(unittest.TestCase):
    def test_largest_and_selected_keep_different_components(self):
        large = line_component(24)
        small = line_component(9, y=5.0)
        points = np.vstack((large, small))

        largest = select_connected_component(points, knn_k=1, alpha=1.5, mode="largest")
        np.testing.assert_array_equal(largest.keep_mask, np.arange(len(points)) < len(large))
        self.assertEqual(largest.kept_point_count, len(large))
        self.assertEqual(largest.component_count, 2)

        selected = np.zeros(len(points), dtype=bool)
        selected[len(large) + 3] = True
        chosen = select_connected_component(
            points, knn_k=1, alpha=1.5, mode="selected", selection_mask=selected
        )
        np.testing.assert_array_equal(chosen.keep_mask, np.arange(len(points)) >= len(large))
        self.assertEqual(chosen.kept_point_count, len(small))

    def test_multiple_selected_points_in_one_component_are_allowed(self):
        points = np.vstack((line_component(10), line_component(8, y=4.0)))
        selection = np.zeros(len(points), dtype=bool)
        selection[[1, 4, 8]] = True
        result = select_connected_component(
            points, knn_k=1, alpha=1.5, mode="selected", selection_mask=selection
        )
        np.testing.assert_array_equal(result.keep_mask, np.arange(len(points)) < 10)

    def test_selected_points_spanning_components_fail_without_fallback(self):
        points = np.vstack((line_component(10), line_component(8, y=4.0)))
        selection = np.zeros(len(points), dtype=bool)
        selection[[0, 12]] = True
        with self.assertRaisesRegex(ConnectedComponentError, "span 2"):
            select_connected_component(
                points, knn_k=1, alpha=1.5, mode="selected", selection_mask=selection
            )

    def test_missing_empty_malformed_selection_is_rejected(self):
        points = line_component(8)
        cases = (None, np.zeros(len(points), dtype=bool), np.zeros(len(points) - 1, dtype=bool),
                 np.full(len(points), np.nan))
        for selection in cases:
            with self.subTest(selection=selection), self.assertRaises(ValueError):
                select_connected_component(
                    points, knn_k=1, alpha=1.5, mode="selected", selection_mask=selection
                )

    def test_input_validation_and_resource_caps(self):
        points = line_component(20)
        with self.assertRaisesRegex(ValueError, "not enough points"):
            connected_component_labels(points[:1], knn_k=1)
        with self.assertRaisesRegex(ValueError, "NaN or inf"):
            invalid = points.copy()
            invalid[0, 0] = np.inf
            connected_component_labels(invalid, knn_k=1)
        self.assertGreater(connected_component_labels(points, knn_k=1, alpha=10.0).epsilon, 0.0)
        for alpha in (0.0, -1.0, np.nan, np.inf, 10.0001):
            with self.subTest(alpha=alpha), self.assertRaisesRegex(ValueError, "alpha must be"):
                connected_component_labels(points, knn_k=1, alpha=alpha)
        with self.assertRaisesRegex(ConnectedComponentError, "safety limit"):
            connected_component_labels(points, knn_k=1, alpha=1.5, max_graph_edges=0)

    def test_result_matches_scipy_query_pairs_graph(self):
        rng = np.random.default_rng(417)
        points = rng.normal(size=(80, 3)).astype(np.float32)
        result = connected_component_labels(points, knn_k=4, alpha=2.0)
        tree = cKDTree(points.astype(np.float64))
        pairs = tree.query_pairs(result.epsilon, output_type="ndarray")
        rows = np.concatenate((pairs[:, 0], pairs[:, 1])) if len(pairs) else np.empty(0, dtype=int)
        cols = np.concatenate((pairs[:, 1], pairs[:, 0])) if len(pairs) else np.empty(0, dtype=int)
        graph = csr_matrix((np.ones(len(rows), dtype=np.uint8), (rows, cols)), shape=(len(points), len(points)))
        _, scipy_labels = connected_components(graph, directed=False)
        for left in range(len(points)):
            for right in range(len(points)):
                self.assertEqual(result.labels[left] == result.labels[right],
                                 scipy_labels[left] == scipy_labels[right])

    def test_equal_sized_components_choose_first_input_component(self):
        points = np.vstack((line_component(8), line_component(8, y=4.0)))
        result = select_connected_component(points, knn_k=1, alpha=1.5, mode="largest")
        np.testing.assert_array_equal(result.keep_mask, np.arange(len(points)) < 8)

    def test_exact_epsilon_boundary_is_connected(self):
        points = np.array([[0.0, 0.0, 0.0], [1.0, 0.0, 0.0],
                           [10.0, 0.0, 0.0], [11.0, 0.0, 0.0]], dtype=np.float64)
        result = connected_component_labels(points, knn_k=1, alpha=1.0)
        self.assertEqual(result.component_count, 2)
        self.assertEqual(result.labels[0], result.labels[1])
        self.assertEqual(result.labels[2], result.labels[3])
        self.assertNotEqual(result.labels[0], result.labels[2])

    def test_duplicate_points_are_connected_without_changing_order(self):
        line = line_component(8)
        points = np.vstack((line, line[0:1]))
        result = select_connected_component(points, knn_k=1, alpha=1.5, mode="largest")
        self.assertEqual(result.component_count, 1)
        self.assertEqual(result.kept_point_count, len(points))
        self.assertEqual(result.labels[0], result.labels[-1])


if __name__ == "__main__":
    unittest.main()
