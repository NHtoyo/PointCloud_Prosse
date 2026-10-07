"""Shared connected-component operations for Nx3 point clouds."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Callable

import numpy as np
from scipy.sparse import csr_matrix
from scipy.sparse.csgraph import connected_components
from scipy.spatial import cKDTree


ProgressCallback = Callable[[float, str], None] | None


class ConnectedComponentError(ValueError):
    def __init__(
        self,
        message: str,
        *,
        input_point_count: int,
        knn_k: int,
        alpha: float,
        median_knn_distance: float | None = None,
        epsilon: float | None = None,
        component_count: int | None = None,
        largest_component_point_count: int | None = None,
    ):
        super().__init__(message)
        self.input_point_count = input_point_count
        self.knn_k = knn_k
        self.alpha = alpha
        self.median_knn_distance = median_knn_distance
        self.epsilon = epsilon
        self.component_count = component_count
        self.largest_component_point_count = largest_component_point_count


@dataclass
class ConnectedComponentResult:
    points: np.ndarray
    indices: np.ndarray
    median_knn_distance: float
    epsilon: float
    component_count: int


def largest_connected_component(
    points: np.ndarray,
    knn_k: int = 8,
    alpha: float = 2.5,
    progress_callback: ProgressCallback = None,
    *,
    return_diagnostics: bool = False,
) -> tuple[np.ndarray, np.ndarray, float, float] | ConnectedComponentResult:
    """Keep the largest epsilon-graph component, preserving input coordinates."""
    x = np.asarray(points, dtype=np.float64)
    input_count = len(x) if x.ndim > 0 else 0
    if x.ndim != 2 or x.shape[1] != 3:
        raise ConnectedComponentError(
            "points must be an Nx3 array", input_point_count=input_count,
            knn_k=int(knn_k) if isinstance(knn_k, (int, np.integer)) else -1,
            alpha=float(alpha),
        )
    if len(x) == 0:
        raise ConnectedComponentError(
            "point cloud is empty", input_point_count=0, knn_k=int(knn_k), alpha=float(alpha)
        )
    if not np.all(np.isfinite(x)):
        raise ConnectedComponentError(
            "point cloud contains NaN or inf", input_point_count=len(x),
            knn_k=int(knn_k), alpha=float(alpha),
        )
    if isinstance(knn_k, (bool, np.bool_)) or not isinstance(knn_k, (int, np.integer)):
        raise ConnectedComponentError(
            "knn_k must be an integer", input_point_count=len(x),
            knn_k=-1, alpha=float(alpha),
        )
    k = int(knn_k)
    if k < 1:
        raise ConnectedComponentError(
            "knn_k must be >= 1", input_point_count=len(x), knn_k=k, alpha=float(alpha)
        )
    try:
        alpha_value = float(alpha)
    except (TypeError, ValueError, OverflowError) as exc:
        raise ConnectedComponentError(
            "alpha must be a finite number in (0, 10]", input_point_count=len(x),
            knn_k=k, alpha=float("nan"),
        ) from exc
    if not np.isfinite(alpha_value) or alpha_value <= 0:
        raise ConnectedComponentError(
            "alpha must be positive", input_point_count=len(x), knn_k=k, alpha=alpha_value
        )
    if alpha_value > 10:
        raise ConnectedComponentError(
            "alpha must be <= 10", input_point_count=len(x), knn_k=k, alpha=alpha_value
        )
    if len(x) <= k:
        raise ConnectedComponentError(
            "not enough points for requested K", input_point_count=len(x), knn_k=k, alpha=alpha_value
        )

    if progress_callback is not None:
        progress_callback(5.0, "KNN distances: building spatial index")
    tree = cKDTree(x)
    distances, _ = tree.query(x, k=k + 1)
    median_knn_distance = float(np.median(distances[:, -1]))
    epsilon = float(alpha_value * median_knn_distance)
    if not np.isfinite(epsilon) or epsilon <= 0:
        raise ConnectedComponentError(
            "invalid connectivity epsilon", input_point_count=len(x), knn_k=k,
            alpha=alpha_value, median_knn_distance=median_knn_distance, epsilon=epsilon,
        )

    if progress_callback is not None:
        progress_callback(30.0, "Connected Components: building epsilon graph")
    pairs = tree.query_pairs(epsilon, output_type="ndarray")
    if len(pairs) == 0:
        raise ConnectedComponentError(
            "no connected point pairs found", input_point_count=len(x), knn_k=k,
            alpha=alpha_value, median_knn_distance=median_knn_distance, epsilon=epsilon,
            component_count=len(x), largest_component_point_count=1,
        )
    rows = np.concatenate((pairs[:, 0], pairs[:, 1]))
    cols = np.concatenate((pairs[:, 1], pairs[:, 0]))
    graph = csr_matrix((np.ones(len(rows), dtype=np.uint8), (rows, cols)), shape=(len(x), len(x)))
    component_count, labels = connected_components(graph, directed=False)
    component_sizes = np.bincount(labels)
    largest_label = int(np.argmax(component_sizes))
    component_indices = np.where(labels == largest_label)[0]
    if len(component_indices) < 5:
        raise ConnectedComponentError(
            "largest connected component is too small", input_point_count=len(x), knn_k=k,
            alpha=alpha_value, median_knn_distance=median_knn_distance, epsilon=epsilon,
            component_count=int(component_count), largest_component_point_count=len(component_indices),
        )
    if progress_callback is not None:
        progress_callback(48.0, "Connected Components completed")
    result = ConnectedComponentResult(
        x[component_indices], component_indices, median_knn_distance, epsilon, int(component_count)
    )
    if return_diagnostics:
        return result
    return result.points, result.indices, result.median_knn_distance, result.epsilon
