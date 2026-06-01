import math
import numpy as np
import open3d as o3d
from scipy.spatial import cKDTree
import multiprocessing
import time

# DBSCAN 関連の定数
DBSCAN_FULL_LIMIT    = 300_000   # これ以下ならFull DBSCANを許可
DBSCAN_TARGET_POINTS = 200_000   # Downsample時の目標点数
DBSCAN_TIMEOUT_SEC   = 120       # タイムアウト上限（秒）

def decide_dbscan_mode(n_points: int) -> str:
    """
    点数に応じてDBSCANをFullモードで走らせるか、Downsampleモードにするかを判定します。
    """
    if n_points <= DBSCAN_FULL_LIMIT:
        return 'full'
    else:
        return 'downsample'

def estimate_base_spacing(points: np.ndarray, k: int = 8) -> float:
    """
    点群の基準となる点間隔を推定します。
    点数が非常に多い場合は、10万点をランダムサンプリングして高速に推定します。
    """
    n = len(points)
    if n <= 1:
        return 0.0
    
    if n > 100000:
        indices = np.random.choice(n, 100000, replace=False)
        sample_points = points[indices]
    else:
        sample_points = points
        
    tree = cKDTree(sample_points)
    query_k = min(k + 1, len(sample_points))
    dists, _ = tree.query(sample_points, k=query_k)
    if query_k == 1:
        return 0.0
    # 自身(距離0)を除いた隣接点への距離
    dists = np.atleast_2d(dists)[:, 1:]
    if dists.shape[1] == 0:
        return 0.0
    mean_dists = np.mean(dists, axis=1)
    return float(np.median(mean_dists))

def compute_sor(points: np.ndarray, tree: cKDTree = None, nb_neighbors: int = 20, std_ratio: float = 1.5) -> dict:
    """
    SOR (Statistical Outlier Removal) を計算します。
    
    Returns:
        dict: {
            'remove_mask': bool[N],
            'sor_score': float32[N]
        }
    """
    if len(points) == 0:
        return {'remove_mask': np.array([], dtype=bool), 'sor_score': np.array([], dtype=np.float32)}
    if len(points) == 1:
        return {'remove_mask': np.zeros(1, dtype=bool), 'sor_score': np.zeros(1, dtype=np.float32)}

    if tree is None:
        tree = cKDTree(points)
    query_k = min(nb_neighbors + 1, len(points))
    dists, _ = tree.query(points, k=query_k)
    dists = np.atleast_2d(dists)
    mean_dists = np.mean(dists[:, 1:], axis=1) if dists.shape[1] > 1 else np.zeros(len(points), dtype=np.float32)
    
    mean = np.mean(mean_dists)
    std = np.std(mean_dists)
    
    if std > 1e-8:
        sor_score = (mean_dists - mean) / std
    else:
        sor_score = np.zeros_like(mean_dists)
        
    remove_mask = sor_score > std_ratio
    
    return {
        'remove_mask': remove_mask.astype(bool),
        'sor_score': sor_score.astype(np.float32)
    }

def compute_ror(points: np.ndarray, base_spacing: float, tree: cKDTree = None, radius_multiplier: float = 3.0, min_neighbors: int = 8) -> dict:
    """
    ROR (Radius Outlier Removal) を計算します。
    
    Returns:
        dict: {
            'remove_mask': bool[N],
            'radius_neighbor_count': int32[N]
        }
    """
    if len(points) == 0:
        return {'remove_mask': np.array([], dtype=bool), 'radius_neighbor_count': np.array([], dtype=np.int32)}
        
    radius = base_spacing * radius_multiplier
    if tree is None:
        tree = cKDTree(points)
    counts = tree.query_ball_point(points, r=radius, return_length=True)
    
    # 自身を除いた近傍点数
    neighbor_counts = (counts - 1).clip(min=0)
    remove_mask = neighbor_counts < min_neighbors
    
    return {
        'remove_mask': remove_mask.astype(bool),
        'radius_neighbor_count': neighbor_counts.astype(np.int32)
    }

def compute_density(points: np.ndarray, tree: cKDTree = None, k: int = 8) -> dict:
    """
    各点の局所密度スコアを計算します。
    density_score = 1 / (k近傍平均距離 + 1e-6)
    
    Returns:
        dict: {
            'density_score': float32[N]
        }
    """
    if len(points) == 0:
        return {'density_score': np.array([], dtype=np.float32)}
    if len(points) == 1:
        return {'density_score': np.zeros(1, dtype=np.float32)}

    if tree is None:
        tree = cKDTree(points)
    query_k = min(k + 1, len(points))
    dists, _ = tree.query(points, k=query_k)
    dists = np.atleast_2d(dists)
    mean_dists = np.mean(dists[:, 1:], axis=1) if dists.shape[1] > 1 else np.zeros(len(points), dtype=np.float32)
    density_score = 1.0 / (mean_dists + 1e-6)
    
    return {
        'density_score': density_score.astype(np.float32)
    }

