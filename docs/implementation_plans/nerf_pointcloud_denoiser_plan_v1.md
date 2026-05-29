# NeRF点群 モヤ・浮遊点除去機能 実装計画書 v1.2（最終確定版）

> ファイル: `E:/VR/docs/implementation_plans/nerf_pointcloud_denoiser_plan_v1.md`
> 対象プロジェクト: `E:/VR/PointCloudVR`（PointCloudVR Workbench）
> 最終更新: 2026-05-29

---

## 概要

NeRF / SfM / VIO などから出力された植物点群（トマト等）に含まれる、空中のモヤ・浮遊点・低密度ノイズ・小さな不要クラスタを、ユーザーが確認しながら安全に除去できる機能を追加する。

**設計の核心原則：**
- 削除は必ずプレビュー式（`removeCandidate`→ユーザー確定後に非表示）
- 元点群配列は決して失わない（エクスポート時のみ物理削除）
- Undo は最低1段階、設計上は複数段階のスタック対応
- Unity = UI / 可視化 / 操作、Python = 重い解析処理
- **Phase A（Python）を先行して完成させてから Unity 連携に入る**

---

## 通信方式・データ受け渡し仕様

### Python → C# の受け渡し形式

NPZ を C# との正式受け渡し形式に **しない**。代わりに以下の形式を使用：

```
output_dir/
├── remove_mask.bin          # bool[N]  を uint8 で保存（0/1）
├── sor_score.bin            # float32[N]
├── density_score.bin        # float32[N]
├── radius_neighbor_count.bin # int32[N]
├── cluster_id.bin           # int32[N]
├── reason.bin               # int32[N]  (RemovalReason enum: 0〜5)
├── metadata.json            # 各binファイルのメタ情報
└── removal_report.json      # 処理サマリー
```

### metadata.json 仕様

```json
{
  "point_count": 3273732,
  "mode": "full",
  "dbscan_mode": "downsample",
  "dbscan_voxel_size": 0.005,
  "dbscan_analysis_count": 180000,
  "voxel_size": null,
  "files": {
    "remove_mask":           {"filename": "remove_mask.bin",           "dtype": "uint8",   "shape": [3273732]},
    "sor_score":             {"filename": "sor_score.bin",             "dtype": "float32", "shape": [3273732]},
    "density_score":         {"filename": "density_score.bin",         "dtype": "float32", "shape": [3273732]},
    "radius_neighbor_count": {"filename": "radius_neighbor_count.bin", "dtype": "int32",   "shape": [3273732]},
    "cluster_id":            {"filename": "cluster_id.bin",            "dtype": "int32",   "shape": [3273732]},
    "reason":                {"filename": "reason.bin",                "dtype": "int32",   "shape": [3273732]}
  },
  "parameters": {
    "sor": {"nb_neighbors": 20, "std_ratio": 1.5},
    "ror": {"radius_multiplier": 3.0, "min_neighbors": 8},
    "dbscan": {"eps_multiplier": 4.0, "min_points": 10, "min_cluster_size": 200,
               "mode": "downsample", "downsample_target": 200000, "timeout_sec": 120}
  }
}
```

C# 側は `metadata.json` を読み、各 `.bin` を `File.ReadAllBytes()` で読み込んで `float[]`/`int[]` 等に変換する。

### removal_report.json 仕様

```json
{
  "timestamp": "2026-05-29T17:00:00",
  "mode": "downsample_preview",
  "original_point_count": 3273732,
  "analysis_point_count": 245000,
  "voxel_size": 0.003,
  "downsample_ratio": 0.0748,
  "kept_point_count": 3228612,
  "removed_candidate_count": 45120,
  "removed_by_sor": 12000,
  "removed_by_ror": 8000,
  "removed_by_low_density": 0,
  "removed_by_small_cluster": 25120,
  "parameters_used": {
    "sor": {"nb_neighbors": 20, "std_ratio": 1.5},
    "ror": {"radius_multiplier": 3.0, "min_neighbors": 8},
    "dbscan": {"eps_multiplier": 4.0, "min_points": 10, "min_cluster_size": 200}
  }
}
```

---

## CLIインターフェース

