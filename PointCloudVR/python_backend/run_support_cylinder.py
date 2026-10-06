import argparse
import os
import sys

import numpy as np

import pointcloud_io
from coordinate_units import convert_points_to_mm_in_place, validate_coordinate_scale_to_mm
from support_cylinder import SupportCylinderParams, extract_support_mask, save_support_result


def setup_argparser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Seeded support pole extraction backend")
    parser.add_argument("--input", required=True, help="入力PLYファイル")
    parser.add_argument("--seed_indices", required=True, help="int32 little-endian の選択点インデックスbin")
    parser.add_argument("--output_dir", required=True, help="結果出力ディレクトリ")
    parser.add_argument("--coordinate-scale-to-mm", type=float, required=True,
                        help="PLY data-space座標をmmへ変換する倍率")
    parser.add_argument("--tube_multiplier", type=float, default=4.0, help="点間隔に対する探索太さ倍率")
    parser.add_argument("--color_tolerance", type=float, default=90.0, help="RGB距離の色許容")
    parser.add_argument("--saturation_slack", type=float, default=0.25, help="低彩度支柱用の彩度許容")
    parser.add_argument("--min_seed_points", type=int, default=12, help="最低種点数")
    parser.add_argument("--max_empty_bins", type=int, default=3, help="高さ方向に許す空白bin数")
    parser.add_argument("--height_bin_multiplier", type=float, default=4.0, help="点間隔に対する高さbin倍率")
    return parser


def main(argv=None) -> int:
    args = setup_argparser().parse_args(argv)

    try:
        print(f"PLYファイルをロード中: {args.input} ...", flush=True)
        points, colors = pointcloud_io.load_ply(args.input)
        raw_span = np.ptp(points, axis=0)
        coordinate_scale_to_mm = validate_coordinate_scale_to_mm(args.coordinate_scale_to_mm)
        points_mm = convert_points_to_mm_in_place(points, coordinate_scale_to_mm)
        print(f"点群ロード完了. 点数: {len(points):,}", flush=True)
        print(f"[SupportCylinderInput] raw_xyz_span={tuple(float(v) for v in raw_span)}", flush=True)
        print(f"[SupportCylinderInput] coordinate_scale_to_mm={coordinate_scale_to_mm:.9g}", flush=True)
        print(f"[SupportCylinderInput] mm_xyz_span={tuple(float(v) for v in np.ptp(points_mm, axis=0))}", flush=True)

        if not os.path.exists(args.seed_indices):
            raise FileNotFoundError(f"seed_indices が見つかりません: {args.seed_indices}")
        seed_indices = np.fromfile(args.seed_indices, dtype="<i4")
        print(f"支柱 seed 読み込み完了. seed点数: {len(seed_indices):,}", flush=True)

        params = SupportCylinderParams(
            tube_multiplier=args.tube_multiplier,
            color_tolerance=args.color_tolerance,
            saturation_slack=args.saturation_slack,
            min_seed_points=args.min_seed_points,
            max_empty_bins=args.max_empty_bins,
            height_bin_multiplier=args.height_bin_multiplier,
        )
        mask, report = extract_support_mask(points_mm, colors, seed_indices, params)
        save_support_result(args.output_dir, mask, report)

        print("================== 支柱抽出結果 ==================", flush=True)
        print(f"実行ステータス : 正常終了", flush=True)
        print(f"元点数         : {report['point_count']:,}", flush=True)
        print(f"seed点数       : {report['seed_count']:,}", flush=True)
        print(f"候補点数       : {report['candidate_count']:,}", flush=True)
        print(f"選択点数       : {report['selected_count']:,}", flush=True)
        print(f"点間隔         : {report['spacing']:.2f} mm", flush=True)
        print(f"探索太さ       : {report['tube_radius']:.2f} mm", flush=True)
        print(f"結果出力       : {args.output_dir}", flush=True)
        print("==================================================", flush=True)
        return 0
    except Exception as ex:
        print(f"[SupportCylinderError] {ex}", file=sys.stderr, flush=True)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
