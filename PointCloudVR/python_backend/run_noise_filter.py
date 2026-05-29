import os
import sys

# OpenMP や MKL などのマルチスレッドライブラリのデッドロック防止（スレッド数を 1 に制限）
os.environ["OMP_NUM_THREADS"] = "1"
os.environ["MKL_NUM_THREADS"] = "1"
os.environ["OPENBLAS_NUM_THREADS"] = "1"
os.environ["VECLIB_MAXIMUM_THREADS"] = "1"
os.environ["NUMEXPR_NUM_THREADS"] = "1"

import argparse
import time
import numpy as np
import open3d as o3d

# 自作モジュールのインポート
import pointcloud_io
import noise_filters
import result_writer

def str_to_bool(value):
    if isinstance(value, bool):
        return value
    if value.lower() in ('yes', 'true', 't', 'y', '1'):
        return True
    elif value.lower() in ('no', 'false', 'f', 'n', '0'):
        return False
    else:
        raise argparse.ArgumentTypeError('Boolean value expected.')

def main():
    parser = argparse.ArgumentParser(description="NeRF Point Cloud Noise Filter Backend CLI")
    
    # 基本の入力と出力
    parser.add_argument("--input", required=True, help="入力PLYファイルのパス")
    parser.add_argument("--output_dir", required=True, help="出力ディレクトリ（バイナリとJSONの保存先）")
    parser.add_argument("--mode", choices=["full", "downsample"], default="full", 
                        help="実行モード: 'full' (フル解像度、DBSCANのみ自動ダウンサンプリング) "
                             "または 'downsample' (全フィルタをダウンサンプリングされた点群に適用)")
    parser.add_argument("--voxel_size", type=float, default=None,
                        help="Downsample モード時のボクセルサイズ（m）。指定しない場合は自動計算")
    
    # フィルタパラメータの引数
    parser.add_argument("--sor_nb", type=int, default=20, help="SORの近傍点数 (default: 20)")
    parser.add_argument("--sor_std", type=float, default=1.5, help="SORの標準偏差比の閾値 (default: 1.5)")
    parser.add_argument("--ror_mul", type=float, default=3.0, help="RORの近傍半径マルチプライヤ (default: 3.0)")
    parser.add_argument("--ror_min", type=int, default=8, help="RORの最小近傍点数 (default: 8)")
    parser.add_argument("--density_k", type=int, default=8, help="密度推定のk近傍数 (default: 8)")
    parser.add_argument("--density_thresh", type=float, default=0.0, 
                        help="密度による削除の閾値。0.0以下の場合は低密度削除を無効化 (default: 0.0)")
    parser.add_argument("--cc_k", type=int, default=20, help="CC平面フィルタの近傍点数 (default: 20)")
    parser.add_argument("--cc_sigma", type=float, default=1.0, help="CC平面フィルタの相対シグマ閾値 (default: 1.0)")
    parser.add_argument("--cc_error", type=float, default=0.0, help="CC平面フィルタの絶対誤差閾値 (default: 0.0)")
    parser.add_argument("--cc_use_knn", type=str_to_bool, default=True, help="CC平面フィルタでKNNモードを使用するか (default: True)")
    parser.add_argument("--cc_radius", type=float, default=0.05, help="CC平面フィルタのRadiusモード時の近傍半径 (default: 0.05)")
    parser.add_argument("--cc_remove_isolated", type=str_to_bool, default=False, help="CC平面フィルタのRadiusモードで孤立点を除去するか (default: False)")
    parser.add_argument("--cc_use_relative", type=str_to_bool, default=True, help="CC平面フィルタで相対シグマ閾値を使用するか (default: True)")
    parser.add_argument("--dbscan_eps", type=float, default=4.0, help="DBSCANのepsマルチプライヤ (default: 4.0)")
    parser.add_argument("--dbscan_min", type=int, default=10, help="DBSCANのコア点条件最小点数 (default: 10)")
    parser.add_argument("--dbscan_cluster", type=int, default=200, help="小クラスタと判定する閾値サイズ (default: 200)")
    parser.add_argument("--dbscan_target", type=int, default=200000, help="DBSCAN自動ダウンサンプル時の目標点数 (default: 200000)")
    parser.add_argument("--dbscan_timeout", type=int, default=120, help="DBSCANのタイムアウト秒数 (default: 120)")
    parser.add_argument("--filters", nargs="*", choices=["sor", "ror", "dbscan", "density", "cc_noise", "none"], default=None,
                        help="有効にするフィルタのリスト (noneを指定した場合はすべて無効)")

    args = parser.parse_args()

    # 有効フィルタ集合のパース (デフォルトは SOR, CC_Noise, DBSCAN)
    enabled_filters = set(args.filters or ["sor", "cc_noise", "dbscan"])
    if "none" in enabled_filters:
        enabled_filters = set()
    
    start_time = time.time()
    
    # 1. 入力ファイルの存在確認とロード
    if not os.path.exists(args.input):
        print(f"[Error] 入力ファイルが見つかりません: {args.input}", file=sys.stderr)
        sys.exit(1)
        
    print(f"PLYファイルをロード中: {args.input} ...")
    try:
        points, colors = pointcloud_io.load_ply(args.input)
    except Exception as e:
        print(f"[Error] 点群のロードに失敗しました: {e}", file=sys.stderr)
        sys.exit(1)
        
    original_count = len(points)
    print(f"点群ロード完了. 点数: {original_count:,}")
    
    # 2. パラメータ辞書の構築
    params = {
        "sor": {"nb_neighbors": args.sor_nb, "std_ratio": args.sor_std},
        "ror": {"radius_multiplier": args.ror_mul, "min_neighbors": args.ror_min},
        "density": {"k": args.density_k, "threshold": args.density_thresh},
        "cc_noise": {
            "k": args.cc_k,
            "relative_sigma": args.cc_sigma if args.cc_use_relative else 0.0,
            "absolute_error": args.cc_error if not args.cc_use_relative else 0.0,
            "use_knn": args.cc_use_knn,
            "radius": args.cc_radius,
            "remove_isolated_points": args.cc_remove_isolated
        },
        "dbscan": {
            "eps_multiplier": args.dbscan_eps,
            "min_points": args.dbscan_min,
            "min_cluster_size": args.dbscan_cluster,
            "target_points": args.dbscan_target,
            "timeout_sec": args.dbscan_timeout
        }
    }
    
    # 3. 実行モード別の処理
    if args.mode == "downsample":
        print("Downsample Preview モードで処理を開始します...")
        base_spacing = noise_filters.estimate_base_spacing(points)
        
        # ボクセルサイズの自動推定または設定値の採用
        if args.voxel_size is not None:
            v_size = args.voxel_size
        else:
            target = args.dbscan_target
            if original_count > target:
                ratio = original_count / target
                v_size = float(base_spacing * (ratio ** (1.0 / 3.0)))
            else:
                v_size = float(base_spacing * 2.0)
                
        print(f"点群のダウンサンプリングを実行中 (ボクセルサイズ: {v_size:.5f} m) ...")
        pcd = o3d.geometry.PointCloud()
        pcd.points = o3d.utility.Vector3dVector(points.astype(np.float64))
        pcd_ds = pcd.voxel_down_sample(v_size)
        points_ds = np.asarray(pcd_ds.points).astype(np.float32)
        
        if pcd_ds.has_colors():
            colors_ds = (np.asarray(pcd_ds.colors) * 255.0).clip(0, 255).astype(np.uint8)
        else:
            colors_ds = np.zeros_like(points_ds, dtype=np.uint8)
            
        analysis_count = len(points_ds)
        print(f"ダウンサンプリング完了. 点数: {analysis_count:,} (比率: {analysis_count/original_count:.2%})")
        
        # ダウンサンプルした点群に対して全フィルタを実行
        results = noise_filters.run_all_filters(points_ds, params, enabled_filters, mode='downsample')
        
        # プレビュー表示用として、ダウンサンプルされた点群自身も PLY として出力ディレクトリに保存する
        preview_ply_path = os.path.join(args.output_dir, "preview.ply")
        pointcloud_io.save_ply(preview_ply_path, points_ds, colors_ds)
        print(f"プレビュー用点群ファイルを保存しました: {preview_ply_path}")
        
        # 結果の出力
        result_writer.write_results(
            args.output_dir, results, params, mode='downsample_preview',
            original_count=original_count, analysis_count=analysis_count, voxel_size=v_size
        )
    else:
        print("Full モードで処理を開始します...")
        # 元点群全体に対して実行（DBSCANは点数に応じて自動でDownsampleしてマッピング）
        results = noise_filters.run_all_filters(points, params, enabled_filters, mode='full')
        analysis_count = original_count
        
        if results['dbscan_mode'] == 'downsample':
            print(f"DBSCANのみ自動ダウンサンプリングされました (ダウンサンプル点数: {results['dbscan_analysis_count']:,})")
            
        # 結果の出力
        result_writer.write_results(
            args.output_dir, results, params, mode='full',
            original_count=original_count, analysis_count=analysis_count, voxel_size=None
        )
        
    # 4. 処理結果サマリーの表示
    elapsed = time.time() - start_time
    removed_count = int(np.sum(results['remove_mask']))
    
    print("\n================== 処理結果サマリー ==================")
    print(f" 実行ステータス: 正常終了")
    print(f" 処理時間     : {elapsed:.2f} 秒")
    print(f" 元点数       : {original_count:,} 点")
    print(f" 解析点数     : {analysis_count:,} 点")
    print(f" 削除候補点数 : {removed_count:,} 点 (割合: {removed_count/len(results['remove_mask']):.2%})")
    print(f" --------------------------------------------------")
    print(f" フィルタ別削除内訳:")
    print(f"  - SOR         : {results['removed_by_sor_count']:,} 点")
    print(f"  - ROR         : {results['removed_by_ror_count']:,} 点")
    print(f"  - 低密度      : {results['removed_by_low_density_count']:,} 点")
    print(f"  - 平面推定(CC): {results['removed_by_cc_noise_count']:,} 点")
    print(f"  - 小クラスタ  : {results['removed_by_small_cluster_count']:,} 点")
    
    if results.get('dbscan_timeout', False):
        print(f" [Warning] DBSCAN処理がタイムアウト({args.dbscan_timeout}秒)したため、小クラスタ検出はスキップされました。")
        
    print(f" --------------------------------------------------")
    print(f" 結果出力ディレクトリ: {args.output_dir}")
    print("======================================================")

if __name__ == "__main__":
    main()
