import numpy as np
import open3d as o3d
from scipy.spatial import cKDTree
import time
import math

class FilterStep:
    """1つのフィルタステップを定義するクラス。"""
    def __init__(self, name: str, func, params: dict, enabled: bool, exclude_from_next: bool):
        self.name = name                 # 'white_haze', 'cc_noise', 'sor', 'ror', 'density', 'dbscan' など
        self.func = func                 # 実行する関数 (例: compute_sor)
        self.params = params             # フィルタパラメータの辞書
        self.enabled = enabled           # 有効フラグ
        self.exclude_from_next = exclude_from_next  # 候補を以降のステップの計算から除外するかどうか

class FilterPipeline:
    """
    フィルタステップのリストを管理し、順番に実行するパイプライン。
    ステップの追加順がそのまま実行順序となります。
    """
    def __init__(self):
        self.steps = []

    def add_step(self, step: FilterStep) -> 'FilterPipeline':
        self.steps.append(step)
        return self

    def run(self, points: np.ndarray, colors: np.ndarray = None) -> dict:
        """
        パイプラインのステップを順に実行し、各フィルタの結果をマージして返します。
        各フィルタは active_points (前段で除外されなかった点群) に対して実行され、
        結果は元の点群サイズ N のマスク/スコアに展開されて記録されます。
        """
        n_points = len(points)
        
        # 全ステップ実行後の出力用配列を準備
        active_mask = np.ones(n_points, dtype=bool) # 次のフィルタに入力する点
        
        # フィルタごとの結果を保持
        step_results = {}
        
        # 共通の点間隔 base_spacing
        base_spacing = 0.01  # デフォルト値
        
        # 必要なフィルタが有効な場合、全体の active_points から base_spacing を推定
        # デフォルトのパイプラインでは最初の active_points (全体) で一度だけ推定する設計と互換
        spacing_needed = any(step.enabled and step.name in ["sor", "ror", "dbscan"] for step in self.steps)
        if spacing_needed and n_points > 0:
            from noise_filters import estimate_base_spacing
            base_spacing = estimate_base_spacing(points)
            print(f"[Pipeline] 基準点間隔の初期推定完了: {base_spacing:.6f} m")

        for step in self.steps:
            if not step.enabled:
                continue

            active_indices = np.where(active_mask)[0]
            active_points = points[active_indices]
            active_count = len(active_points)

            print(f"[Pipeline] Step '{step.name}' 実行中... (入力点数: {active_count:,})")
            t0 = time.time()

            # フィルタごとの個別処理
            if step.name == "white_haze":
                # White Haze は色情報を必要とする
                wh_res = step.func(
                    active_points,
                    colors[active_indices] if colors is not None else None,
                    brightness_min=step.params.get('brightness_min', 190.0),
                    saturation_max=step.params.get('saturation_max', 0.20)
                )
                
                # 元サイズに展開して記録
                candidate_mask_full = np.zeros(n_points, dtype=bool)
                candidate_mask_full[active_indices] = wh_res['candidate_mask']
                
                white_haze_score_full = np.zeros(n_points, dtype=np.float32)
                white_haze_score_full[active_indices] = wh_res['white_haze_score']
                
                step_results[step.name] = {
                    'candidate_mask': candidate_mask_full,
                    'white_haze_score': white_haze_score_full
                }
                
                # 前段除外の適用
                if step.exclude_from_next:
                    # candidate_mask が True の点を active_mask から除外
                    active_mask = active_mask & ~candidate_mask_full

            elif step.name in ["sor", "ror", "density", "cc_noise"]:
                # 近傍検索のためにローカルな cKDTree を構築
                tree = None
                if active_count > 0:
                    tree = cKDTree(active_points)

                if step.name == "sor":
                    res = step.func(
                        active_points,
                        tree,
                        nb_neighbors=step.params.get('nb_neighbors', 20),
                        std_ratio=step.params.get('std_ratio', 1.5)
                    )
                    
                    remove_mask_full = np.zeros(n_points, dtype=bool)
                    remove_mask_full[active_indices] = res['remove_mask']
                    
                    sor_score_full = np.zeros(n_points, dtype=np.float32)
                    sor_score_full[active_indices] = res['sor_score']
                    
                    step_results[step.name] = {
                        'remove_mask': remove_mask_full,
                        'sor_score': sor_score_full
                    }
                    if step.exclude_from_next:
                        active_mask = active_mask & ~remove_mask_full

                elif step.name == "ror":
                    res = step.func(
                        active_points,
                        base_spacing,
                        tree,
                        radius_multiplier=step.params.get('radius_multiplier', 3.0),
                        min_neighbors=step.params.get('min_neighbors', 8)
                    )
                    
                    remove_mask_full = np.zeros(n_points, dtype=bool)
                    remove_mask_full[active_indices] = res['remove_mask']
                    
                    neighbor_count_full = np.zeros(n_points, dtype=np.int32)
                    neighbor_count_full[active_indices] = res['radius_neighbor_count']
                    
                    step_results[step.name] = {
                        'remove_mask': remove_mask_full,
                        'radius_neighbor_count': neighbor_count_full
                    }
                    if step.exclude_from_next:
                        active_mask = active_mask & ~remove_mask_full

                elif step.name == "density":
                    res = step.func(
                        active_points,
                        tree,
                        k=step.params.get('k', 8)
                    )
                    
                    density_score_full = np.zeros(n_points, dtype=np.float32)
                    density_score_full[active_indices] = res['density_score']
                    
                    # 密度による削除閾値の判定
                    threshold = step.params.get('threshold', 0.0)
                    remove_mask_full = np.zeros(n_points, dtype=bool)
                    if threshold > 0.0:
                        # 実行された active_indices の中で閾値未満のものを判定
                        remove_mask_full[active_indices] = res['density_score'] < threshold

                    step_results[step.name] = {
                        'remove_mask': remove_mask_full,
                        'density_score': density_score_full
                    }
                    if step.exclude_from_next:
                        active_mask = active_mask & ~remove_mask_full

                elif step.name == "cc_noise":
                    res = step.func(
                        active_points,
                        tree,
                        k=step.params.get('k', 20),
                        relative_sigma=step.params.get('relative_sigma', 1.0),
                        absolute_error=step.params.get('absolute_error', 0.0),
                        use_knn=step.params.get('use_knn', True),
                        radius=step.params.get('radius', None),
                        remove_isolated_points=step.params.get('remove_isolated_points', False),
                        chunk_size=step.params.get('chunk_size', 25000)
                    )
                    
                    remove_mask_full = np.zeros(n_points, dtype=bool)
                    remove_mask_full[active_indices] = res['remove_mask']
                    
                    cc_score_full = np.zeros(n_points, dtype=np.float32)
                    cc_score_full[active_indices] = res['cc_noise_score']
                    
                    step_results[step.name] = {
                        'remove_mask': remove_mask_full,
                        'cc_noise_score': cc_score_full
                    }
                    if step.exclude_from_next:
                        active_mask = active_mask & ~remove_mask_full

            elif step.name == "dbscan":
                # DBSCAN は自動ダウンサンプリング判定や KDTree 伝播など独自の内部制御が必要
                dbscan_mode = 'full'
                dbscan_voxel_size = None
                dbscan_analysis_count = active_count
                dbscan_timeout = False
                
                cluster_id_full = np.full(n_points, -1, dtype=np.int32)
                remove_mask_full = np.zeros(n_points, dtype=bool)

                if active_count > 0:
                    from noise_filters import decide_dbscan_mode, compute_dbscan
                    
                    target = step.params.get('target_points', 200000)
                    # 全体のモード判定
                    dbscan_mode = decide_dbscan_mode(active_count)
                    
                    if dbscan_mode == 'downsample':
                        ratio = active_count / target
                        dbscan_voxel_size = float(base_spacing * (ratio ** (1.0 / 3.0)))
                        
                        pcd = o3d.geometry.PointCloud()
                        pcd.points = o3d.utility.Vector3dVector(active_points.astype(np.float64))
                        pcd_ds = pcd.voxel_down_sample(dbscan_voxel_size)
                        points_ds = np.asarray(pcd_ds.points).astype(np.float32)
                        dbscan_analysis_count = len(points_ds)
                        
                        dbscan_res = compute_dbscan(
                            points_ds,
                            base_spacing,
                            eps_multiplier=step.params.get('eps_multiplier', 4.0),
                            min_points=step.params.get('min_points', 10),
                            min_cluster_size=step.params.get('min_cluster_size', 200)
                        )
                        dbscan_timeout = dbscan_res['timeout']
                        
                        if len(points_ds) > 0:
                            tree_ds = cKDTree(points_ds)
                            _, nn_indices = tree_ds.query(active_points, k=1)
                            cluster_id_full[active_indices] = dbscan_res['cluster_id'][nn_indices]
                            remove_mask_full[active_indices] = dbscan_res['remove_mask'][nn_indices]
                    else:
                        dbscan_res = compute_dbscan(
                            active_points,
                            base_spacing,
                            eps_multiplier=step.params.get('eps_multiplier', 4.0),
                            min_points=step.params.get('min_points', 10),
                            min_cluster_size=step.params.get('min_cluster_size', 200)
                        )
                        cluster_id_full[active_indices] = dbscan_res['cluster_id']
                        remove_mask_full[active_indices] = dbscan_res['remove_mask']
                        dbscan_timeout = dbscan_res['timeout']

                step_results[step.name] = {
                    'remove_mask': remove_mask_full,
                    'cluster_id': cluster_id_full,
                    'dbscan_mode': dbscan_mode,
                    'dbscan_voxel_size': dbscan_voxel_size,
                    'dbscan_analysis_count': dbscan_analysis_count,
                    'dbscan_timeout': dbscan_timeout
                }
                if step.exclude_from_next:
                    active_mask = active_mask & ~remove_mask_full

            print(f"[Pipeline] Step '{step.name}' 完了. (所要時間: {time.time() - t0:.2f}秒)")

        # 後処理・マージ処理
        # 各種配列が結果辞書に含まれない場合はゼロで初期化
        remove_mask_wh = step_results.get('white_haze', {}).get('candidate_mask', np.zeros(n_points, dtype=bool))
        remove_mask_sor = step_results.get('sor', {}).get('remove_mask', np.zeros(n_points, dtype=bool))
        remove_mask_ror = step_results.get('ror', {}).get('remove_mask', np.zeros(n_points, dtype=bool))
        remove_mask_density = step_results.get('density', {}).get('remove_mask', np.zeros(n_points, dtype=bool))
        remove_mask_cc = step_results.get('cc_noise', {}).get('remove_mask', np.zeros(n_points, dtype=bool))
        remove_mask_dbscan = step_results.get('dbscan', {}).get('remove_mask', np.zeros(n_points, dtype=bool))

        preview_mask = remove_mask_wh | remove_mask_sor | remove_mask_ror | remove_mask_cc | remove_mask_dbscan | remove_mask_density
        final_remove_mask = remove_mask_sor | remove_mask_ror | remove_mask_cc | remove_mask_dbscan | remove_mask_density

        # プレビュー理由の割り当て (優先度順: 白モヤ 7 が最前面に来るよう、後から上書き)
        preview_reason = np.zeros(n_points, dtype=np.int32)
        preview_reason[remove_mask_sor] = 1
        preview_reason[remove_mask_ror] = 2
        preview_reason[remove_mask_density] = 3
        preview_reason[remove_mask_dbscan] = 4
        preview_reason[remove_mask_cc] = 5
        preview_reason[remove_mask_wh] = 7
        preview_reason[~preview_mask] = 0

        # 最終候補の理由
        reason = np.zeros(n_points, dtype=np.int32)
        reason[remove_mask_density] = 3
        reason[remove_mask_dbscan] = 4
        reason[remove_mask_cc] = 5
        reason[remove_mask_ror] = 2
        reason[reason == 0 & remove_mask_sor] = 1 # SORの競合回避（上書きされないように）
        # 元の動作と同じに設定：
        reason[remove_mask_sor] = 1
        reason[remove_mask_ror] = 2
        reason[remove_mask_density] = 3
        reason[remove_mask_dbscan] = 4
        reason[remove_mask_cc] = 5
        reason[~final_remove_mask] = 0

        # DBSCANのメタ情報
        db_info = step_results.get('dbscan', {
            'cluster_id': np.full(n_points, -1, dtype=np.int32),
            'dbscan_mode': 'full',
            'dbscan_voxel_size': None,
            'dbscan_analysis_count': 0,
            'dbscan_timeout': False
        })

        return {
            'base_spacing': base_spacing,
            'remove_mask': final_remove_mask,
            'preview_mask': preview_mask,
            'preview_reason': preview_reason,
            'white_haze_candidate_mask': remove_mask_wh,
            'sor_score': step_results.get('sor', {}).get('sor_score', np.zeros(n_points, dtype=np.float32)),
            'density_score': step_results.get('density', {}).get('density_score', np.zeros(n_points, dtype=np.float32)),
            'radius_neighbor_count': step_results.get('ror', {}).get('radius_neighbor_count', np.zeros(n_points, dtype=np.int32)),
            'cc_noise_score': step_results.get('cc_noise', {}).get('cc_noise_score', np.zeros(n_points, dtype=np.float32)),
            'white_haze_score': step_results.get('white_haze', {}).get('white_haze_score', np.zeros(n_points, dtype=np.float32)),
            'cluster_id': db_info['cluster_id'],
            'reason': reason,
            'dbscan_mode': db_info['dbscan_mode'],
            'dbscan_voxel_size': db_info['dbscan_voxel_size'],
            'dbscan_analysis_count': db_info['dbscan_analysis_count'],
            'dbscan_timeout': db_info['dbscan_timeout'],
            'removed_by_sor_count': int(np.sum(reason == 1)),
            'removed_by_ror_count': int(np.sum(reason == 2)),
            'removed_by_low_density_count': int(np.sum(reason == 3)),
            'removed_by_small_cluster_count': int(np.sum(reason == 4)),
            'removed_by_cc_noise_count': int(np.sum(reason == 5)),
            'removed_by_white_haze_count': int(np.sum(remove_mask_wh)),
            'white_haze_candidate_count': int(np.sum(remove_mask_wh))
        }

