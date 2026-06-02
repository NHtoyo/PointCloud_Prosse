import os
import json
import numpy as np
from datetime import datetime

def _write_array(path: str, array: np.ndarray, dtype: str):
    """
    指定dtypeでバイナリ出力します。
    既に同じdtypeなら余分なコピーを避けます。
    """
    arr = np.asarray(array)
    target_dtype = np.dtype(dtype)
    if arr.dtype != target_dtype:
        arr = arr.astype(target_dtype, copy=False)
    arr.tofile(path)

def _has_data(array: np.ndarray, default_value=0) -> bool:
    arr = np.asarray(array)
    if arr.size == 0:
        return False
    return bool(np.any(arr != default_value))

def _register_file(metadata_files: dict, key: str, filename: str, dtype: str, point_count: int):
    metadata_files[key] = {"filename": filename, "dtype": dtype, "shape": [point_count]}

KNOWN_OUTPUT_FILES = (
    "remove_mask.bin",
    "preview_mask.bin",
    "white_haze_candidate_mask.bin",
    "sor_score.bin",
    "density_score.bin",
    "radius_neighbor_count.bin",
    "cc_noise_score.bin",
    "white_haze_score.bin",
    "cluster_id.bin",
    "preview_reason.bin",
    "reason.bin",
    "metadata.json",
    "removal_report.json",
)

def _clear_previous_outputs(output_dir: str):
    for filename in KNOWN_OUTPUT_FILES:
        path = os.path.join(output_dir, filename)
        if os.path.exists(path):
            os.remove(path)

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
    _clear_previous_outputs(output_dir)
    
    point_count = len(results['remove_mask'])
    metadata_files = {}

    # 1. 各種バイナリデータの書き出し (C#との互換性のために明示的にリトルエンディアンにする)
    _write_array(os.path.join(output_dir, "remove_mask.bin"), results['remove_mask'], '|u1')
    _register_file(metadata_files, "remove_mask", "remove_mask.bin", "uint8", point_count)
    _write_array(os.path.join(output_dir, "preview_mask.bin"), results['preview_mask'], '|u1')
    _register_file(metadata_files, "preview_mask", "preview_mask.bin", "uint8", point_count)
    _write_array(os.path.join(output_dir, "white_haze_candidate_mask.bin"), results['white_haze_candidate_mask'], '|u1')
    _register_file(metadata_files, "white_haze_candidate_mask", "white_haze_candidate_mask.bin", "uint8", point_count)

    if _has_data(results['sor_score']):
        _write_array(os.path.join(output_dir, "sor_score.bin"), results['sor_score'], '<f4')
        _register_file(metadata_files, "sor_score", "sor_score.bin", "float32", point_count)
    if _has_data(results['density_score']):
        _write_array(os.path.join(output_dir, "density_score.bin"), results['density_score'], '<f4')
        _register_file(metadata_files, "density_score", "density_score.bin", "float32", point_count)
    if _has_data(results['radius_neighbor_count']):
        _write_array(os.path.join(output_dir, "radius_neighbor_count.bin"), results['radius_neighbor_count'], '<i4')
        _register_file(metadata_files, "radius_neighbor_count", "radius_neighbor_count.bin", "int32", point_count)
    if _has_data(results['cc_noise_score']):
        _write_array(os.path.join(output_dir, "cc_noise_score.bin"), results['cc_noise_score'], '<f4')
        _register_file(metadata_files, "cc_noise_score", "cc_noise_score.bin", "float32", point_count)
    if _has_data(results['white_haze_score']):
        _write_array(os.path.join(output_dir, "white_haze_score.bin"), results['white_haze_score'], '<f4')
        _register_file(metadata_files, "white_haze_score", "white_haze_score.bin", "float32", point_count)
    if _has_data(results['cluster_id'], default_value=-1):
        _write_array(os.path.join(output_dir, "cluster_id.bin"), results['cluster_id'], '<i4')
        _register_file(metadata_files, "cluster_id", "cluster_id.bin", "int32", point_count)
    _write_array(os.path.join(output_dir, "preview_reason.bin"), results['preview_reason'], '<i4')
    _register_file(metadata_files, "preview_reason", "preview_reason.bin", "int32", point_count)
    if _has_data(results['reason']):
        _write_array(os.path.join(output_dir, "reason.bin"), results['reason'], '<i4')
        _register_file(metadata_files, "reason", "reason.bin", "int32", point_count)
    
    # 2. metadata.json の書き出し
    metadata = {
        "point_count": point_count,
        "mode": mode,
        "dbscan_mode": results['dbscan_mode'],
        "dbscan_voxel_size": results['dbscan_voxel_size'],
        "dbscan_analysis_count": results['dbscan_analysis_count'],
        "voxel_size": voxel_size,
        "files": metadata_files,
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
        "removed_by_white_haze": results['removed_by_white_haze_count'],
        "white_haze_candidate_count": results['white_haze_candidate_count'],
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
