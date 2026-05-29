import os
import json
import numpy as np
from datetime import datetime

def write_results(output_dir: str, results: dict, params: dict, mode: str, 
                  original_count: int, analysis_count: int, voxel_size: float = None):
    """
    点群処理結果をバイナリファイルおよびJSONレポート形式で保存します。
    
    Args:
        output_dir (str): 保存先ディレクトリパス
        results (dict): run_all_filters の返り値
        params (dict): 使用したパラメータ
        mode (str): 'full' または 'downsample'
        original_count (int): 元の点群の点数
        analysis_count (int): 実際に処理した点群の点数
        voxel_size (float, optional): Downsample Preview mode 時のボクセルサイズ
    """
    os.makedirs(output_dir, exist_ok=True)
    
    # 1. 各種バイナリデータの書き出し (C#との互換性のために明示的にリトルエンディアンにする)
    remove_mask_bin = results['remove_mask'].astype('|u1')  # uint8 (1 byte, エンディアン不問)
    sor_score_bin = results['sor_score'].astype('<f4')      # float32 little endian (4 bytes)
    density_score_bin = results['density_score'].astype('<f4') # float32 little endian (4 bytes)
    radius_neighbor_count_bin = results['radius_neighbor_count'].astype('<i4') # int32 little endian (4 bytes)
    cc_noise_score_bin = results['cc_noise_score'].astype('<f4') # float32 little endian (4 bytes)
    cluster_id_bin = results['cluster_id'].astype('<i4')    # int32 little endian (4 bytes)
    reason_bin = results['reason'].astype('<i4')            # int32 little endian (4 bytes)
    
    # ファイル書き出し
    remove_mask_bin.tofile(os.path.join(output_dir, "remove_mask.bin"))
    sor_score_bin.tofile(os.path.join(output_dir, "sor_score.bin"))
    density_score_bin.tofile(os.path.join(output_dir, "density_score.bin"))
    radius_neighbor_count_bin.tofile(os.path.join(output_dir, "radius_neighbor_count.bin"))
    cc_noise_score_bin.tofile(os.path.join(output_dir, "cc_noise_score.bin"))
    cluster_id_bin.tofile(os.path.join(output_dir, "cluster_id.bin"))
    reason_bin.tofile(os.path.join(output_dir, "reason.bin"))
    
    # 2. metadata.json の書き出し
    point_count = len(results['remove_mask'])
    metadata = {
        "point_count": point_count,
        "mode": mode,
        "dbscan_mode": results['dbscan_mode'],
        "dbscan_voxel_size": results['dbscan_voxel_size'],
        "dbscan_analysis_count": results['dbscan_analysis_count'],
        "voxel_size": voxel_size,
        "files": {
            "remove_mask":           {"filename": "remove_mask.bin",           "dtype": "uint8",   "shape": [point_count]},
            "sor_score":             {"filename": "sor_score.bin",             "dtype": "float32", "shape": [point_count]},
            "density_score":         {"filename": "density_score.bin",         "dtype": "float32", "shape": [point_count]},
            "radius_neighbor_count": {"filename": "radius_neighbor_count.bin", "dtype": "int32",   "shape": [point_count]},
            "cc_noise_score":        {"filename": "cc_noise_score.bin",        "dtype": "float32", "shape": [point_count]},
            "cluster_id":            {"filename": "cluster_id.bin",            "dtype": "int32",   "shape": [point_count]},
            "reason":                {"filename": "reason.bin",                "dtype": "int32",   "shape": [point_count]}
        },
        "parameters": params
    }
    
    with open(os.path.join(output_dir, "metadata.json"), "w", encoding="utf-8") as f:
        json.dump(metadata, f, indent=2, ensure_ascii=False)
        
    # 3. removal_report.json の書き出し
    kept_count = int(np.sum(~results['remove_mask']))
    removed_count = int(np.sum(results['remove_mask']))
    
    report = {
        "timestamp": datetime.now().isoformat(),
        "mode": mode,
        "original_point_count": original_count,
        "analysis_point_count": analysis_count,
        "voxel_size": voxel_size,
        "downsample_ratio": float(analysis_count / original_count) if original_count > 0 else 0.0,
        "kept_point_count": kept_count,
        "removed_candidate_count": removed_count,
        "removed_by_sor": results['removed_by_sor_count'],
        "removed_by_ror": results['removed_by_ror_count'],
        "removed_by_low_density": results['removed_by_low_density_count'],
        "removed_by_cc_noise": results['removed_by_cc_noise_count'],
        "removed_by_small_cluster": results['removed_by_small_cluster_count'],
        "dbscan_timeout": results['dbscan_timeout'],
        "parameters_used": params
    }
    
    with open(os.path.join(output_dir, "removal_report.json"), "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2, ensure_ascii=False)

def read_bin(path: str, dtype: str) -> np.ndarray:
    """
    保存されたバイナリファイルから numpy 配列を復元します。(テスト用)
    
    Args:
        path (str): バイナリファイルのパス
        dtype (str): 'uint8', 'float32', 'int32'
        
    Returns:
        np.ndarray: 復元された配列
    """
    if dtype == 'uint8':
        np_dtype = '|u1'
    elif dtype == 'float32':
        np_dtype = '<f4'
    elif dtype == 'int32':
        np_dtype = '<i4'
    else:
        raise ValueError(f"不明な dtype: {dtype}")
        
    return np.fromfile(path, dtype=np_dtype)
