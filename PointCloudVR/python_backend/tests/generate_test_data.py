import numpy as np
import open3d as o3d
import os
import sys

# 親ディレクトリをパスに追加してモジュールが読めるようにする
sys.path.append(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import pointcloud_io

def generate_floating_points():
    """
    テスト1用: 球面上の密な点群 (1000点) + 空中に浮いた外れ点 (20点)
    """
    np.random.seed(42)
    
    # 密な球面点群 1000点 (中心原点, 半径1.0)
    phi = np.random.uniform(0, 2 * np.pi, 1000)
    theta = np.random.uniform(0, np.pi, 1000)
    r = 1.0
    x = r * np.sin(theta) * np.cos(phi)
    y = r * np.sin(theta) * np.sin(phi)
    z = r * np.cos(theta)
    sphere_pts = np.stack([x, y, z], axis=1)
    
    # 浮遊点 20点（離れた場所、1.5m以上離れていることを保証）
    floating_pts = np.random.uniform(-3, 3, (20, 3))
    dists = np.linalg.norm(floating_pts, axis=1)
    while np.any(dists < 1.5):
        mask = dists < 1.5
        floating_pts[mask] = np.random.uniform(-3, 3, (np.sum(mask), 3))
        dists = np.linalg.norm(floating_pts, axis=1)
        
    pts = np.vstack([sphere_pts, floating_pts])
    colors = np.zeros_like(pts, dtype=np.uint8) + 128  # 中間グレー
    return pts, colors

def generate_thin_structures():
    """
    テスト2用: 細い線状点群 (cylinder, radius=0.001m, 500点) + 主球面点群 (1000点)
    """
    np.random.seed(42)
    
    # 主球面点群 1000点 (中心原点, 半径0.5)
    phi = np.random.uniform(0, 2 * np.pi, 1000)
    theta = np.random.uniform(0, np.pi, 1000)
    r = 0.5
    x = r * np.sin(theta) * np.cos(phi)
    y = r * np.sin(theta) * np.sin(phi)
    z = r * np.cos(theta)
    sphere_pts = np.stack([x, y, z], axis=1)
    
    # 細い線状点群 40点 (Z軸方向に伸びる細い円柱, 長さ1.0m, 半径0.001m = 1mm)
    h = np.random.uniform(0.5, 1.5, 40)
    t = np.random.uniform(0, 2 * np.pi, 40)
    r_cyl = 0.001
    cx = r_cyl * np.cos(t)
    cy = r_cyl * np.sin(t)
    cz = h
    cyl_pts = np.stack([cx, cy, cz], axis=1)
    
    pts = np.vstack([sphere_pts, cyl_pts])
    colors = np.zeros_like(pts, dtype=np.uint8) + 128
    return pts, colors

def generate_clusters():
    """
    テスト3用: 大きなクラスタ1つ (3000点) + 小さなクラスタ3つ (各30点) + ノイズ点 (20点)
    """
    np.random.seed(42)
    
    # 大クラスタ 3000点 (中心原点, 半径2.0)
    phi = np.random.uniform(0, 2 * np.pi, 3000)
    theta = np.random.uniform(0, np.pi, 3000)
    r = 2.0
    x = r * np.sin(theta) * np.cos(phi)
    y = r * np.sin(theta) * np.sin(phi)
    z = r * np.cos(theta)
    sphere_pts = np.stack([x, y, z], axis=1)
    
    # 小クラスタ3つ 各30点 (離れた場所の小球)
    centers = [
        np.array([6.0, 0.0, 0.0]),
        np.array([0.0, 6.0, 0.0]),
        np.array([0.0, 0.0, 6.0])
    ]
    small_pts_list = []
    for center in centers:
        phi_s = np.random.uniform(0, 2 * np.pi, 30)
        theta_s = np.random.uniform(0, np.pi, 30)
        r_s = np.random.uniform(0, 0.1, 30)
        sx = center[0] + r_s * np.sin(theta_s) * np.cos(phi_s)
        sy = center[1] + r_s * np.sin(theta_s) * np.sin(phi_s)
        sz = center[2] + r_s * np.cos(theta_s)
        small_pts_list.append(np.stack([sx, sy, sz], axis=1))
        
    # ノイズ点 20点 (広範囲にランダム配置、クラスタから離れていることを保証)
    noise_pts = np.random.uniform(-8, 8, (20, 3))
    # クラスタ中心から離れている点だけ選別する簡易ロジック
    for i in range(len(noise_pts)):
        while True:
            pt = noise_pts[i]
            # 原点(大クラスタ中心)からの距離
            dist_large = np.linalg.norm(pt)
            # 各小クラスタ中心からの距離
            dist_smalls = [np.linalg.norm(pt - c) for c in centers]
            if dist_large > 2.5 and all(d > 0.5 for d in dist_smalls):
                break
            noise_pts[i] = np.random.uniform(-8, 8, 3)
            
    pts = np.vstack([sphere_pts] + small_pts_list + [noise_pts])
    colors = np.zeros_like(pts, dtype=np.uint8) + 128
    return pts, colors

def main():
    test_dir = os.path.dirname(os.path.abspath(__file__))
    os.makedirs(test_dir, exist_ok=True)
    
    print("テスト用点群データを生成中...")
    
    # テスト1
    pts1, col1 = generate_floating_points()
    path1 = os.path.join(test_dir, "test_data_floating.ply")
    pointcloud_io.save_ply(path1, pts1, col1)
    print(f"テスト1データ保存完了: {path1} (点数: {len(pts1)})")
    
    # テスト2
    pts2, col2 = generate_thin_structures()
    path2 = os.path.join(test_dir, "test_data_thin_structures.ply")
    pointcloud_io.save_ply(path2, pts2, col2)
    print(f"テスト2データ保存完了: {path2} (点数: {len(pts2)})")
    
    # テスト3
    pts3, col3 = generate_clusters()
    path3 = os.path.join(test_dir, "test_data_clusters.ply")
    pointcloud_io.save_ply(path3, pts3, col3)
    print(f"テスト3データ保存完了: {path3} (点数: {len(pts3)})")
    
    print("すべてのテストデータ生成が完了しました。")

if __name__ == "__main__":
    main()
