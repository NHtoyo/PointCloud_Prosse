import unittest
import os
import sys
import numpy as np

# 親ディレクトリをパスに追加してモジュールが読めるようにする
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pointcloud_io
import noise_filters

class TestNoiseFilters(unittest.TestCase):
    
    @classmethod
    def setUpClass(cls):
        cls.test_dir = os.path.dirname(os.path.abspath(__file__))
        cls.floating_ply = os.path.join(cls.test_dir, "test_data_floating.ply")
        cls.thin_ply = os.path.join(cls.test_dir, "test_data_thin_structures.ply")
        cls.clusters_ply = os.path.join(cls.test_dir, "test_data_clusters.ply")
        
        # テスト用点群ファイルが存在しない場合は生成する
        if not (os.path.exists(cls.floating_ply) and os.path.exists(cls.thin_ply) and os.path.exists(cls.clusters_ply)):
            import generate_test_data
            generate_test_data.main()

    def test_1_floating_points(self):
        """
        テスト1: 球面上の密な点群 (1000点) + 空中に浮いた外れ点 (20点)
        → SOR/RORでその20点が90%以上(18点以上)除去対象になることを確認
        """
        print("\n=== テスト1: 浮遊点除去テスト ===")
        points, colors = pointcloud_io.load_ply(self.floating_ply)
        self.assertEqual(len(points), 1020)
        
        # デフォルトパラメータでフィルタ実行
        params = {
            'sor': {'nb_neighbors': 20, 'std_ratio': 1.5},
            'ror': {'radius_multiplier': 3.0, 'min_neighbors': 8},
            'dbscan': {'eps_multiplier': 4.0, 'min_points': 10, 'min_cluster_size': 200, 'timeout_sec': 120, 'target_points': 200000}
        }
        
        # SOR/RORのみ有効にして確認（DBSCANのmin_cluster_sizeを極端に小さくして影響を避けるか、DBSCAN結果を無視）
        base_spacing = noise_filters.estimate_base_spacing(points)
        sor_res = noise_filters.compute_sor(points, nb_neighbors=20, std_ratio=1.5)
        ror_res = noise_filters.compute_ror(points, base_spacing, radius_multiplier=3.0, min_neighbors=8)
        
        # SOR または ROR のマスク
        combined_mask = sor_res['remove_mask'] | ror_res['remove_mask']
        
        # 後半の20点が浮遊点
        floating_removed = np.sum(combined_mask[1000:])
        sphere_removed = np.sum(combined_mask[:1000])
        
        print(f"浮遊点除去数: {floating_removed}/20 (割合: {floating_removed/20.0:.1%})")
        print(f"球面点誤除去数: {sphere_removed}/1000 (割合: {sphere_removed/1000.0:.1%})")
        
        # 基準: 浮遊点の90% (18点) 以上が除去対象
        self.assertTrue(floating_removed >= 18, f"浮遊点の除去数が足りません: {floating_removed}/20")
        # 球面点群は95%以上残ること
        self.assertTrue(sphere_removed <= 50, f"球面点が誤除去されすぎています: {sphere_removed}/1000")

    def test_2_thin_structures(self):
        """
        テスト2: 細い線状点群 (cylinder, 40点) + 主球面点群 (1000点)
        - Soft設定: 線状点群 of 80%以上(32点以上)が残ること
        - Strong設定: 線状点群 of 50%以上(20点以上)が除去対象になること
        """
        print("\n=== テスト2: 細い構造物除去比較テスト ===")
        points, colors = pointcloud_io.load_ply(self.thin_ply)
        self.assertEqual(len(points), 1040)
        
        # 1. Soft 設定
        base_spacing = noise_filters.estimate_base_spacing(points)
        
        # 緩い設定: SORの標準偏差比を大きく、RORの近傍半径を小さく/近傍点数を少なく
        sor_soft = noise_filters.compute_sor(points, nb_neighbors=10, std_ratio=3.0)
        ror_soft = noise_filters.compute_ror(points, base_spacing, radius_multiplier=2.0, min_neighbors=3)
        soft_mask = sor_soft['remove_mask'] | ror_soft['remove_mask']
        
        # 後半の40点が円柱（細い構造）
        cyl_removed_soft = np.sum(soft_mask[1000:])
        cyl_kept_soft = 40 - cyl_removed_soft
        
        print(f"Soft設定 - 円柱残存数: {cyl_kept_soft}/40 (割合: {cyl_kept_soft/40.0:.1%})")
        # 基準: 80% (32点) 以上残存
        self.assertTrue(cyl_kept_soft >= 32, f"Soft設定で円柱が消えすぎています: 残存 {cyl_kept_soft}/40")
        
        # 2. Strong 設定
        # 厳しい設定: SORの標準偏差比を小さく、RORの近傍半径を大きく/近傍点数を多く
        sor_strong = noise_filters.compute_sor(points, nb_neighbors=30, std_ratio=1.0)
        ror_strong = noise_filters.compute_ror(points, base_spacing, radius_multiplier=5.0, min_neighbors=15)
        strong_mask = sor_strong['remove_mask'] | ror_strong['remove_mask']
        
        cyl_removed_strong = np.sum(strong_mask[1000:])
        print(f"Strong設定 - 円柱除去数: {cyl_removed_strong}/40 (割合: {cyl_removed_strong/40.0:.1%})")
        # 基準: 50% (20点) 以上除去対象
        self.assertTrue(cyl_removed_strong >= 20, f"Strong設定で円柱の除去が不十分です: 除去 {cyl_removed_strong}/40")

    def test_3_small_clusters(self):
        """
        テスト3: 大きなクラスタ1つ (3000点) + 小さなクラスタ3つ (各30点) + ノイズ点 (20点)
        → 小クラスタ(計90点)とノイズ(20点)はすべて除去対象になり、大クラスタは残ることを確認
        """
        print("\n=== テスト3: 小クラスタ検出テスト ===")
        points, colors = pointcloud_io.load_ply(self.clusters_ply)
        self.assertEqual(len(points), 3110)
        
        # パラメータ設定: 小クラスタ検出用に min_cluster_size=200 と設定
        params = {
            'sor': {'nb_neighbors': 20, 'std_ratio': 10.0}, # SOR/RORは無効化するため閾値を緩く
            'ror': {'radius_multiplier': 0.1, 'min_neighbors': 0},
            'dbscan': {
                'eps_multiplier': 4.0,
                'min_points': 10,
                'min_cluster_size': 200, # 200点未満を小クラスタとして除去
                'timeout_sec': 120,
                'target_points': 200000
            }
        }
        
        # フィルタ実行
        results = noise_filters.run_all_filters(points, params, mode='full')
        
        remove_mask = results['remove_mask']
        
        # 各インデックス範囲の除去状況
        large_removed = np.sum(remove_mask[:3000])
        small1_removed = np.sum(remove_mask[3000:3030])
        small2_removed = np.sum(remove_mask[3030:3060])
        small3_removed = np.sum(remove_mask[3060:3090])
        noise_removed = np.sum(remove_mask[3090:])
        
        print(f"大クラスタ誤除去数: {large_removed}/3000 (割合: {large_removed/3000.0:.1%})")
        print(f"小クラスタ1除去数: {small1_removed}/30 (割合: {small1_removed/30.0:.1%})")
        print(f"小クラスタ2除去数: {small2_removed}/30 (割合: {small2_removed/30.0:.1%})")
        print(f"小クラスタ3除去数: {small3_removed}/30 (割合: {small3_removed/30.0:.1%})")
        print(f"浮遊ノイズ除去数: {noise_removed}/20 (割合: {noise_removed/20.0:.1%})")
        
        # 基準: 小クラスタ(合計90点)および浮遊ノイズ(20点)が100%除去対象
        self.assertEqual(small1_removed, 30, "小クラスタ1が完全に除去されていません")
        self.assertEqual(small2_removed, 30, "小クラスタ2が完全に除去されていません")
        self.assertEqual(small3_removed, 30, "小クラスタ3が完全に除去されていません")
        self.assertEqual(noise_removed, 20, "浮遊ノイズが完全に除去されていません")
        
        # 基準: 大クラスタは99%以上残る (誤除去は30点以下)
        self.assertTrue(large_removed <= 30, f"大クラスタの誤除去が多すぎます: {large_removed}/3000")

if __name__ == "__main__":
    unittest.main()