def compute_cc_noise(points: np.ndarray,
                     tree: cKDTree = None,
                     k: int = 20,
                     relative_sigma: float = 1.0,
                     absolute_error: float = 0.0,
                     use_knn: bool = True,
                     radius: float = None,
                     remove_isolated_points: bool = False,
                     chunk_size: int = 25000) -> dict:
    """
    CloudCompare風の局所平面残差ノイズフィルタを計算します。

    - KNN もしくは Radius の近傍で局所平面を推定
    - 点と局所平面の距離をスコア化
    - REL モードでは近傍残差分布に対する相対閾値
    - ABS モードでは絶対誤差閾値
    - 近傍不足点は remove_isolated_points=True の場合のみ除去
    """
    n_points = len(points)
    if n_points == 0:
        return {
            'remove_mask': np.array([], dtype=bool),
            'cc_noise_score': np.array([], dtype=np.float32)
        }

    if tree is None:
        tree = cKDTree(points)

    scores = np.zeros(n_points, dtype=np.float32)
    remove_mask = np.zeros(n_points, dtype=bool)

    if use_knn:
        if n_points < 3:
            return {
                'remove_mask': np.zeros(n_points, dtype=bool),
                'cc_noise_score': np.zeros(n_points, dtype=np.float32)
            }

        k = max(int(k), 3)
        query_k = min(k + 1, n_points)
        chunk_size = max(int(chunk_size), 1024)
        total_chunks = math.ceil(n_points / chunk_size)

        for chunk_index, start in enumerate(range(0, n_points, chunk_size), start=1):
            end = min(start + chunk_size, n_points)
            chunk_points = points[start:end]

            dists, indices = tree.query(chunk_points, k=query_k)
            if query_k == 1:
                dists = dists[:, np.newaxis]
                indices = indices[:, np.newaxis]

            neighbors = points[indices]
            means = np.mean(neighbors, axis=1)
            deviations = neighbors - means[:, np.newaxis, :]
            covs = np.matmul(deviations.transpose(0, 2, 1), deviations) / neighbors.shape[1]

            try:
                _, eigenvectors = np.linalg.eigh(covs)
            except np.linalg.LinAlgError:
                # 数値不安定なケースは念のため単点ごとにフォールバック
                eigenvectors = np.zeros((end - start, 3, 3), dtype=np.float64)
                for local_i in range(end - start):
                    try:
                        _, vecs = np.linalg.eigh(covs[local_i])
                    except np.linalg.LinAlgError:
                        vecs = np.eye(3, dtype=np.float64)
                    eigenvectors[local_i] = vecs

            normals = eigenvectors[:, :, 0]
            diff = chunk_points - means
            chunk_scores = np.abs(np.sum(diff * normals, axis=1))

            nb_dists = np.abs(np.sum(deviations * normals[:, np.newaxis, :], axis=2))
            nb_means = np.mean(nb_dists, axis=1)
            nb_stds = np.std(nb_dists, axis=1)

            chunk_remove_mask = np.zeros(end - start, dtype=bool)
            if relative_sigma > 0.0:
                threshold_rel = nb_means + relative_sigma * nb_stds
                chunk_remove_mask |= (chunk_scores > threshold_rel)
            if absolute_error > 0.0:
                chunk_remove_mask |= (chunk_scores > absolute_error)

            scores[start:end] = chunk_scores.astype(np.float32)
            remove_mask[start:end] = chunk_remove_mask

            if total_chunks > 1:
                print(f"  CC局所平面解析 {chunk_index}/{total_chunks} チャンク完了")
    else:
        if radius is None or radius <= 0.0:
            raise ValueError("CloudCompare風の Radius モードでは radius > 0 が必要です。")

        chunk_size = max(int(chunk_size), 2048)
        total_chunks = math.ceil(n_points / chunk_size)

        for chunk_index, start in enumerate(range(0, n_points, chunk_size), start=1):
            end = min(start + chunk_size, n_points)
            chunk_points = points[start:end]
            neighbor_lists = tree.query_ball_point(chunk_points, r=radius)

            for local_i, neighbor_ids in enumerate(neighbor_lists):
                global_i = start + local_i
                if len(neighbor_ids) < 3:
                    if remove_isolated_points:
                        remove_mask[global_i] = True
                    continue

                neighbors = points[np.asarray(neighbor_ids, dtype=np.int32)]
                mean = neighbors.mean(axis=0)
                deviations = neighbors - mean
                cov = deviations.T @ deviations / len(neighbor_ids)

                try:
                    _, vecs = np.linalg.eigh(cov)
                    normal = vecs[:, 0]
                except np.linalg.LinAlgError:
                    if remove_isolated_points:
                        remove_mask[global_i] = True
                    continue

                score = abs(np.dot(points[global_i] - mean, normal))
                residuals = np.abs(deviations @ normal)
                rel_threshold = residuals.mean() + relative_sigma * residuals.std() if relative_sigma > 0.0 else np.inf
                abs_threshold = absolute_error if absolute_error > 0.0 else np.inf

                scores[global_i] = np.float32(score)
                if score > min(rel_threshold, abs_threshold):
                    remove_mask[global_i] = True

            if total_chunks > 1:
                print(f"  CC局所平面解析 {chunk_index}/{total_chunks} チャンク完了")

    return {
        'remove_mask': remove_mask.astype(bool),
        'cc_noise_score': scores.astype(np.float32)
    }

