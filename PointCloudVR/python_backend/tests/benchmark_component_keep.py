"""Opt-in Windows benchmark for exact connected-component resource checks."""

import argparse
import json
import math
from pathlib import Path
import sys
import time

import numpy as np
import psutil

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from pointcloud_components import ConnectedComponentError, select_connected_component


def make_grid(point_count: int) -> np.ndarray:
    side = math.ceil(point_count ** (1.0 / 3.0))
    points = np.empty((point_count, 3), dtype=np.float32)
    chunk_size = 1_000_000
    for start in range(0, point_count, chunk_size):
        stop = min(point_count, start + chunk_size)
        index = np.arange(start, stop, dtype=np.int64)
        points[start:stop, 0] = index % side
        points[start:stop, 1] = (index // side) % side
        points[start:stop, 2] = index // (side * side)
    return points


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--points", type=int, required=True)
    parser.add_argument("--knn-k", type=int, default=8)
    parser.add_argument("--alpha", type=float, default=2.5)
    parser.add_argument("--max-edges", type=int, default=25_000_000)
    args = parser.parse_args()

    points = make_grid(args.points)
    process = psutil.Process()
    before_mib = process.memory_info().rss / (1024 * 1024)
    started = time.perf_counter()
    try:
        result = select_connected_component(
            points,
            knn_k=args.knn_k,
            alpha=args.alpha,
            mode="largest",
            max_graph_edges=args.max_edges,
        )
        outcome = {
            "status": "completed",
            "component_count": result.component_count,
            "edge_count": result.edge_count,
            "kept_point_count": result.kept_point_count,
            "max_degree": result.max_degree,
            "epsilon": result.epsilon,
        }
    except ConnectedComponentError as error:
        outcome = {
            "status": "safely_rejected",
            "error": str(error),
            "edge_count": error.edge_count,
            "max_degree": error.max_degree,
            "epsilon": error.epsilon,
        }
    outcome.update({
        "input_point_count": args.points,
        "K": args.knn_k,
        "alpha": args.alpha,
        "elapsed_seconds": time.perf_counter() - started,
        "baseline_rss_mib": before_mib,
        "peak_working_set_mib": getattr(process.memory_info(), "peak_wset", None),
        "current_rss_mib": process.memory_info().rss / (1024 * 1024),
    })
    if outcome["peak_working_set_mib"] is not None:
        outcome["peak_working_set_mib"] /= 1024 * 1024
    print(json.dumps(outcome, ensure_ascii=False, allow_nan=False))


if __name__ == "__main__":
    main()
