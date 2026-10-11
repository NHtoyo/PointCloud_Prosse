using System;
using UnityEngine;

namespace PointCloudWorkbench
{
    /// <summary>
    /// ノイズ除去データの適用、非破壊プレビュー、および履歴管理（Undo/Redo）を統括するマネージャクラス。
    /// </summary>
    public class NoiseFilterManager
    {
        private static NoiseFilterManager instance;
        public static NoiseFilterManager Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new NoiseFilterManager();
                }
                return instance;
            }
        }

        // PointData.label 内の上位ビット拡張定義
        public const int NOISE_CANDIDATE_BIT = 0x40000; // bit18: 削除候補 (プレビュー表示用)
        public const int NOISE_HIDDEN_BIT    = 0x80000; // bit19: 確定非表示 (描画除外用)
        public const int NOISE_REASON_MASK   = 0x700000; // bit20-22: ノイズの除去理由コード
        public const int NOISE_REASON_SHIFT  = 20;

        private NoiseFilterResult currentResult;
        private bool isPreviewActive = false;
        private PointCloudRenderer boundRenderer;
        private long boundGeneration = -1;
        private long boundRevision = -1;
        public string LastMutationFailure { get; private set; } = string.Empty;

        // Keep only changed indices and noise-owned bits, not a full label snapshot.
        private readonly PointLabelEditHistory editHistory = new PointLabelEditHistory();
        private readonly int[] previewReasonCounts = new int[8];

        public NoiseFilterResult CurrentResult => currentResult;
        public PointLabelEditHistory SharedEditHistory => editHistory;
        public bool IsPreviewActive => isPreviewActive;
        public int GetPreviewReasonCount(int reason) => reason >= 0 && reason < previewReasonCounts.Length ? previewReasonCounts[reason] : 0;
        public bool CanUndo => editHistory.CanUndo && editHistory.UndoDomain == PointLabelHistoryDomain.Noise && BoundStateIsCurrent();
        public bool CanRedo => editHistory.CanRedo && editHistory.RedoDomain == PointLabelHistoryDomain.Noise && BoundStateIsCurrent();

        /// <summary>
        /// 最新のノイズ除去処理結果を設定します。
        /// </summary>
        public bool SetResult(NoiseFilterResult result, PointCloudRenderer renderer, long datasetGeneration, long contentRevision)
        {
            if (result == null || renderer == null || renderer.DatasetGeneration != datasetGeneration ||
                renderer.ContentRevision != contentRevision ||
                !result.HasValidArrayLengths(renderer.GetPointData()?.Length ?? 0))
                return false;
            Bind(renderer);
            currentResult = result;
            boundGeneration = datasetGeneration;
            boundRevision = contentRevision;
            return true;
        }

        /// <summary>
        /// 除去結果に基づき、対象点にプレビュー用ビットフラグを立ててGPUバッファを更新します。
        /// </summary>
        public bool ApplyPreview(PointCloudRenderer renderer)
        {
            if (currentResult == null || !IsBoundToCurrentData(renderer)) return false;

            PointData[] points = renderer.GetPointData();
            if (!currentResult.HasValidArrayLengths(points?.Length ?? 0))
            {
                UnityEngine.Debug.LogWarning("[NoiseFilterManager] ノイズ結果の配列が点群と一致しません。プレビューを適用しません。");
                return false;
            }

            // プレビュービット（CANDIDATE）と理由コード（REASON）を設定
            int countSor = 0, countRor = 0, countDensity = 0, countCluster = 0, countCc = 0, countWhiteHaze = 0;
            PointLabelDelta delta = BuildNoiseDelta(points, 0x740000, 18, index =>
            {
                if (currentResult.previewMask[index] == 0)
                    return (byte)(((points[index].label & 0x740000) >> 18) & ~0x1d);
                int reason = currentResult.previewReason[index] & 7;
                return (byte)(1 | (reason << 2));
            });
            for (int i = 0; i < currentResult.previewMask.Length; i++)
            {
                if (currentResult.previewMask[i] == 0) continue;
                int reason = currentResult.previewReason[i];
                if (reason == 1) countSor++;
                else if (reason == 2) countRor++;
                else if (reason == 3) countDensity++;
                else if (reason == 4) countCluster++;
                else if (reason == 5) countCc++;
                else if (reason == 7) countWhiteHaze++;
            }

            if (!TryApplyNoiseDelta(renderer, delta, true, "プレビューを適用できませんでした。"))
                return false;
            SetPreviewReasonCounts(countSor, countRor, countDensity, countCluster, countCc, countWhiteHaze);
            isPreviewActive = true;
            boundRevision = renderer.ContentRevision;
            return true;
        }

        /// <summary>
        /// プレビュー用ビットフラグをすべての点から降ろしてGPUバッファを更新します。
        /// </summary>
        public bool ClearPreview(PointCloudRenderer renderer)
        {
            if (!IsBoundToCurrentData(renderer)) return false;

            PointData[] points = renderer.GetPointData();
            if (points == null) return false;
            PointLabelDelta delta = BuildNoiseDelta(points, 0x740000, 18,
                index => (byte)(((points[index].label & 0x740000) >> 18) & ~0x1d));
            if (!TryApplyNoiseDelta(renderer, delta, true, "プレビュー解除に失敗しました。"))
                return false;
            Array.Clear(previewReasonCounts, 0, previewReasonCounts.Length);
            isPreviewActive = false;
            boundRevision = renderer.ContentRevision;
            return true;
        }

        /// <summary>
        /// 現在プレビュー中のノイズ除去候補点を確定非表示にし、履歴スタックに保存します。
        /// </summary>
        public bool CommitRemoval(PointCloudRenderer renderer)
        {
            if (!IsBoundToCurrentData(renderer)) return false;

            PointData[] points = renderer.GetPointData();
            if (points == null) return false;
            // プレビュー点はすべて非表示確定（HIDDEN）に変換
            // White Haze のように後続計算から除外するだけの候補も、
            // ユーザーが Commit した時点で「削除対象として確定した」とみなして隠す。
            PointLabelDelta delta = BuildNoiseDelta(points, 0x0c0000, 18, index =>
            {
                int noise = (points[index].label & 0x0c0000) >> 18;
                return (byte)((noise & 1) != 0 ? ((noise & ~1) | 2) : noise);
            });
            if (delta.PointCount == 0) return false;
            if (!editHistory.CanRecord(delta))
            {
                LastMutationFailure = "ノイズUndo履歴の上限（1操作あたり128 MiB）を超えるため、点群は変更していません。";
                UnityEngine.Debug.LogWarning($"[NoiseFilterManager] {LastMutationFailure}");
                return false;
            }
            if (!TryApplyNoiseDelta(renderer, delta, true, "ノイズ候補の確定に失敗しました。"))
                return false;
            if (!editHistory.Record(delta, PointLabelHistoryDomain.Noise))
            {
                TryApplyNoiseDelta(renderer, delta, false, "ノイズ履歴を保存できませんでした。");
                LastMutationFailure = "ノイズUndo履歴を保存できなかったため、操作を取り消しました。";
                return false;
            }
            Array.Clear(previewReasonCounts, 0, previewReasonCounts.Length);
            isPreviewActive = false;
            boundRevision = renderer.ContentRevision;
            UnityEngine.Debug.Log("[NoiseFilterManager] ノイズ候補点の非表示化を確定しました。");
            return true;
        }

        /// <summary>
        /// 最後に確定したノイズ除去操作を元に戻します。
        /// </summary>
        public bool Undo(PointCloudRenderer renderer)
        {
            if (!CanUndo || !ReferenceEquals(boundRenderer, renderer)) return false;
            PointData[] points = renderer.GetPointData();
            PointLabelDelta delta = editHistory.PeekUndo();
            PointLabelHistoryDomain domain = editHistory.UndoDomain;
            if (points == null || delta == null || !TryApplyNoiseDelta(renderer, delta, false, "ノイズUndoに失敗しました。"))
                return false;
            editHistory.CompleteUndo();
            if (domain == PointLabelHistoryDomain.Noise) RebuildPreviewState(points);
            boundRevision = renderer.ContentRevision;
            UnityEngine.Debug.Log($"[NoiseFilterManager] 点群ラベル操作を Undo しました。domain={domain}");
            return true;
        }

        /// <summary>
        /// 元に戻した操作をやり直します。
        /// </summary>
        public bool Redo(PointCloudRenderer renderer)
        {
            if (!CanRedo || !ReferenceEquals(boundRenderer, renderer)) return false;
            PointData[] points = renderer.GetPointData();
            PointLabelDelta delta = editHistory.PeekRedo();
            PointLabelHistoryDomain domain = editHistory.RedoDomain;
            if (points == null || delta == null || !TryApplyNoiseDelta(renderer, delta, true, "ノイズRedoに失敗しました。"))
                return false;
            editHistory.CompleteRedo();
            if (domain == PointLabelHistoryDomain.Noise) RebuildPreviewState(points);
            boundRevision = renderer.ContentRevision;
            UnityEngine.Debug.Log($"[NoiseFilterManager] 点群ラベル操作を Redo しました。domain={domain}");
            return true;
        }

        /// <summary>
        /// 点群のすべてのノイズフラグ（プレビュー用、非表示確定用）を完全にリセットします。
        /// </summary>
        public bool ResetAllFilterFlags(PointCloudRenderer renderer)
        {
            if (renderer == null) return false;
            Bind(renderer);
            boundRevision = renderer.ContentRevision;
            PointData[] points = renderer.GetPointData();
            if (points == null) return false;
            PointLabelDelta delta = BuildNoiseDelta(points, 0x7c0000, 18, _ => 0);
            if (delta.PointCount == 0) return true;
            if (!editHistory.CanRecord(delta))
            {
                LastMutationFailure = "ノイズUndo履歴の上限（1操作あたり128 MiB）を超えるため、点群は変更していません。";
                UnityEngine.Debug.LogWarning($"[NoiseFilterManager] {LastMutationFailure}");
                return false;
            }
            if (!TryApplyNoiseDelta(renderer, delta, true, "ノイズ状態のリセットに失敗しました。")) return false;
            if (!editHistory.Record(delta, PointLabelHistoryDomain.Noise))
            {
                TryApplyNoiseDelta(renderer, delta, false, "ノイズ履歴を保存できませんでした。");
                LastMutationFailure = "ノイズUndo履歴を保存できなかったため、操作を取り消しました。";
                return false;
            }
            Array.Clear(previewReasonCounts, 0, previewReasonCounts.Length);
            isPreviewActive = false;
            boundRevision = renderer.ContentRevision;
            UnityEngine.Debug.Log("[NoiseFilterManager] すべてのノイズフィルタフラグをリセットしました。");
            return true;
        }

        // History is stored as sparse, noise-bit-only deltas.

        private static PointLabelDelta BuildNoiseDelta(PointData[] points, int mask, int shift,
            Func<int, byte> getAfterValue)
        {
            int changedCount = 0;
            for (int i = 0; i < points.Length; i++)
            {
                byte before = (byte)((points[i].label & mask) >> shift);
                if (before != getAfterValue(i)) changedCount++;
            }

            int[] indices = new int[changedCount];
            byte[] beforeValues = new byte[changedCount];
            byte[] afterValues = new byte[changedCount];
            int cursor = 0;
            for (int i = 0; i < points.Length; i++)
            {
                byte before = (byte)((points[i].label & mask) >> shift);
                byte after = getAfterValue(i);
                if (before == after) continue;
                indices[cursor] = i;
                beforeValues[cursor] = before;
                afterValues[cursor] = after;
                cursor++;
            }
            return PointLabelDelta.MaskedValues(indices, mask, shift, beforeValues, afterValues);
        }

        private bool TryApplyNoiseDelta(PointCloudRenderer renderer, PointLabelDelta delta, bool forward,
            string failureMessage)
        {
            if (delta == null || delta.PointCount == 0) return true;
            PointData[] points = renderer != null ? renderer.GetPointData() : null;
            if (points == null || !delta.MatchesExpected(points, forward))
            {
                LastMutationFailure = "ノイズ対象の状態が後から変更されたため、古い履歴を適用しませんでした。";
                UnityEngine.Debug.LogWarning($"[NoiseFilterManager] {LastMutationFailure}");
                return false;
            }

            try
            {
                delta.Apply(points, forward);
                if (!renderer.TryUpdatePointBuffer())
                    throw new InvalidOperationException("GPU点群バッファを更新できません。");
                LastMutationFailure = string.Empty;
                boundRevision = renderer.ContentRevision;
                return true;
            }
            catch (Exception updateException)
            {
                bool restored = false;
                try
                {
                    delta.Apply(points, !forward);
                    restored = renderer.TryUpdatePointBuffer();
                }
                catch (Exception rollbackException)
                {
                    UnityEngine.Debug.LogError($"[NoiseFilterManager] {failureMessage} ノイズラベル復元にも失敗しました。\n{updateException}\n{rollbackException}");
                }
                LastMutationFailure = restored
                    ? $"{failureMessage} 点群と描画状態を変更前へ戻しました。\n{updateException.Message}"
                    : $"{failureMessage} CPUラベルまたは描画状態の復元に失敗しました。点群を再読み込みしてください。\n{updateException.Message}";
                UnityEngine.Debug.LogWarning($"[RecoverableOperationError] {LastMutationFailure}");
                return false;
            }
        }

        public void ResetForPointCloud(PointCloudRenderer renderer)
        {
            if (renderer == null) return;
            Bind(renderer);
            currentResult = null;
            Array.Clear(previewReasonCounts, 0, previewReasonCounts.Length);
            isPreviewActive = false;
            LastMutationFailure = string.Empty;
        }

        private void Bind(PointCloudRenderer renderer)
        {
            if (ReferenceEquals(boundRenderer, renderer) && boundGeneration == renderer.DatasetGeneration) return;
            boundRenderer = renderer;
            boundGeneration = renderer.DatasetGeneration;
            boundRevision = renderer.ContentRevision;
            currentResult = null;
            Array.Clear(previewReasonCounts, 0, previewReasonCounts.Length);
            isPreviewActive = false;
            editHistory.Clear();
        }

        private void SetPreviewReasonCounts(int sor, int ror, int density, int cluster, int cc, int whiteHaze)
        {
            Array.Clear(previewReasonCounts, 0, previewReasonCounts.Length);
            previewReasonCounts[1] = sor;
            previewReasonCounts[2] = ror;
            previewReasonCounts[3] = density;
            previewReasonCounts[4] = cluster;
            previewReasonCounts[5] = cc;
            previewReasonCounts[7] = whiteHaze;
        }

        private void RebuildPreviewState(PointData[] points)
        {
            Array.Clear(previewReasonCounts, 0, previewReasonCounts.Length);
            isPreviewActive = false;
            if (points == null) return;
            for (int i = 0; i < points.Length; i++)
            {
                int label = points[i].label;
                if ((label & NOISE_CANDIDATE_BIT) == 0) continue;
                isPreviewActive = true;
                int reason = (label & NOISE_REASON_MASK) >> NOISE_REASON_SHIFT;
                if (reason >= 0 && reason < previewReasonCounts.Length) previewReasonCounts[reason]++;
            }
        }

        public void NotifySharedHistoryApplied(PointCloudRenderer renderer, PointData[] points)
        {
            if (!ReferenceEquals(boundRenderer, renderer) || renderer == null ||
                renderer.DatasetGeneration != boundGeneration) return;
            RebuildPreviewState(points);
            boundRevision = renderer.ContentRevision;
        }

        private bool IsBoundToCurrentData(PointCloudRenderer renderer)
        {
            if (renderer == null || !ReferenceEquals(boundRenderer, renderer) ||
                renderer.DatasetGeneration != boundGeneration)
            {
                UnityEngine.Debug.LogWarning("[NoiseFilterManager] 点群が切り替わったため、古いノイズ結果・履歴を適用しません。");
                return false;
            }
            PointData[] points = renderer.GetPointData();
            return points != null && (currentResult == null || currentResult.pointCount == points.Length);
        }

        private bool BoundStateIsCurrent()
        {
            return boundRenderer != null && boundRenderer.DatasetGeneration == boundGeneration;
        }

    }
}
