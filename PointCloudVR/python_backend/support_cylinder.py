import json
import os
import time
from dataclasses import dataclass

import numpy as np


@dataclass
class SupportCylinderParams:
    tube_multiplier: float = 4.0
    color_tolerance: float = 90.0
    saturation_slack: float = 0.25
    min_seed_points: int = 12
    max_empty_bins: int = 3
    height_bin_multiplier: float = 4.0


def _progress(pct: float, message: str) -> None:
    print(f"[Progress] {pct:.1f} {message}", flush=True)


def _rgb_distance(colors: np.ndarray, seed_color: np.ndarray) -> np.ndarray:
    diff = colors.astype(np.float32) - seed_color.astype(np.float32)
    return np.sqrt(np.sum(diff * diff, axis=1))


def _saturation(colors: np.ndarray) -> np.ndarray:
    c = colors.astype(np.float32)
    max_c = np.max(c, axis=1)
    min_c = np.min(c, axis=1)
    return np.divide(max_c - min_c, np.maximum(max_c, 1.0))


def _estimate_seed_spacing(seed_points: np.ndarray, max_samples: int = 1024) -> float:
    n = len(seed_points)
    if n < 2:
        return 0.0
    if n > max_samples:
        sample_idx = np.linspace(0, n - 1, max_samples).astype(np.int64)
        pts = seed_points[sample_idx]
    else:
        pts = seed_points

    # Seed点だけなので単純な総当たりで十分。外れ値を避けるため中央値を使う。
    diff = pts[:, None, :] - pts[None, :, :]
    dist2 = np.sum(diff * diff, axis=2)
    np.fill_diagonal(dist2, np.inf)
    nearest = np.sqrt(np.min(dist2, axis=1))
    nearest = nearest[np.isfinite(nearest)]
    if nearest.size == 0:
        return 0.0
    return float(np.median(nearest))


