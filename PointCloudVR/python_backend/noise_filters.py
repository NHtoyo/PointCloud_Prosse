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
    """
    n_points = len(points)
    if enabled_filters is None:
        enabled_filters = {"sor", "cc_noise", "dbscan", "white_haze"}
    
    # 1. White Haze Filter (幾何フィルタより前の前処理。即削除ではなく候補マスクのみ作る)
    if "white_haze" in enabled_filters:
        print("White Haze (空中白モヤ) 除去フィルタを実行中...")
        t0 = time.time()
        wh_params = params.get('white_haze', {'brightness_min': 190.0, 'saturation_max': 0.20})
        wh_res = compute_white_haze(
            points,
            colors,
            brightness_min=wh_params.get('brightness_min', 190.0),
            saturation_max=wh_params.get('saturation_max', 0.20)
        )
        print(f"White Haze 完了. (所要時間: {time.time() - t0:.2f}秒)")
    else:
        wh_res = {
            'candidate_mask': np.zeros(n_points, dtype=bool),
            'white_haze_score': np.zeros(n_points, dtype=np.float32)
        }
    white_haze_candidate_mask = wh_res['candidate_mask']
    active_mask = ~white_haze_candidate_mask
    active_points = points[active_mask]
    active_count = len(active_points)

    # 2. 共通の cKDTree を1度だけ構築 (全体の90%以上の近傍検索処理で再利用)
    tree = None
    if active_count > 0 and any(f in enabled_filters for f in ["sor", "ror", "density", "cc_noise"]):
        print("近傍探索用の共通 cKDTree を構築中...")
        t0 = time.time()
        tree = cKDTree(active_points)
        print(f"cKDTree 構築完了. (所要時間: {time.time() - t0:.2f}秒)")

    # 3. 基準点間隔の推定 (SOR/ROR/DBSCANの各種半径計算に使用されるため、いずれかが有効な場合に計算)
    if active_count > 0 and any(f in enabled_filters for f in ["sor", "ror", "dbscan"]):
        print("基準点間隔を推定中...")
        t0 = time.time()
        base_spacing = estimate_base_spacing(active_points)
        print(f"基準点間隔の推定完了: {base_spacing:.6f} m (所要時間: {time.time() - t0:.2f}秒)")
    else:
        base_spacing = 0.01 # デフォルトのフォールバック値

    # 4. SOR
    if "sor" in enabled_filters and active_count > 0:
        print("SOR (統計的ノイズ除去) を実行中...")
        t0 = time.time()
        sor_params = params.get('sor', {'nb_neighbors': 20, 'std_ratio': 1.5})
        sor_partial = compute_sor(active_points, tree, nb_neighbors=sor_params['nb_neighbors'], std_ratio=sor_params['std_ratio'])
        sor_res = {
            'remove_mask': np.zeros(n_points, dtype=bool),
            'sor_score': np.zeros(n_points, dtype=np.float32)
        }
        sor_res['remove_mask'][active_mask] = sor_partial['remove_mask']
        sor_res['sor_score'][active_mask] = sor_partial['sor_score']
        print(f"SOR 完了. (所要時間: {time.time() - t0:.2f}秒)")
    else:
        sor_res = {
            'remove_mask': np.zeros(n_points, dtype=bool),
            'sor_score': np.zeros(n_points, dtype=np.float32)
        }
    
    # 5. ROR
    if "ror" in enabled_filters and active_count > 0:
        print("ROR (半径外れ値除去) を実行中...")
        t0 = time.time()
        ror_params = params.get('ror', {'radius_multiplier': 3.0, 'min_neighbors': 8})
        ror_partial = compute_ror(active_points, base_spacing, tree, radius_multiplier=ror_params['radius_multiplier'], min_neighbors=ror_params['min_neighbors'])
        ror_res = {
            'remove_mask': np.zeros(n_points, dtype=bool),
            'radius_neighbor_count': np.zeros(n_points, dtype=np.int32)
        }
        ror_res['remove_mask'][active_mask] = ror_partial['remove_mask']
        ror_res['radius_neighbor_count'][active_mask] = ror_partial['radius_neighbor_count']
        print(f"ROR 完了. (所要時間: {time.time() - t0:.2f}秒)")
    else:
        ror_res = {
            'remove_mask': np.zeros(n_points, dtype=bool),
            'radius_neighbor_count': np.zeros(n_points, dtype=np.int32)
        }
    
    # 6. 密度スコア
    if "density" in enabled_filters and active_count > 0:
        print("低密度ノイズ判定を実行中...")
        t0 = time.time()
        density_params = params.get('density', {'k': 8})
        density_partial = compute_density(active_points, tree, k=density_params['k'])
        density_res = {
            'density_score': np.zeros(n_points, dtype=np.float32)
        }
        density_res['density_score'][active_mask] = density_partial['density_score']
        print(f"低密度ノイズ判定完了. (所要時間: {time.time() - t0:.2f}秒)")
    else:
        density_res = {
            'density_score': np.zeros(n_points, dtype=np.float32)
        }

    # 6.5. CC風ノイズフィルタ
    if "cc_noise" in enabled_filters and active_count > 0:
        print("CC風局所平面ノイズフィルタを実行中...")
        t0 = time.time()
        cc_params = params.get('cc_noise', {'k': 20, 'relative_sigma': 1.0, 'absolute_error': 0.0})
        cc_partial = compute_cc_noise(
            active_points,
            tree,
            k=cc_params.get('k', 20),
            relative_sigma=cc_params.get('relative_sigma', 1.0),
            absolute_error=cc_params.get('absolute_error', 0.0),
            use_knn=cc_params.get('use_knn', True),
            radius=cc_params.get('radius', None),
            remove_isolated_points=cc_params.get('remove_isolated_points', False),
            chunk_size=cc_params.get('chunk_size', 25000)
        )
        cc_res = {
            'remove_mask': np.zeros(n_points, dtype=bool),
            'cc_noise_score': np.zeros(n_points, dtype=np.float32)
        }
        cc_res['remove_mask'][active_mask] = cc_partial['remove_mask']
        cc_res['cc_noise_score'][active_mask] = cc_partial['cc_noise_score']
        print(f"CC風ノイズフィルタ完了. (所要時間: {time.time() - t0:.2f}秒)")
    else:
        cc_res = {
            'remove_mask': np.zeros(n_points, dtype=bool),
            'cc_noise_score': np.zeros(n_points, dtype=np.float32)
        }
    
    # 7. DBSCAN (自動Downsample判定とタイムアウト管理)
    dbscan_params = params.get('dbscan', {
        'eps_multiplier': 4.0,
        'min_points': 10,
        'min_cluster_size': 200,
        'target_points': 200000
    })
    
    dbscan_mode = 'full'
    dbscan_voxel_size = None
    dbscan_analysis_count = active_count
    dbscan_timeout = False
    
    if "dbscan" in enabled_filters and active_count > 0:
        print("DBSCAN (クラスタノイズ除去) を実行中...")
        t0 = time.time()
        points_for_dbscan = active_points

        # モード決定
        if mode == 'full':
            dbscan_mode = decide_dbscan_mode(active_count)
        else:
            dbscan_mode = 'downsample'
            
        if dbscan_mode == 'downsample':
            # 自動ダウンサンプルの算出
            target = dbscan_params.get('target_points', 200000)
            if active_count > target:
                ratio = active_count / target
                dbscan_voxel_size = float(base_spacing * (ratio ** (1.0 / 3.0)))
                
                pcd = o3d.geometry.PointCloud()
                pcd.points = o3d.utility.Vector3dVector(points_for_dbscan.astype(np.float64))
                pcd_ds = pcd.voxel_down_sample(dbscan_voxel_size)
                points_ds = np.asarray(pcd_ds.points).astype(np.float32)
                dbscan_analysis_count = len(points_ds)
                
                # ダウンサンプルされた点群でDBSCAN実行
                dbscan_res = compute_dbscan(
                    points_ds,
                    base_spacing,
                    eps_multiplier=dbscan_params['eps_multiplier'],
                    min_points=dbscan_params['min_points'],
                    min_cluster_size=dbscan_params['min_cluster_size']
                )
                dbscan_timeout = dbscan_res['timeout']
                
                # 元の点数 N に対する結果へ伝播（KDTree 1-NN）
                if len(points_ds) > 0 and active_count > 0:
                    tree_ds = cKDTree(points_ds)
                    _, nn_indices = tree_ds.query(points_for_dbscan, k=1)
                    cluster_id = np.full(n_points, -1, dtype=np.int32)
                    remove_mask_dbscan = np.zeros(n_points, dtype=bool)
                    cluster_id[active_mask] = dbscan_res['cluster_id'][nn_indices]
                    remove_mask_dbscan[active_mask] = dbscan_res['remove_mask'][nn_indices]
                else:
                    cluster_id = np.full(n_points, -1, dtype=np.int32)
                    remove_mask_dbscan = np.zeros(n_points, dtype=bool)
            else:
                # 点数が目標値以下であればそのままFull実行
                dbscan_mode = 'full'
                dbscan_res = compute_dbscan(
                    points_for_dbscan,
                    base_spacing,
                    eps_multiplier=dbscan_params['eps_multiplier'],
                    min_points=dbscan_params['min_points'],
                    min_cluster_size=dbscan_params['min_cluster_size']
                )
                cluster_id = np.full(n_points, -1, dtype=np.int32)
                remove_mask_dbscan = np.zeros(n_points, dtype=bool)
                cluster_id[active_mask] = dbscan_res['cluster_id']
                remove_mask_dbscan[active_mask] = dbscan_res['remove_mask']
                dbscan_timeout = dbscan_res['timeout']
        else:
            # Fullモードでの実行
            dbscan_res = compute_dbscan(
                points_for_dbscan,
                base_spacing,
                eps_multiplier=dbscan_params['eps_multiplier'],
                min_points=dbscan_params['min_points'],
                min_cluster_size=dbscan_params['min_cluster_size']
            )
            cluster_id = np.full(n_points, -1, dtype=np.int32)
            remove_mask_dbscan = np.zeros(n_points, dtype=bool)
            cluster_id[active_mask] = dbscan_res['cluster_id']
            remove_mask_dbscan[active_mask] = dbscan_res['remove_mask']
            dbscan_timeout = dbscan_res['timeout']
        print(f"DBSCAN 完了. モード: {dbscan_mode}, 解析点数: {dbscan_analysis_count} (所要時間: {time.time() - t0:.2f}秒)")
    else:
        cluster_id = np.full(n_points, -1, dtype=np.int32)
        remove_mask_dbscan = np.zeros(n_points, dtype=bool)
        
    # 6. 低密度ノイズ判定マスク (低密度閾値が指定されている場合のみ)
    density_threshold = params.get('density', {}).get('threshold', 0.0)
    remove_mask_density = np.zeros(n_points, dtype=bool)
    if "density" in enabled_filters and density_threshold > 0.0:
        remove_mask_density[active_mask] = density_res['density_score'][active_mask] < density_threshold
    
    # 8. 候補マスクの整理
    remove_mask_wh = white_haze_candidate_mask
    remove_mask_sor = sor_res['remove_mask']
    remove_mask_ror = ror_res['remove_mask']
    remove_mask_cc = cc_res['remove_mask']

    # 9. preview は「見える化」用、final は実際に Commit で非表示化する対象
    preview_mask = remove_mask_wh | remove_mask_sor | remove_mask_ror | remove_mask_cc | remove_mask_dbscan | remove_mask_density
    final_remove_mask = remove_mask_sor | remove_mask_ror | remove_mask_cc | remove_mask_dbscan | remove_mask_density

    # 10. プレビュー理由 (色分け優先順位: 白モヤを最後に上書きして見えやすくする)
    preview_reason = np.zeros(n_points, dtype=np.int32)
    preview_reason[remove_mask_sor] = 1
    preview_reason[remove_mask_ror] = 2
    preview_reason[remove_mask_density] = 3
    preview_reason[remove_mask_dbscan] = 4
    preview_reason[remove_mask_cc] = 5
    preview_reason[remove_mask_wh] = 7
    preview_reason[~preview_mask] = 0

    # 11. 実削除候補の理由
    reason = np.zeros(n_points, dtype=np.int32)
    reason[remove_mask_density] = 3
    reason[remove_mask_dbscan] = 4
    reason[remove_mask_cc] = 5
    reason[remove_mask_ror] = 2
    reason[remove_mask_sor] = 1
    reason[~final_remove_mask] = 0

    return {
        'base_spacing': base_spacing,
        'remove_mask': final_remove_mask,
        'preview_mask': preview_mask,
        'preview_reason': preview_reason,
        'white_haze_candidate_mask': remove_mask_wh,
        'sor_score': sor_res['sor_score'],
        'density_score': density_res['density_score'],
        'radius_neighbor_count': ror_res['radius_neighbor_count'],
        'cc_noise_score': cc_res['cc_noise_score'],
        'white_haze_score': wh_res['white_haze_score'],
        'cluster_id': cluster_id,
        'reason': reason,
        'dbscan_mode': dbscan_mode,
        'dbscan_voxel_size': dbscan_voxel_size,
        'dbscan_analysis_count': dbscan_analysis_count,
        'dbscan_timeout': dbscan_timeout,
        'removed_by_sor_count': int(np.sum(reason == 1)),
        'removed_by_ror_count': int(np.sum(reason == 2)),
        'removed_by_low_density_count': int(np.sum(reason == 3)),
        'removed_by_small_cluster_count': int(np.sum(reason == 4)),
        'removed_by_cc_noise_count': int(np.sum(reason == 5)),
        'removed_by_white_haze_count': int(np.sum(remove_mask_wh)),
        'white_haze_candidate_count': int(np.sum(remove_mask_wh))
    }