def compute_dbscan(points: np.ndarray, base_spacing: float, eps_multiplier: float = 4.0,
                   min_points: int = 10, min_cluster_size: int = 200) -> dict:
    """
    DBSCANクラスタリングを同期的に実行し、ノイズ点および小クラスタ点（削除候補）を検出します。
    
    Returns:
        dict: {
            'cluster_id': int32[N],
            'remove_mask': bool[N],
            'timeout': bool
        }
    """
    n_points = len(points)
    if n_points == 0:
        return {'cluster_id': np.array([], dtype=np.int32), 'remove_mask': np.array([], dtype=bool), 'timeout': False}
        
    eps = base_spacing * eps_multiplier
    
    try:
        pcd = o3d.geometry.PointCloud()
        pcd.points = o3d.utility.Vector3dVector(points.astype(np.float64))
        # 同期的にDBSCANを実行 (Open3DのC++実装のため非常に高速)
        labels = np.array(pcd.cluster_dbscan(eps=eps, min_points=min_points, print_progress=False))
    except Exception as e:
        print(f"[Warning] DBSCAN failed: {e}")
        labels = np.full(n_points, -1, dtype=np.int32)
        remove_mask = np.zeros(n_points, dtype=bool)
        return {
            'cluster_id': labels,
            'remove_mask': remove_mask,
            'timeout': True
        }
        
    # クラスタ要素数の集計
    unique_labels, counts = np.unique(labels, return_counts=True)
    label_counts = dict(zip(unique_labels, counts))
    
    # ノイズ点 (labels == -1)
    remove_mask = (labels == -1)
    
    # 要素数が min_cluster_size 未満の小クラスタ
    small_clusters = [lbl for lbl, count in label_counts.items() if lbl >= 0 and count < min_cluster_size]
    if small_clusters:
        remove_mask = remove_mask | np.isin(labels, small_clusters)
        
    return {
        'cluster_id': labels.astype(np.int32),
        'remove_mask': remove_mask.astype(bool),
        'timeout': False
    }

def compute_white_haze(points: np.ndarray, colors: np.ndarray, brightness_min: float = 190.0, saturation_max: float = 0.20) -> dict:
    """
    点の色情報 (RGB) から、明るく低彩度な白〜灰色系のノイズ点（白モヤ）を検出します。
    """
    n_points = len(points)
    if n_points == 0 or colors is None or len(colors) == 0:
        return {
            'candidate_mask': np.zeros(n_points, dtype=bool),
            'white_haze_score': np.zeros(n_points, dtype=np.float32)
        }
    
    # colorsは uint8 [N, 3] または [N, 4] 想定
    rgbs = colors[:, :3].astype(np.float32)
    
    # brightness = (R + G + B) / 3
    brightness = np.mean(rgbs, axis=1)
    
    # saturation = (max - min) / max
    c_max = np.max(rgbs, axis=1)
    c_min = np.min(rgbs, axis=1)
    
    # ゼロ除算防止
    saturation = np.zeros(n_points, dtype=np.float32)
    valid_mask = c_max > 0.0
    saturation[valid_mask] = (c_max[valid_mask] - c_min[valid_mask]) / c_max[valid_mask]
    
    # brightness >= brightness_min かつ saturation <= saturation_max の点をノイズ判定
    candidate_mask = (brightness >= brightness_min) & (saturation <= saturation_max)
    
    # score = brightness * (1.0 - saturation)
    white_haze_score = brightness * (1.0 - saturation)
    
    return {
        'candidate_mask': candidate_mask.astype(bool),
        'white_haze_score': white_haze_score.astype(np.float32)
    }

def run_all_filters(points: np.ndarray, params: dict, enabled_filters: set = None, mode: str = 'full', colors: np.ndarray = None) -> dict:
    """
    指定されたパラメータとモードに従い、すべてのフィルタを実行してマージ結果を返します。
    後方互換性のためのラッパー関数です。
    """
    if enabled_filters is None:
        enabled_filters = {"sor", "cc_noise", "dbscan", "white_haze"}
    
    from filter_pipeline import build_default_pipeline
    pipeline = build_default_pipeline(params, enabled_filters, colors)
    return pipeline.run(points, colors)