def _principal_axis(seed_points: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    center = seed_points.mean(axis=0)
    centered = seed_points - center
    _, _, vh = np.linalg.svd(centered, full_matrices=False)
    axis = vh[0].astype(np.float32)
    norm = np.linalg.norm(axis)
    if norm < 1e-8:
        axis = np.array([0.0, 1.0, 0.0], dtype=np.float32)
    else:
        axis = axis / norm
    return center.astype(np.float32), axis


def _line_distance(points: np.ndarray, center: np.ndarray, axis: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    rel = points - center
    height = rel @ axis
    closest = center + height[:, None] * axis[None, :]
    dist = np.linalg.norm(points - closest, axis=1)
    return dist.astype(np.float32), height.astype(np.float32)


def _grow_bins_from_seed(seed_bins: np.ndarray, candidate_bins: np.ndarray, max_empty_bins: int) -> set[int]:
    if seed_bins.size == 0:
        return set()

    candidate_set = set(int(v) for v in np.unique(candidate_bins))
    accepted: set[int] = set()
    frontier = [int(v) for v in np.unique(seed_bins)]
    for v in frontier:
        if v in candidate_set:
            accepted.add(v)

    if not accepted:
        accepted.add(int(np.median(seed_bins)))

    lo = min(accepted)
    hi = max(accepted)

    empty = 0
    b = lo - 1
    while empty <= max_empty_bins:
        if b in candidate_set:
            accepted.add(b)
            empty = 0
        else:
            empty += 1
        b -= 1

    empty = 0
    b = hi + 1
    while empty <= max_empty_bins:
        if b in candidate_set:
            accepted.add(b)
            empty = 0
        else:
            empty += 1
        b += 1

    return accepted


def extract_support_mask(
    points: np.ndarray,
    colors: np.ndarray,
    seed_indices: np.ndarray,
    params: SupportCylinderParams,
) -> tuple[np.ndarray, dict]:
    start = time.time()
    n = len(points)
    mask = np.zeros(n, dtype=np.uint8)

    seed_indices = np.unique(seed_indices.astype(np.int64))
    seed_indices = seed_indices[(seed_indices >= 0) & (seed_indices < n)]
    if seed_indices.size < params.min_seed_points:
        raise ValueError(f"支柱の種点が少なすぎます: {seed_indices.size} 点。最低 {params.min_seed_points} 点必要です。")

    _progress(15.0, "支柱の種点から軸とスケールを推定中...")
    seed_points = points[seed_indices]
    seed_colors = colors[seed_indices]
    center, axis = _principal_axis(seed_points)
    spacing = _estimate_seed_spacing(seed_points)
    if spacing <= 1e-8:
        raise ValueError("選択点の点間隔を推定できません。支柱の一部をもう少し広めに選択してください。")

    seed_dist, seed_height = _line_distance(seed_points, center, axis)
    seed_radius = float(np.percentile(seed_dist, 90))
    seed_height_span = float(np.percentile(seed_height, 90) - np.percentile(seed_height, 10))
    if seed_height_span < spacing * 4.0 or seed_height_span < max(seed_radius * 2.0, spacing * 2.0):
        raise ValueError(
            "選択点が細長い支柱片に見えません。葉や果実が混ざっていない支柱の一部を細長く選択してください。"
        )

    seed_color = np.median(seed_colors.astype(np.float32), axis=0)
    seed_sat = float(np.median(_saturation(seed_colors)))
    tube_radius = max(spacing * params.tube_multiplier, seed_radius + spacing * 2.0)
    bin_size = max(spacing * params.height_bin_multiplier, tube_radius)

    _progress(35.0, "支柱軸の周囲から候補点を抽出中...")
    dist, height = _line_distance(points, center, axis)
    geom_candidate = dist <= tube_radius

    rgb_dist = _rgb_distance(colors, seed_color)
    if seed_sat <= 0.25:
        sat = _saturation(colors)
        color_candidate = (sat <= seed_sat + params.saturation_slack) & (rgb_dist <= params.color_tolerance * 2.5)
    else:
        color_candidate = rgb_dist <= params.color_tolerance

    candidate = geom_candidate & color_candidate
    if not np.any(candidate):
        raise ValueError("支柱候補が見つかりません。色許容または太さ倍率を上げてください。")

    _progress(65.0, "種点から高さ方向に連続している支柱区間を追跡中...")
    seed_bins = np.floor(seed_height / bin_size).astype(np.int64)
    candidate_bins = np.floor(height[candidate] / bin_size).astype(np.int64)
    accepted_bins = _grow_bins_from_seed(seed_bins, candidate_bins, params.max_empty_bins)
    all_bins = np.floor(height / bin_size).astype(np.int64)
    bin_candidate = np.isin(all_bins, list(accepted_bins))

    final = candidate & bin_candidate
    # seedは必ず残す。ユーザーが選んだ支柱片が消えると確認しづらい。
    final[seed_indices] = True
    mask[final] = 1

    report = {
        "point_count": int(n),
        "seed_count": int(seed_indices.size),
        "candidate_count": int(np.count_nonzero(candidate)),
        "selected_count": int(np.count_nonzero(final)),
        "spacing": spacing,
        "seed_radius_p90": seed_radius,
        "seed_height_span_p80": seed_height_span,
        "tube_radius": float(tube_radius),
        "bin_size": float(bin_size),
        "seed_color_rgb_median": [float(v) for v in seed_color],
        "seed_saturation_median": seed_sat,
        "axis": [float(v) for v in axis],
        "center": [float(v) for v in center],
        "parameters": params.__dict__,
        "elapsed_sec": time.time() - start,
    }
    return mask, report


def save_support_result(output_dir: str, mask: np.ndarray, report: dict) -> None:
    os.makedirs(output_dir, exist_ok=True)
    mask.astype("|u1", copy=False).tofile(os.path.join(output_dir, "support_mask.bin"))
    with open(os.path.join(output_dir, "support_report.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, ensure_ascii=False, indent=2)
