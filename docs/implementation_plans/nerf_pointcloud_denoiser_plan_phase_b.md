# NeRF点群 モヤ・浮遊点除去機能 実装計画書 Phase B（Unity データ層との連携）

> ファイル: `E:/VR/docs/implementation_plans/nerf_pointcloud_denoiser_plan_phase_b.md`  
> 対象プロジェクト: `E:/VR/PointCloudVR`  
> 最終更新: 2026-05-29  

---

## 概要

Phase A（Pythonバックエンド）の完了を受け、Unity C#側においてデータ連携と非破壊プレビュー、履歴管理を行うための**データ層（Phase B）**を実装する。

**実装の核心：**
- **非同期プロセス制御**: Unityをフリーズさせずに `Process` を非同期起動し、標準出力・標準エラーをリダイレクトしてログ取得する。
- **高速デシリアライズ**: `File.ReadAllBytes` と `Buffer.BlockCopy` を組み合わせ、GCアロケーションを抑えた最高速のリトルエンディアン復元を行う。
- **非破壊ビットフラグ管理**: `PointData.label` の上位ビット（bit18/19）をマスク表示および非表示フラグに割り当て、既存のGPU構造体サイズを変更せずに可視化できるようにする。
- **履歴（Undo/Redo）スタック**: 変更前後のフラグ状態をスタックで管理し、いつでも元に戻せる安全性を確保する。

---

## 提案される変更

### 1. [NEW] [PythonBridge.cs](file:///E:/VR/PointCloudVR/Assets/PointCloudWorkbench/Scripts/PythonBridge.cs)

Pythonプロセスの起動と、生成されたデータのC#への高速デシリアライズを担当するブリッジクラス。

#### 主な仕様：
* **Python実行環境の解決**:
  * デフォルトでは `E:/VR/PointCloudVR/python_backend/.venv/Scripts/python.exe` を使用。
  * 存在しない場合はシステムの `python.exe` へのフォールバックを試みる。
* **非同期実行 (`Task<bool>`)**:
  * `System.Diagnostics.Process` を使用。
  * `ProcessStartInfo` にてウィンドウを非表示にし、標準出力・標準エラー出力を非同期受信（`OutputDataReceived`）してログファイルまたはUnityコンソールに記録する。
  * 進捗モーダルに「実行中...」等の状態を通知し、キャンセル時にはプロセスを強制終了（`Kill()`）する。
* **高速バイナリロード**:
  * `metadata.json` から点の数 `point_count` を読み込む。
  * 各 `.bin` ファイルを `File.ReadAllBytes` で読み込み、`Buffer.BlockCopy` で対応配列へコピーする。

### 2. [NEW] [NoiseFilterResult.cs](file:///E:/VR/PointCloudVR/Assets/PointCloudWorkbench/Scripts/NoiseFilterResult.cs)

Pythonからデシリアライズされたデータを保持するクラス。

#### 主な仕様：
* 以下のプロパティを保持：
  * `byte[] removeMask` (1 = 削除候補, 0 = 残存)
  * `float[] sorScore` (各点の統計的外れ点スコア)
  * `float[] densityScore` (各点の局所密度スコア)
  * `int[] radiusNeighborCount` (RORでの近傍点数)
  * `int[] clusterId` (DBSCANクラスタID、-1はノイズ)
  * `int[] reason` (削除理由: SOR/ROR/低密度/小クラスタ/手動)
* **RemovalReason Enum**:
  ```csharp
  public enum RemovalReason
  {
      None = 0,
      SOR = 1,
      ROR = 2,
      LowDensity = 3,
      SmallCluster = 4,
      Manual = 5
  }
  ```

### 3. [NEW] [NoiseFilterManager.cs](file:///E:/VR/PointCloudVR/Assets/PointCloudWorkbench/Scripts/NoiseFilterManager.cs)

点群データに対するノイズ除去情報の適用・管理・Undo履歴を司る中心マネージャクラス。

#### 主な仕様：
* **非破壊フラグ操作**:
  * `PointCloudData.cs` のビット定義に基づき、点群の `PointData` の `label` を操作する。
  * `NOISE_CANDIDATE_BIT = 0x40000` (削除候補。Phase Cで赤/オレンジなどのオーバーレイ描画対象)
  * `NOISE_HIDDEN_BIT = 0x80000` (確定非表示。シェーダ側でサイズをつぶして描画スキップ)
* **プレビュー適用 (`ApplyPreview`)**:
  * `NoiseFilterResult` に基づいて、マスクが立っている点の `label` に `NOISE_CANDIDATE_BIT` を付与。
* **変更確定 (`CommitRemoval`)**:
  * プレビュー中の点（`NOISE_CANDIDATE_BIT`）から candidate を外し、`NOISE_HIDDEN_BIT` を適用。
  * 変更前の `label` 配列（または差分）を履歴スタックに保存。
* **履歴管理**:
  * `Undo()` / `Redo()` 時に `label` 配列を書き戻し、GPUバッファ（ComputeBuffer）を即座に更新する。

---

## データ通信・バイナリデシリアライズ実装詳細（C#）

```csharp
// バイト配列から float/int 配列への高速コピー例
byte[] rawBytes = File.ReadAllBytes(path);
int floatCount = rawBytes.Length / sizeof(float);
float[] data = new float[floatCount];
Buffer.BlockCopy(rawBytes, 0, data, 0, rawBytes.Length);
```
※Windows/x64（リトルエンディアン）とPython出力（リトルエンディアン）が一致しているため、BlockCopyのみで正しく復元できます。

---

## ビットフラグと GPU レンダラ側の連携方針

`PointCloudData.cs` の `PointData` 構造体に含まれる `label` フィールドの上位ビットをフラグに拡張します。

```csharp
// 既存の PointData
public struct PointData
{
    public Vector3 position;
    public uint originalColor;
    public int label; // ← この32bit整数を拡張
    public float distance;
}

// ビット定義
public const int LABEL_MASK          = 0x000FF; // 下位8bit: 通常ラベル (0-255)
public const int NOISE_CANDIDATE_BIT = 0x40000; // bit18: 削除候補 (プレビュー用)
public const int NOISE_HIDDEN_BIT    = 0x80000; // bit19: 確定非表示 (描画除外用)
```

- **プレビュー時**: `label |= NOISE_CANDIDATE_BIT`
- **削除確定時**: `label = (label & ~NOISE_CANDIDATE_BIT) | NOISE_HIDDEN_BIT`
- **元に戻す時**: `label &= ~(NOISE_CANDIDATE_BIT | NOISE_HIDDEN_BIT)`

※GPU側で描画をスキップするシェーダー実装（Phase C）では、頂点シェーダー内で `label & 0x80000` を判定し、頂点スケールを0に設定します。

---

## 検証プラン

### 1. プロセス起動・終了テスト
- `PythonBridge` がバックグラウンドで Python プロセスを正しく立ち上げ、60秒以内で終了することを確認。
- プロセス実行中に「キャンセル」ボタンを押した場合、速やかに Python プロセスがキルされ、Unityがハングせずに復帰することを確認。

### 2. バイナリデシリアライズ整合性テスト
- Python側で生成した `remove_mask.bin` 等の点数と、C#側で `Buffer.BlockCopy` により復元した配列の長さ・中身が完全に一致することを確認（アサーションによる確認用テストコードまたはログ出力を作成）。

### 3. Undo/Redo 動作検証
- プレビュー適用→確定→Undo で点群のフラグ状態が元に戻り、再度 Redo で非表示になることをエディタ上のメモリログで確認。
