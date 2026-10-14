import hashlib
import hmac
import open3d as o3d
import numpy as np

def load_ply(path: str) -> tuple[np.ndarray, np.ndarray]:
    """
    PLYファイルをロードします。
    
    Args:
        path (str): PLYファイルのパス
        
    Returns:
        tuple[np.ndarray, np.ndarray]: (points: float32[N,3], colors: uint8[N,3])
    """
    pcd = o3d.io.read_point_cloud(path)
    if not pcd.has_points():
        raise ValueError(f"指定されたファイルに点が含まれていないか、読み込めませんでした: {path}")
        
    points = np.asarray(pcd.points).astype(np.float32)
    
    if pcd.has_colors():
        colors = (np.asarray(pcd.colors) * 255.0).clip(0, 255).astype(np.uint8)
    else:
        colors = np.zeros_like(points, dtype=np.uint8)
        
    return points, colors


def xyz_sha256(points: np.ndarray) -> str:
    """Hash the ordered XYZ coordinates in the canonical little-endian float32 form."""
    xyz = np.asarray(points, dtype="<f4", order="C")
    if xyz.ndim != 2 or xyz.shape[1] != 3:
        raise ValueError("XYZ fingerprint requires an Nx3 point array")
    return hashlib.sha256(memoryview(xyz).cast("B")).hexdigest()


def validate_xyz_sha256(points: np.ndarray, expected: str) -> str:
    actual = xyz_sha256(points)
    if not isinstance(expected, str) or not hmac.compare_digest(actual.lower(), expected.lower()):
        raise ValueError("PLY point order or XYZ values do not match the Unity snapshot")
    return actual

def save_ply(path: str, points: np.ndarray, colors: np.ndarray, mask: np.ndarray = None):
    """
    PLYファイルを保存します。maskが指定された場合は mask == True の点のみ保存します。
    
    Args:
        path (str): 出力ファイルのパス
        points (np.ndarray): 点の3D座標 [N, 3]
        colors (np.ndarray): 点の色 [N, 3] (0-255)
        mask (np.ndarray, optional): 抽出用マスク [N] (bool)
    """
    if mask is not None:
        points = points[mask]
        colors = colors[mask]
        
    pcd = o3d.geometry.PointCloud()
    pcd.points = o3d.utility.Vector3dVector(points.astype(np.float64))
    pcd.colors = o3d.utility.Vector3dVector(colors.astype(np.float64) / 255.0)
    
    o3d.io.write_point_cloud(path, pcd)

def load_npz(path: str) -> tuple[np.ndarray, np.ndarray]:
    """
    NPZファイルをロードします。(Python内部用)
    
    Args:
        path (str): NPZファイルのパス
        
    Returns:
        tuple[np.ndarray, np.ndarray]: (points: float32[N,3], colors: uint8[N,3])
    """
    with np.load(path) as data:
        points = data['points'].astype(np.float32)
        colors = data['colors'].astype(np.uint8)
    return points, colors

def save_npz(path: str, points: np.ndarray, colors: np.ndarray):
    """
    NPZファイルとして保存します。(Python内部用)
    
    Args:
        path (str): 出力ファイルのパス
        points (np.ndarray): 点の3D座標
        colors (np.ndarray): 点の色 (0-255)
    """
    np.savez_compressed(path, points=points.astype(np.float32), colors=colors.astype(np.uint8))