```bash
# Full mode（SOR/ROR/密度は元点群全体、DBSCANは自動Downsample）
python run_noise_filter.py \
  --input  E:/VR/PointCloudData/rei1.ply \
  --output_dir E:/VR/PointCloudVR/python_backend/output \
  --mode full

# Downsample Preview mode（全フィルタをダウンサンプル点群に適用）
python run_noise_filter.py \
  --input  E:/VR/PointCloudData/rei1.ply \
  --output_dir E:/VR/PointCloudVR/python_backend/output \
  --mode downsample \
  --voxel_size 0.005

# パラメータ個別指定
python run_noise_filter.py --input xxx.ply --output_dir out --mode full \
  --filters sor ror dbscan \
  --sor_nb 20 --sor_std 1.5 \
  --ror_mul 3.0 --ror_min 8 \
  --dbscan_eps 4.0 --dbscan_min 10 --dbscan_cluster 200 \
  --dbscan_target 200000
```

> **スケール注記:** 現在の点群（rei1.ply）はメートル単位。将来的にミリメートル単位へ移行予定。
> `base_spacing` の自動推定はスケール依存のため、移行時は `voxel_size` / `eps` の初期値を調整すること。

---

## Python バックエンド ファイル構成

```
E:/VR/PointCloudVR/python_backend/
├── run_noise_filter.py      ← CLIエントリポイント（メイン）
├── pointcloud_io.py         ← PLY/NPZ 読み書き
├── noise_filters.py         ← SOR / ROR / 密度 / DBSCAN
├── result_writer.py         ← bin + metadata.json + report 書き出し
├── requirements.txt
└── tests/
    ├── test_filters.py      ← テスト1〜3（自動テスト）
    └── generate_test_data.py ← テスト用点群生成スクリプト
```

### Python 環境

```
E:/VR/PointCloudVR/python_backend/.venv/
```

作成コマンド：
```bash
python -m venv E:/VR/PointCloudVR/python_backend/.venv
E:/VR/PointCloudVR/python_backend/.venv/Scripts/activate
pip install -r requirements.txt
```

---

## 各ファイルの詳細仕様

### requirements.txt

```
open3d
numpy
scipy
fastapi
uvicorn
pydantic
```

### pointcloud_io.py

```python
def load_ply(path: str) -> tuple[np.ndarray, np.ndarray]:
    """returns: (points: float32[N,3], colors: uint8[N,3])"""

def save_ply(path: str, points, colors, mask=None):
    """mask=True の点のみ書き出し（cleaned.ply 用）"""

def load_npz(path: str) -> tuple[np.ndarray, np.ndarray]:
    """NPZ は Python 内部用（C#との通信には使わない）"""
```

### noise_filters.py

```python
def estimate_base_spacing(points, k=8) -> float:
    """8近傍距離の中央値を base_spacing として返す"""

def compute_sor(points, nb_neighbors=20, std_ratio=1.5) -> dict:
    """
    各点の k近傍平均距離を計算し、全体の μ+σ*std_ratio を超える点を候補にする
    returns: {
        'remove_mask': bool[N],
        'sor_score':   float32[N]  # (mean_dist - μ) / σ
    }
    """

def compute_ror(points, radius_multiplier=3.0, min_neighbors=8) -> dict:
    """
    base_spacing * radius_multiplier 以内の近傍点数が
    min_neighbors 未満の点を候補にする
    returns: {
        'remove_mask':           bool[N],
        'radius_neighbor_count': int32[N]
    }
    """

def compute_density(points, k=8) -> dict:
    """
    density_score = 1 / (k近傍平均距離 + 1e-6)
    returns: {'density_score': float32[N]}
    """

def compute_dbscan(points, base_spacing, eps_multiplier=4.0,
                   min_points=10, min_cluster_size=200) -> dict:
    """
    eps = base_spacing * eps_multiplier
    cluster_id == -1 → ノイズ候補
    点数 < min_cluster_size のクラスタ → small_cluster 候補
    returns: {
        'cluster_id':  int32[N],
        'remove_mask': bool[N]
    }
    """

def run_all_filters(points, params: dict) -> dict:
    """SOR/ROR/密度/DBSCANを一括実行し、結果をマージして返す"""
```

**reason（RemovalReason）の優先順位：** SOR > ROR > SmallCluster > LowDensity（複数に該当する場合は上位を採用）

### result_writer.py

```python
def write_results(output_dir, results: dict, params: dict, 
                  mode: str, original_count: int,
                  analysis_count: int, voxel_size=None):
    """
    remove_mask.bin, sor_score.bin, ... を書き出す
    metadata.json と removal_report.json も生成
    """

def read_bin(path: str, dtype: str) -> np.ndarray:
    """C#との通信形式 .bin を読み込む（テスト用）"""
```

### run_noise_filter.py

CLIのエントリポイント。argparse で引数を受け取り、以下を実行：

1. PLY を読み込む
2. mode に応じて Full / Downsample 処理分岐
3. `run_all_filters()` を呼ぶ
4. `write_results()` で bin + JSON 出力
5. 処理時間・点数サマリーを標準出力に表示

