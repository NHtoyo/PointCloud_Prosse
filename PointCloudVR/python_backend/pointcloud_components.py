"""Exact, memory-bounded connected-component operations for Nx3 point clouds."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Callable

import numpy as np
from scipy.spatial import cKDTree


ProgressCallback = Callable[[float, str], None] | None

DEFAULT_MAX_GRAPH_EDGES = 25_000_000
DEFAULT_MAX_NEIGHBORS_PER_POINT = 250_000
_KNN_CHUNK_SIZE = 100_000
_NEIGHBOR_COUNT_CHUNK_SIZE = 100_000
_UNION_BATCH_NEIGHBORS = 500_000


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
        edge_count: int | None = None,
        max_degree: int | None = None,
    ):
        super().__init__(message)
        self.input_point_count = input_point_count
        self.knn_k = knn_k
        self.alpha = alpha
        self.median_knn_distance = median_knn_distance
        self.epsilon = epsilon
        self.component_count = component_count
        self.largest_component_point_count = largest_component_point_count
        self.edge_count = edge_count
        self.max_degree = max_degree


@dataclass
class ConnectedComponentLabelsResult:
    # Each label is the smallest original index belonging to that component.
    labels: np.ndarray
    median_knn_distance: float
    epsilon: float
    component_count: int
    edge_count: int
    max_degree: int


@dataclass
class ConnectedComponentSelectionResult:
    keep_mask: np.ndarray
    kept_component_label: int
    kept_point_count: int
    labels: np.ndarray
    median_knn_distance: float
    epsilon: float
    component_count: int
    edge_count: int
    max_degree: int


@dataclass
class ConnectedComponentResult:
    points: np.ndarray
    indices: np.ndarray
    median_knn_distance: float
    epsilon: float
    component_count: int


def _find(parent: np.ndarray, value: int) -> int:
    root = value
    while int(parent[root]) != root:
        root = int(parent[root])
    while value != root:
        next_value = int(parent[value])
        parent[value] = root
        value = next_value
    return root


def connected_component_labels(
    points: np.ndarray,
    knn_k: int = 8,
    alpha: float = 2.5,
    progress_callback: ProgressCallback = None,
    *,
    max_graph_edges: int = DEFAULT_MAX_GRAPH_EDGES,
    max_neighbors_per_point: int = DEFAULT_MAX_NEIGHBORS_PER_POINT,
) -> ConnectedComponentLabelsResult:
    """Label the exact epsilon graph without materializing its edge list.

    The graph definition remains epsilon = alpha * median(kth-neighbor distance).
    KNN queries, graph degree counting, and edge unions are chunked/streamed. A
    preflight rejects graphs whose exact edge count or single-point degree would
    exceed the configured resource budget; no approximate graph is substituted.
    """
    x = np.asarray(points, dtype=np.float64)
    input_count = len(x) if x.ndim > 0 else 0

    try:
        k = int(knn_k)
    except (TypeError, ValueError, OverflowError):
        k = -1
    try:
        alpha_value = float(alpha)
    except (TypeError, ValueError, OverflowError):
        alpha_value = float("nan")

    def fail(message: str, **diagnostics):
        raise ConnectedComponentError(
            message,
            input_point_count=input_count,
            knn_k=k,
            alpha=alpha_value,
            **diagnostics,
        )

    if x.ndim != 2 or x.shape[1] != 3:
        fail("points must be an Nx3 array")
    if input_count == 0:
        fail("point cloud is empty")
    if input_count > np.iinfo(np.int32).max:
        fail("point count exceeds the exact in-memory component label limit")
    if not np.all(np.isfinite(x)):
        fail("point cloud contains NaN or inf")
    if isinstance(knn_k, (bool, np.bool_)) or not isinstance(knn_k, (int, np.integer)):
        fail("knn_k must be an integer")
    if k < 1:
        fail("knn_k must be >= 1")
    if not np.isfinite(alpha_value) or alpha_value <= 0 or alpha_value > 10:
        fail("alpha must be in (0, 10]")
    if input_count <= k:
        fail("not enough points for requested K")
    if isinstance(max_graph_edges, (bool, np.bool_)) or not isinstance(max_graph_edges, (int, np.integer)) or max_graph_edges < 0:
        fail("max_graph_edges must be a non-negative integer")
    if isinstance(max_neighbors_per_point, (bool, np.bool_)) or not isinstance(max_neighbors_per_point, (int, np.integer)) or max_neighbors_per_point < 1:
        fail("max_neighbors_per_point must be a positive integer")

    if progress_callback is not None:
        progress_callback(0.0, "Building exact KNN index")
    tree = cKDTree(x)
    kth_distances = np.empty(input_count, dtype=np.float64)
    for start in range(0, input_count, _KNN_CHUNK_SIZE):
        stop = min(input_count, start + _KNN_CHUNK_SIZE)
        distances, _ = tree.query(x[start:stop], k=k + 1, workers=1)
        kth_distances[start:stop] = distances[:, -1]
        if progress_callback is not None:
            progress_callback(5.0 + 20.0 * stop / input_count, "Measuring kth-neighbor distances")
    median_knn_distance = float(np.median(kth_distances))
    del kth_distances
    epsilon = float(alpha_value * median_knn_distance)
    if not np.isfinite(epsilon) or epsilon <= 0.0:
        fail(
            "invalid connectivity epsilon",
            median_knn_distance=median_knn_distance,
            epsilon=epsilon,
        )

    # Preflight exact degrees using SciPy's count-only API. This avoids allocating
    # an O(E) pair array before knowing whether a dense graph is safe to process.
    directed_edge_count = 0
    max_degree = 0
    degree_counts = np.empty(input_count, dtype=np.int32)
    for start in range(0, input_count, _NEIGHBOR_COUNT_CHUNK_SIZE):
        stop = min(input_count, start + _NEIGHBOR_COUNT_CHUNK_SIZE)
        degrees = np.asarray(
            tree.query_ball_point(x[start:stop], epsilon, return_length=True, workers=1),
            dtype=np.int64,
        )
        if degrees.size:
            degree_counts[start:stop] = degrees
            chunk_max_degree = int(np.max(degrees))
            max_degree = max(max_degree, chunk_max_degree)
            if chunk_max_degree > max_neighbors_per_point:
                fail(
                    f"epsilon graph point degree {chunk_max_degree} exceeds the safety limit "
                    f"{max_neighbors_per_point}; no points were changed",
                    median_knn_distance=median_knn_distance,
                    epsilon=epsilon,
                    max_degree=chunk_max_degree,
                )
        directed_edge_count += int(np.sum(degrees, dtype=np.int64)) - (stop - start)
        estimated_edges = directed_edge_count // 2
        if estimated_edges > max_graph_edges:
            fail(
                f"exact epsilon graph exceeds the edge safety limit {max_graph_edges}; "
                "no points were changed",
                median_knn_distance=median_knn_distance,
                epsilon=epsilon,
                edge_count=estimated_edges,
                max_degree=max_degree,
            )
        if progress_callback is not None:
            progress_callback(25.0 + 20.0 * stop / input_count, "Checking exact graph size")

    edge_count = directed_edge_count // 2
    if edge_count > max_graph_edges:
        fail(
            f"exact epsilon graph exceeds the edge safety limit {max_graph_edges}; no points were changed",
            median_knn_distance=median_knn_distance,
            epsilon=epsilon,
            edge_count=edge_count,
            max_degree=max_degree,
        )

    # Union-Find stores O(N) arrays and streams one point's exact neighbors at a
    # time. Equal-size roots are chosen by the smallest input index, making ties
    # deterministic independently of dictionary/hash iteration order.
    parent = np.arange(input_count, dtype=np.int32)
    sizes = np.ones(input_count, dtype=np.int32)
    minimum_index = np.arange(input_count, dtype=np.int32)
    if progress_callback is not None:
        progress_callback(45.0, "Merging exact epsilon-neighbor edges")

    point_start = 0
    while point_start < input_count:
        point_stop = point_start
        neighbor_budget = 0
        while point_stop < input_count:
            point_neighbors = int(degree_counts[point_stop])
            if point_stop > point_start and neighbor_budget + point_neighbors > _UNION_BATCH_NEIGHBORS:
                break
            neighbor_budget += point_neighbors
            point_stop += 1

        neighborhoods = tree.query_ball_point(
            x[point_start:point_stop], epsilon, return_sorted=False, workers=1
        )
        for local_index, neighbors in enumerate(neighborhoods):
            point_index = point_start + local_index
            for neighbor in neighbors:
                neighbor_index = int(neighbor)
                if neighbor_index <= point_index:
                    continue
                root_a = _find(parent, point_index)
                root_b = _find(parent, neighbor_index)
                if root_a == root_b:
                    continue
                size_a = int(sizes[root_a])
                size_b = int(sizes[root_b])
                min_a = int(minimum_index[root_a])
                min_b = int(minimum_index[root_b])
                if size_a < size_b or (size_a == size_b and min_a > min_b):
                    root_a, root_b = root_b, root_a
                    size_a, size_b = size_b, size_a
                    min_a, min_b = min_b, min_a
                parent[root_b] = root_a
                sizes[root_a] = size_a + size_b
                minimum_index[root_a] = min(min_a, min_b)
        del neighborhoods
        point_start = point_stop
        if progress_callback is not None:
            progress_callback(
                45.0 + 50.0 * point_start / input_count,
                "Merging exact epsilon-neighbor edges",
            )

    labels = np.empty(input_count, dtype=np.int32)
    for index in range(input_count):
        root = _find(parent, index)
        labels[index] = minimum_index[root]
    component_count = int(np.unique(labels).size)
    if progress_callback is not None:
        progress_callback(100.0, "Connected components completed")
    return ConnectedComponentLabelsResult(
        labels=labels,
        median_knn_distance=median_knn_distance,
        epsilon=epsilon,
        component_count=component_count,
        edge_count=edge_count,
        max_degree=max_degree,
    )


def select_connected_component(
    points: np.ndarray,
    knn_k: int = 8,
    alpha: float = 2.5,
    mode: str = "largest",
    selection_mask: np.ndarray | None = None,
    progress_callback: ProgressCallback = None,
    *,
    max_graph_edges: int = DEFAULT_MAX_GRAPH_EDGES,
    max_neighbors_per_point: int = DEFAULT_MAX_NEIGHBORS_PER_POINT,
) -> ConnectedComponentSelectionResult:
    """Return the exact component to keep, selected by size or input selection."""
    try:
        point_count = len(points)
    except TypeError:
        point_count = 0
    try:
        diagnostic_k = int(knn_k)
    except (TypeError, ValueError, OverflowError):
        diagnostic_k = -1
    try:
        diagnostic_alpha = float(alpha)
    except (TypeError, ValueError, OverflowError):
        diagnostic_alpha = float("nan")
    if mode not in ("largest", "selected"):
        raise ValueError("component keep mode must be 'largest' or 'selected'")
    if mode == "selected":
        if selection_mask is None:
            raise ConnectedComponentError(
                "selected component mode requires an input-index selection mask",
                input_point_count=point_count, knn_k=diagnostic_k, alpha=diagnostic_alpha,
            )
        selected = np.asarray(selection_mask)
        if selected.ndim != 1 or len(selected) != len(points):
            raise ConnectedComponentError(
                "selection mask length must exactly match the point count",
                input_point_count=point_count, knn_k=diagnostic_k, alpha=diagnostic_alpha,
            )
        if not np.issubdtype(selected.dtype, np.bool_):
            if not np.all(np.isfinite(selected)) or np.any((selected != 0) & (selected != 1)):
                raise ConnectedComponentError(
                    "selection mask values must be boolean or 0/1",
                    input_point_count=point_count, knn_k=diagnostic_k, alpha=diagnostic_alpha,
                )
        if not np.any(selected):
            raise ConnectedComponentError(
                "selected component mode has no active selected points",
                input_point_count=point_count, knn_k=diagnostic_k, alpha=diagnostic_alpha,
            )

    labels_result = connected_component_labels(
        points,
        knn_k=knn_k,
        alpha=alpha,
        progress_callback=progress_callback,
        max_graph_edges=max_graph_edges,
        max_neighbors_per_point=max_neighbors_per_point,
    )
    labels = labels_result.labels
    if mode == "largest":
        component_labels, component_sizes = np.unique(labels, return_counts=True)
        # np.unique sorts labels, which are each component's first input index.
        selected_component_label = int(component_labels[int(np.argmax(component_sizes))])
    elif mode == "selected":
        if selection_mask is None:
            raise ConnectedComponentError(
                "selected component mode requires an input-index selection mask",
                input_point_count=len(labels), knn_k=int(knn_k), alpha=float(alpha),
                median_knn_distance=labels_result.median_knn_distance,
                epsilon=labels_result.epsilon, component_count=labels_result.component_count,
            )
        selected = np.asarray(selection_mask)
        if selected.ndim != 1 or len(selected) != len(labels):
            raise ConnectedComponentError(
                "selection mask length must exactly match the point count",
                input_point_count=len(labels), knn_k=int(knn_k), alpha=float(alpha),
                median_knn_distance=labels_result.median_knn_distance,
                epsilon=labels_result.epsilon, component_count=labels_result.component_count,
            )
        selected_indices = np.flatnonzero(selected.astype(bool, copy=False))
        if len(selected_indices) == 0:
            raise ConnectedComponentError(
                "selected component mode has no active selected points",
                input_point_count=len(labels), knn_k=int(knn_k), alpha=float(alpha),
                median_knn_distance=labels_result.median_knn_distance,
                epsilon=labels_result.epsilon, component_count=labels_result.component_count,
            )
        selected_labels = np.unique(labels[selected_indices])
        if len(selected_labels) != 1:
            raise ConnectedComponentError(
                f"selected points span {len(selected_labels)} connected components; "
                "select points from exactly one component",
                input_point_count=len(labels), knn_k=int(knn_k), alpha=float(alpha),
                median_knn_distance=labels_result.median_knn_distance,
                epsilon=labels_result.epsilon, component_count=labels_result.component_count,
            )
        selected_component_label = int(selected_labels[0])
    else:  # guarded above
        raise AssertionError("unreachable component mode")

    keep_mask = labels == selected_component_label
    return ConnectedComponentSelectionResult(
        keep_mask=keep_mask,
        kept_component_label=selected_component_label,
        kept_point_count=int(np.count_nonzero(keep_mask)),
        labels=labels,
        median_knn_distance=labels_result.median_knn_distance,
        epsilon=labels_result.epsilon,
        component_count=labels_result.component_count,
        edge_count=labels_result.edge_count,
        max_degree=labels_result.max_degree,
    )


def largest_connected_component(
    points: np.ndarray,
    knn_k: int = 8,
    alpha: float = 2.5,
    progress_callback: ProgressCallback = None,
    *,
    return_diagnostics: bool = False,
) -> tuple[np.ndarray, np.ndarray, float, float] | ConnectedComponentResult:
    """Keep the largest epsilon-graph component, preserving the legacy API."""
    result = select_connected_component(
        points,
        knn_k=knn_k,
        alpha=alpha,
        mode="largest",
        progress_callback=progress_callback,
    )
    x = np.asarray(points, dtype=np.float64)
    indices = np.flatnonzero(result.keep_mask)
    if len(indices) < 5:
        raise ConnectedComponentError(
            "largest connected component is too small",
            input_point_count=len(x),
            knn_k=int(knn_k),
            alpha=float(alpha),
            median_knn_distance=result.median_knn_distance,
            epsilon=result.epsilon,
            component_count=result.component_count,
            largest_component_point_count=len(indices),
            edge_count=result.edge_count,
            max_degree=result.max_degree,
        )
    component_result = ConnectedComponentResult(
        x[indices], indices, result.median_knn_distance, result.epsilon, result.component_count
    )
    if return_diagnostics:
        return component_result
    return (
        component_result.points,
        component_result.indices,
        component_result.median_knn_distance,
        component_result.epsilon,
    )
