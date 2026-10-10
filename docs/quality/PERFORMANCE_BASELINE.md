# 性能ベースライン

測定日: 2026-10-09。以下は環境記録と小さな合成データ測定であり、Unity Profilerによる製品性能保証ではありません。

## 実行環境

- Windows 11 Pro 10.0.26200
- CPU: Intel Core i9-9900K
- GPU: NVIDIA GeForce RTX 2080 Ti、11,264 MiB、driver 591.86
- RAM: OS報告可視容量 67,027,868 KB、測定時空き 29,536,192 KB
- Unity: 6000.4.7f1
- .NET検証ハーネス: `PlyExportValidation.csproj`、TargetFramework `net10.0`

Unity EditorのプロセスWorking Setは単一スナップショットで約825 MBでした。GPU使用率41%も単一時点の値で、アプリ負荷のベンチマーク値ではありません。

## 比較可能な測定

厳密KD-treeを使った純C#合成ベンチを3回実行しました。点は決定可能な乱数列で生成しています。

| 参照点 | 問合せ点 | 構築 | 検索 | 合計 |
|---:|---:|---:|---:|---:|
| 50,000 | 5,000 | 48 ms | 9 ms | 57 ms |
| 50,000 | 5,000 | 48 ms | 8 ms | 56 ms |
| 50,000 | 5,000 | 47 ms | 9 ms | 56 ms |
| 50,000 | 5,000 | 47 ms | 8 ms | 55 ms |
| 50,000 | 5,000 | 47 ms | 10 ms | 57 ms |

最終行は第2次耐障害性監査での単回再測定です。複数回平均ではありません。

この値にはUnityのTransform座標変換、Unityメインスレッドへの戻し、GPU色更新、2.2M点級データ、GC変動を含みません。旧近似処理との公平な同一入力速度比較は行っていません。今回の変更はC2Cの厳密性を保証するためのもので、速度向上率を主張しません。

## 性能上の変更

- PLY/TXTのファイル読み込みとデコードをバックグラウンドへ移動。メインスレッドに残るUnity Transform変換、点群GPU buffer生成、octree構築の詳細時間は未測定です。
- C2CのKD-tree構築と距離検索をバックグラウンド化。キャンセルは構築・検索中に定期確認します。
- GPU buffer更新時の全点ラベル走査と同期GPU readbackを除去。`ComputeBuffer.SetData`自体は全点転送のままです。

## 次の実測

Unity Profilerで約5万、50万、200万点の読込・C2C・注釈更新を測り、CPU Main Thread、GC Alloc、GPU wait、peak memoryを記録してください。条件はEditor/Player、色表示モード、LOD状態を揃え、各3回以上実施します。結果を記録するまでは「大規模点群で十分高速」とは判定しません。