---

## 処理モード詳細

### フィルタ別のモード制御方針

| フィルタ | Full mode | Downsample Preview mode |
|---|---|---|
| SOR | N点全体に適用 ✅ | Downsample M点に適用 ✅ |
| ROR | N点全体に適用 ✅ | Downsample M点に適用 ✅ |
| 密度スコア | N点全体に適用 ✅ | Downsample M点に適用 ✅ |
| DBSCAN | **自動Downsample強制** ⚠ | Downsample M点に適用 ✅ |

### DBSCANのモード自動制御ロジック

```python
DBSCAN_FULL_LIMIT    = 300_000   # これ以下ならFull DBSCANを許可
DBSCAN_WARN_LIMIT    = 1_000_000 # これ超はUI警告（将来）
DBSCAN_TARGET_POINTS = 200_000   # Downsample時の目標点数（10万〜30万）
DBSCAN_TIMEOUT_SEC   = 120       # タイムアウト上限

def decide_dbscan_mode(n_points):
    if n_points <= DBSCAN_FULL_LIMIT:
        return 'full'       # Full DBSCAN を許可
    else:
        return 'downsample' # 自動的にDownsample強制
```

- **N ≤ 300,000点**: DBSCAN Full mode 許可
- **300,000 < N ≤ 1,000,000点**: DBSCAN を Downsample 強制（警告なし）
- **N > 1,000,000点**: DBSCAN を Downsample 強制（将来UIで警告表示）
- **rei1.ply (3.27M点)**: DBSCAN は常に Downsample 強制
- **タイムアウト**: DBSCAN が120秒を超えた場合、中断してプレビュー扱いで結果を返す

### Full mode の実際の処理フロー

```
PLY 読み込み (N点)
    ↓
SOR → N点全体に適用 → remove_mask_sor[N]
    ↓
ROR → N点全体に適用 → remove_mask_ror[N]
    ↓
密度スコア → N点全体に計算 → density_score[N]
    ↓
DBSCAN → decide_dbscan_mode(N) で分岐
  ├─ Full (N≤300k): N点に適用 → cluster_id[N]
  └─ Downsample強制: voxel_down_sample → M点 → DBSCAN → cluster_id[M]
       ↓ (初期実装ではM点のプレビュー。次フェーズでN点への伝播追加)
    ↓
全結果マージ → bin + metadata.json + removal_report.json 出力
```

### Downsample Preview mode の処理フロー

```
PLY 読み込み (N点)
    ↓
voxel_down_sample(voxel_size) → M点 (目標: 10万〜30万点)
    ↓
SOR / ROR / 密度 / DBSCAN を M点に一括適用
    ↓
結果 M点分を bin + metadata.json に出力
（全フィルタがM点規模で高速動作するプレビューモード）
```

### 次フェーズ追加予定（ラベル伝播）

```
Downsample M点で得たcluster_id / remove_mask
    ↓
KDTree最近傍探索で元N点の各点に最近傍のM点を対応付け
    ↓
対応M点のラベルをN点へ伝播 → N点のremoveCandidateを確定
```

---

## テスト仕様（tests/test_filters.py）

### テスト1：浮遊点除去テスト（SOR/ROR）

```python
# 球面上の密な点群 (1000点) + 空中に浮いた外れ点 (20点)
# → SOR/ROR でその20点が remove_mask=True になることを確認
# 基準: 浮遊点の90%以上が候補になること
```

### テスト2：細い線状点群のSoft vs Strong 比較

```python
# 細い線状点群 (cylinder, radius=0.001m) + 主球面点群
# Soft 設定: 線状点群の80%以上が残ること
# Strong 設定: 線状点群の50%以上が候補になること
# → 「Strongは細い構造を消しやすい」ことを確認
```

### テスト3：小クラスタ検出テスト（DBSCAN）

```python
# 大きなクラスタ1つ (3000点) + 小さなクラスタ3つ (各30点) + ノイズ点 (20点)
# → 小さなクラスタ3つがすべて small_cluster 候補になることを確認
# → 大きなクラスタ (3000点) は候補にならないことを確認
```

---

## Unity 連携（Phase B 以降）

### C# 側の読み込みフロー（後述）