def build_default_pipeline(params: dict, enabled_filters: set, colors: np.ndarray = None) -> FilterPipeline:
    """
    現在の動作順序（white_haze -> cc_noise -> sor -> ror -> density -> dbscan）に準拠した
    デフォルトの FilterPipeline を構築します。
    """
    from noise_filters import (
        compute_white_haze,
        compute_cc_noise,
        compute_sor,
        compute_ror,
        compute_density
    )

    pipeline = FilterPipeline()
    
    # 1. White Haze (前段除外ON)
    wh_p = params.get('white_haze', {'brightness_min': 190.0, 'saturation_max': 0.20})
    pipeline.add_step(FilterStep(
        name="white_haze",
        func=compute_white_haze,
        params=wh_p,
        enabled=("white_haze" in enabled_filters),
        exclude_from_next=True
    ))

    # 2. CC風局所平面ノイズフィルタ (前段除外OFF)
    cc_p = params.get('cc_noise', {'k': 20, 'relative_sigma': 1.0, 'absolute_error': 0.0})
    pipeline.add_step(FilterStep(
        name="cc_noise",
        func=compute_cc_noise,
        params=cc_p,
        enabled=("cc_noise" in enabled_filters),
        exclude_from_next=False
    ))

    # 3. SOR (前段除外OFF)
    sor_p = params.get('sor', {'nb_neighbors': 20, 'std_ratio': 1.5})
    pipeline.add_step(FilterStep(
        name="sor",
        func=compute_sor,
        params=sor_p,
        enabled=("sor" in enabled_filters),
        exclude_from_next=False
    ))

    # 4. ROR (前段除外OFF)
    ror_p = params.get('ror', {'radius_multiplier': 3.0, 'min_neighbors': 8})
    pipeline.add_step(FilterStep(
        name="ror",
        func=compute_ror,
        params=ror_p,
        enabled=("ror" in enabled_filters),
        exclude_from_next=False
    ))

    # 5. Density (前段除外OFF)
    density_p = params.get('density', {'k': 8, 'threshold': 0.0})
    pipeline.add_step(FilterStep(
        name="density",
        func=compute_density,
        params=density_p,
        enabled=("density" in enabled_filters),
        exclude_from_next=False
    ))

    # 6. DBSCAN (前段除外OFF)
    db_p = params.get('dbscan', {
        'eps_multiplier': 4.0,
        'min_points': 10,
        'min_cluster_size': 200,
        'target_points': 200000
    })
    pipeline.add_step(FilterStep(
        name="dbscan",
        func=None,  # DBSCANは内部で制御するためダミー
        params=db_p,
        enabled=("dbscan" in enabled_filters),
        exclude_from_next=False
    ))

    return pipeline
