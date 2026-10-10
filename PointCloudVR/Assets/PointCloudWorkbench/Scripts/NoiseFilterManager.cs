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

        // 履歴管理スタック（ディープコピー方式、メモリ保護のため最大5段）
        private const int MAX_HISTORY = 5;
        private const long MAX_HISTORY_BYTES_PER_STACK = 16L * 1024 * 1024;
        private readonly BoundedHistory<int[]> undoStack = new BoundedHistory<int[]>(MAX_HISTORY,
            MAX_HISTORY_BYTES_PER_STACK, labels => (long)labels.Length * sizeof(int));
        private readonly BoundedHistory<int[]> redoStack = new BoundedHistory<int[]>(MAX_HISTORY,
            MAX_HISTORY_BYTES_PER_STACK, labels => (long)labels.Length * sizeof(int));
        private readonly int[] previewReasonCounts = new int[8];

        public NoiseFilterResult CurrentResult => currentResult;
        public bool IsPreviewActive => isPreviewActive;
        public int GetPreviewReasonCount(int reason) => reason >= 0 && reason < previewReasonCounts.Length ? previewReasonCounts[reason] : 0;
        public bool CanUndo => undoStack.Count > 0 && BoundStateIsCurrent();
        public bool CanRedo => redoStack.Count > 0 && BoundStateIsCurrent();

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

            if (!TryCaptureLabels(points, out int[] previousLabels)) return false;
            // プレビュービット（CANDIDATE）と理由コード（REASON）を設定
            int countSor = 0, countRor = 0, countDensity = 0, countCluster = 0, countCc = 0, countWhiteHaze = 0;
            for (int i = 0; i < points.Length; i++)
            {
                if (currentResult.previewMask[i] != 0)
                {
                    points[i].label |= NOISE_CANDIDATE_BIT;
                    int reasonVal = currentResult.previewReason[i];
                    points[i].label = (points[i].label & ~NOISE_REASON_MASK) | ((reasonVal << NOISE_REASON_SHIFT) & NOISE_REASON_MASK);
                    if (reasonVal == 1) countSor++;
                    else if (reasonVal == 2) countRor++;
                    else if (reasonVal == 3) countDensity++;
                    else if (reasonVal == 4) countCluster++;
                    else if (reasonVal == 5) countCc++;
                    else if (reasonVal == 7) countWhiteHaze++;
                }
                else
                {
                    points[i].label &= ~NOISE_CANDIDATE_BIT;
                    points[i].label &= ~NOISE_REASON_MASK;
                }
            }

            if (!TryCommitLabelMutation(renderer, points, previousLabels, "プレビューを適用できませんでした。"))
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
            if (!TryCaptureLabels(points, out int[] previousLabels)) return false;

            for (int i = 0; i < points.Length; i++)
            {
                points[i].label &= ~NOISE_CANDIDATE_BIT;
                points[i].label &= ~NOISE_REASON_MASK;
            }

            if (!TryCommitLabelMutation(renderer, points, previousLabels, "プレビュー解除に失敗しました。"))
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
            if (!TryCaptureLabels(points, out int[] previousLabels)) return false;

            // プレビュー点はすべて非表示確定（HIDDEN）に変換
            // White Haze のように後続計算から除外するだけの候補も、
            // ユーザーが Commit した時点で「削除対象として確定した」とみなして隠す。
            for (int i = 0; i < points.Length; i++)
            {
                if ((points[i].label & NOISE_CANDIDATE_BIT) != 0)
                {
                    points[i].label = (points[i].label & ~NOISE_CANDIDATE_BIT) | NOISE_HIDDEN_BIT;
                }
            }

            if (!TryCommitLabelMutation(renderer, points, previousLabels, "ノイズ候補の確定に失敗しました。"))
                return false;
            PushToUndo(previousLabels);
            redoStack.Clear();
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
            if (!CanUndo || !IsBoundToCurrentData(renderer)) return false;

            PointData[] points = renderer.GetPointData();
            if (points == null || undoStack.Peek().Length != points.Length) return false;

            if (!TryCaptureLabels(points, out int[] previousLabels)) return false;
            int[] restoreLabels = undoStack.Peek();
            for (int i = 0; i < points.Length; i++)
            {
                points[i].label = restoreLabels[i];
            }
            if (!TryCommitLabelMutation(renderer, points, previousLabels, "ノイズUndoに失敗しました。"))
                return false;
            PushToRedo(previousLabels);
            undoStack.Pop();
            isPreviewActive = HasCandidates(restoreLabels);
            RebuildPreviewReasonCounts(restoreLabels);
            boundRevision = renderer.ContentRevision;
            UnityEngine.Debug.Log($"[NoiseFilterManager] ノイズ除去操作を Undo しました。(プレビュー活性状態: {isPreviewActive})");
            return true;
        }

        /// <summary>
        /// 元に戻した操作をやり直します。
        /// </summary>
        public bool Redo(PointCloudRenderer renderer)
        {
            if (!CanRedo || !IsBoundToCurrentData(renderer)) return false;

            PointData[] points = renderer.GetPointData();
            if (points == null || redoStack.Peek().Length != points.Length) return false;

            if (!TryCaptureLabels(points, out int[] previousLabels)) return false;
            int[] restoreLabels = redoStack.Peek();
            for (int i = 0; i < points.Length; i++)
            {
                points[i].label = restoreLabels[i];
            }
            if (!TryCommitLabelMutation(renderer, points, previousLabels, "ノイズRedoに失敗しました。"))
                return false;
            PushToUndo(previousLabels);
            redoStack.Pop();
            isPreviewActive = HasCandidates(restoreLabels);
            RebuildPreviewReasonCounts(restoreLabels);
            boundRevision = renderer.ContentRevision;
            UnityEngine.Debug.Log($"[NoiseFilterManager] ノイズ除去操作を Redo しました。(プレビュー活性状態: {isPreviewActive})");
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
            if (!TryCaptureLabels(points, out int[] previousLabels)) return false;

            for (int i = 0; i < points.Length; i++)
            {
                points[i].label &= ~(NOISE_CANDIDATE_BIT | NOISE_HIDDEN_BIT | NOISE_REASON_MASK);
            }

            if (!TryCommitLabelMutation(renderer, points, previousLabels, "ノイズ状態のリセットに失敗しました。"))
                return false;
            PushToUndo(previousLabels);
            redoStack.Clear();
            Array.Clear(previewReasonCounts, 0, previewReasonCounts.Length);
            isPreviewActive = false;
            boundRevision = renderer.ContentRevision;
            UnityEngine.Debug.Log("[NoiseFilterManager] すべてのノイズフィルタフラグをリセットしました。");
            return true;
        }

        private void PushToUndo(int[] labels)
        {
            if (!undoStack.Push(labels))
                UnityEngine.Debug.LogWarning("[NoiseFilterManager] 点群が大きいためUndo履歴のメモリ上限を超えました。今回の操作はUndo対象になりません。");
        }

        private void PushToRedo(int[] labels)
        {
            if (!redoStack.Push(labels))
                UnityEngine.Debug.LogWarning("[NoiseFilterManager] 点群が大きいためRedo履歴のメモリ上限を超えました。今回の操作はRedo対象になりません。");
        }

        private bool TryCaptureLabels(PointData[] points, out int[] labels)
        {
            labels = null;
            long snapshotBytes = (long)points.Length * sizeof(int);
            if (snapshotBytes > MAX_HISTORY_BYTES_PER_STACK)
            {
                LastMutationFailure = $"点群が大きく、noise rollback snapshotの上限（{MAX_HISTORY_BYTES_PER_STACK / (1024 * 1024)} MiB）を超えています。点群は変更していません。";
                UnityEngine.Debug.LogWarning($"[NoiseFilterManager] {LastMutationFailure}");
                return false;
            }

            labels = new int[points.Length];
            for (int i = 0; i < points.Length; i++) labels[i] = points[i].label;
            return true;
        }

        private static bool HasCandidates(int[] labels)
        {
            for (int i = 0; i < labels.Length; i++)
                if ((labels[i] & NOISE_CANDIDATE_BIT) != 0) return true;
            return false;
        }

        private bool TryCommitLabelMutation(PointCloudRenderer renderer, PointData[] points,
            int[] previousLabels, string failureMessage)
        {
            try
            {
                if (!renderer.TryUpdatePointBuffer())
                    throw new InvalidOperationException("GPU点群バッファを更新できません。");
                LastMutationFailure = string.Empty;
                return true;
            }
            catch (Exception updateException)
            {
                for (int i = 0; i < points.Length; i++) points[i].label = previousLabels[i];
                bool gpuRestored = false;
                try
                {
                    gpuRestored = renderer.TryUpdatePointBuffer();
                }
                catch (Exception rollbackException)
                {
                    UnityEngine.Debug.LogError($"[NoiseFilterManager] {failureMessage} CPUラベルは復元しましたがGPU再同期にも失敗しました。\n{updateException}\n{rollbackException}");
                }
                boundRevision = renderer.ContentRevision;
                LastMutationFailure = gpuRestored
                    ? $"{failureMessage} 点群と描画状態を変更前へ戻しました。\n{updateException.Message}"
                    : $"{failureMessage} CPUラベルは戻しましたが描画バッファの同期に失敗しました。点群表示を再読み込みしてください。\n{updateException.Message}";
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
            if (!PointCloudRevisionBinding.RequiresReset(boundRenderer, boundGeneration, boundRevision,
                renderer, renderer.DatasetGeneration, renderer.ContentRevision)) return;
            boundRenderer = renderer;
            boundGeneration = renderer.DatasetGeneration;
            boundRevision = renderer.ContentRevision;
            currentResult = null;
            Array.Clear(previewReasonCounts, 0, previewReasonCounts.Length);
            isPreviewActive = false;
            undoStack.Clear();
            redoStack.Clear();
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

        private void RebuildPreviewReasonCounts(int[] labels)
        {
            Array.Clear(previewReasonCounts, 0, previewReasonCounts.Length);
            if (labels == null) return;
            for (int i = 0; i < labels.Length; i++)
            {
                int label = labels[i];
                if ((label & NOISE_CANDIDATE_BIT) == 0) continue;
                int reason = (label & NOISE_REASON_MASK) >> NOISE_REASON_SHIFT;
                if (reason >= 0 && reason < previewReasonCounts.Length) previewReasonCounts[reason]++;
            }
        }

        private bool IsBoundToCurrentData(PointCloudRenderer renderer)
        {
            if (renderer == null || !ReferenceEquals(boundRenderer, renderer) ||
                renderer.DatasetGeneration != boundGeneration || renderer.ContentRevision != boundRevision)
            {
                UnityEngine.Debug.LogWarning("[NoiseFilterManager] 点群または編集状態が解析後に変化したため、古いノイズ結果・履歴を適用しません。");
                return false;
            }
            PointData[] points = renderer.GetPointData();
            return points != null && (currentResult == null || currentResult.pointCount == points.Length);
        }

        private bool BoundStateIsCurrent()
        {
            return boundRenderer != null && boundRenderer.DatasetGeneration == boundGeneration &&
                boundRenderer.ContentRevision == boundRevision;
        }

    }
}