```csharp
// PythonBridge.cs の実装概要（Phase B で実装）
string metadataJson = File.ReadAllText(outputDir + "/metadata.json");
var meta = JsonUtility.FromJson<NoiseFilterMetadata>(metadataJson);

byte[] rawBytes = File.ReadAllBytes(outputDir + "/remove_mask.bin");
// uint8 → bool[] に変換
bool[] removeMask = rawBytes.Select(b => b != 0).ToArray();

byte[] scoreBytes = File.ReadAllBytes(outputDir + "/sor_score.bin");
// float32 LE のバイト列 → float[] に変換
float[] sorScore = new float[meta.point_count];
Buffer.BlockCopy(scoreBytes, 0, sorScore, 0, scoreBytes.Length);
```

### PointCloudData ビット拡張（Phase B で実装）

```csharp
// PointCloudData.cs への追記（GPU構造体は変更しない）
public const int NOISE_CANDIDATE_BIT = 0x40000;  // bit18: 削除候補
public const int NOISE_HIDDEN_BIT    = 0x80000;  // bit19: 確定非表示
```

### カラーオーバーレイ仕様（Phase C で実装）

| 種別 | 色 |
|---|---|
| 通常点 | 元のRGB |
| SOR 候補 | 赤 (255, 30, 30) |
| ROR 候補 | オレンジ (255, 140, 0) |
| 低密度候補 | 紫 (160, 30, 230) |
| 小クラスタ候補 | 黄 (255, 220, 0) |
| 確定非表示 | GPU で除外（描画スキップ） |

---

## 実装フェーズ

### Phase A: Python バックエンド（現在のフェーズ）

**完了条件：**
- [ ] Python 環境確認（`python --version`）または venv 作成完了
- [ ] `pip install -r requirements.txt` が成功する
- [ ] PLY ファイルを読み込める（`rei1.ply` で確認）
- [ ] `estimate_base_spacing()` が動く（スケールに依存しない自動推定）
- [ ] SOR が動く（remove_mask, sor_score を出力できる）
- [ ] ROR が動く（remove_mask, radius_neighbor_count を出力できる）
- [ ] 密度スコアが動く（density_score を出力できる）
- [ ] DBSCAN が動く（自動モード判定 + タイムアウト120秒対応）
- [ ] `decide_dbscan_mode(N)` の分岐が正しく動く
- [ ] Full mode（SOR/ROR/密度はフル、DBSCANは自動判定）で出力できる
- [ ] Downsample Preview mode で全フィルタが動く
- [ ] bin + metadata.json（dbscan_mode/dbscan_voxel_size 含む） + removal_report.json を出力できる
- [ ] テスト1〜3 がパスする

### Phase B: Unity データ層（Python 完了後）

- PythonBridge.cs: 非同期プロセス起動 + bin 読み込み
- NoiseFilterResult.cs: 結果データクラス
- NoiseFilterManager.cs: 非破壊フラグ管理・Undo スタック

### Phase C: Unity 表示層

- PointCloudRenderer.cs: colorMode=4 追加
- PointCloudShader.shader: ノイズ候補色の分岐追加
- NoiseFilterOverlay.cs: オーバーレイ適用ロジック

### Phase D: Unity UI

- NoiseFilterUI.cs: パネル全体実装
- PointCloudEditorUI.cs: 折りたたみセクション統合

### Phase E: 出力・最終テスト

- ExportCleaned() / ExportReport() 実装
- HANDOVER_DOC.md 更新

---

## 確定済み設計決定事項

| 項目 | 決定内容 |
|---|---|
| C#通信形式 | `.bin` + `metadata.json`（NPZ は使わない） |
| DBSCAN Full mode | N ≤ 300,000 点のみ許可 |
| DBSCAN 大規模時 | Downsample 強制（目標 10万〜30万点） |
| DBSCAN タイムアウト | 120秒で中断・プレビュー扱い |
| rei1.ply (3.27M点) | DBSCAN は常に Downsample 強制 |
| 点群スケール（現在） | メートル単位（将来ミリメートルへ移行予定） |
| スケール移行時の対応 | `base_spacing` 自動推定で吸収。`voxel_size`/`eps` 初期値を再調整する |
| ラベル伝播 | 次フェーズ（KDTree最近傍でN点へ伝播）で追加 |
| Python 環境 | `E:/VR/PointCloudVR/python_backend/.venv`（venv形式、環境構築時に確認） |

## Phase A 着手時の最初のステップ

1. `python --version` で実行環境確認
2. venv が必要なら `python -m venv E:/VR/PointCloudVR/python_backend/.venv` で作成
3. `requirements.txt` 作成 → `pip install`
4. `rei1.ply` で `estimate_base_spacing()` を走らせてスケール確認
5. `pointcloud_io.py` → `noise_filters.py` → `result_writer.py` → `run_noise_filter.py` の順に実装
6. テスト点群でテスト1〜3 を確認
