using System;
using System.Collections.Generic;
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

        private NoiseFilterResult currentResult;
        private bool isPreviewActive = false;

        // 履歴管理スタック（ディープコピー方式、メモリ保護のため最大5段）
        private const int MAX_HISTORY = 5;
        private Stack<int[]> undoStack = new Stack<int[]>();
        private Stack<int[]> redoStack = new Stack<int[]>();

        public NoiseFilterResult CurrentResult => currentResult;
        public bool IsPreviewActive => isPreviewActive;
        public bool CanUndo => undoStack.Count > 0;
        public bool CanRedo => redoStack.Count > 0;

        /// <summary>
        /// 最新のノイズ除去処理結果を設定します。
        /// </summary>
        public void SetResult(NoiseFilterResult result)
        {
            currentResult = result;
        }

        /// <summary>
        /// 除去結果に基づき、対象点にプレビュー用ビットフラグを立ててGPUバッファを更新します。
        /// </summary>
        public void ApplyPreview(PointCloudRenderer renderer)
        {
            if (currentResult == null || renderer == null) return;

            PointData[] points = renderer.GetPointData();
            if (points == null || points.Length != currentResult.pointCount)
            {
                UnityEngine.Debug.LogError($"[NoiseFilterManager] 点群サイズがフィルタ結果と一致しません。点数: {points?.Length ?? 0}, フィルタ点数: {currentResult.pointCount}");
                return;
            }

            // プレビュービット（CANDIDATE）をマスクに基づいて設定
            for (int i = 0; i < points.Length; i++)
            {
                if (currentResult.removeMask[i] != 0)
                {
                    points[i].label |= NOISE_CANDIDATE_BIT;
                }
                else
                {
                    points[i].label &= ~NOISE_CANDIDATE_BIT;
                }
            }

            isPreviewActive = true;
            renderer.UpdatePointBuffer();
        }

        /// <summary>
        /// プレビュー用ビットフラグをすべての点から降ろしてGPUバッファを更新します。
        /// </summary>
        public void ClearPreview(PointCloudRenderer renderer)
        {
            if (renderer == null) return;

            PointData[] points = renderer.GetPointData();
            if (points == null) return;

            for (int i = 0; i < points.Length; i++)
            {
                points[i].label &= ~NOISE_CANDIDATE_BIT;
            }

            isPreviewActive = false;
            renderer.UpdatePointBuffer();
        }

        /// <summary>
        /// 現在プレビュー中のノイズ除去候補点を確定非表示にし、履歴スタックに保存します。
        /// </summary>
        public void CommitRemoval(PointCloudRenderer renderer)
        {
            if (renderer == null) return;

            PointData[] points = renderer.GetPointData();
            if (points == null) return;

            // 変更前のlabel状態をUndoスタックに保存
            PushToUndo(points);
            redoStack.Clear(); // 新規変更時はRedoをリセット

            // プレビュー点（CANDIDATE）を非表示確定（HIDDEN）に変換
            for (int i = 0; i < points.Length; i++)
            {
                if ((points[i].label & NOISE_CANDIDATE_BIT) != 0)
                {
                    points[i].label = (points[i].label & ~NOISE_CANDIDATE_BIT) | NOISE_HIDDEN_BIT;
                }
            }

            isPreviewActive = false;
            renderer.UpdatePointBuffer();
            UnityEngine.Debug.Log("[NoiseFilterManager] ノイズ候補点の非表示化を確定しました。");
        }

        /// <summary>
        /// 最後に確定したノイズ除去操作を元に戻します。
        /// </summary>
        public bool Undo(PointCloudRenderer renderer)
        {
            if (!CanUndo || renderer == null) return false;

            PointData[] points = renderer.GetPointData();
            if (points == null) return false;

            // 現在の状態をRedoに保存
            PushToRedo(points);

            // Undoスタックから値を復元
            int[] prevLabels = undoStack.Pop();
            for (int i = 0; i < points.Length; i++)
            {
                points[i].label = prevLabels[i];
            }

            renderer.UpdatePointBuffer();
            UnityEngine.Debug.Log("[NoiseFilterManager] ノイズ除去操作を Undo しました。");
            return true;
        }

        /// <summary>
        /// 元に戻した操作をやり直します。
        /// </summary>
        public bool Redo(PointCloudRenderer renderer)
        {
            if (!CanRedo || renderer == null) return false;

            PointData[] points = renderer.GetPointData();
            if (points == null) return false;

            // 現在の状態をUndoに保存
            PushToUndo(points);

            // Redoスタックから値を復元
            int[] nextLabels = redoStack.Pop();
            for (int i = 0; i < points.Length; i++)
            {
                points[i].label = nextLabels[i];
            }

            renderer.UpdatePointBuffer();
            UnityEngine.Debug.Log("[NoiseFilterManager] ノイズ除去操作を Redo しました。");
            return true;
        }

        /// <summary>
        /// 点群のすべてのノイズフラグ（プレビュー用、非表示確定用）を完全にリセットします。
        /// </summary>
        public void ResetAllFilterFlags(PointCloudRenderer renderer)
        {
            if (renderer == null) return;
            PointData[] points = renderer.GetPointData();
            if (points == null) return;

            PushToUndo(points);
            redoStack.Clear();

            for (int i = 0; i < points.Length; i++)
            {
                points[i].label &= ~(NOISE_CANDIDATE_BIT | NOISE_HIDDEN_BIT);
            }

            isPreviewActive = false;
            renderer.UpdatePointBuffer();
            UnityEngine.Debug.Log("[NoiseFilterManager] すべてのノイズフィルタフラグをリセットしました。");
        }

        private void PushToUndo(PointData[] points)
        {
            if (undoStack.Count >= MAX_HISTORY)
            {
                // 最も古い履歴を破棄するために展開
                var list = new List<int[]>(undoStack);
                list.RemoveAt(0);
                undoStack = new Stack<int[]>(list);
            }

            int[] labels = new int[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                labels[i] = points[i].label;
            }
            undoStack.Push(labels);
        }

        private void PushToRedo(PointData[] points)
        {
            if (redoStack.Count >= MAX_HISTORY)
            {
                var list = new List<int[]>(redoStack);
                list.RemoveAt(0);
                redoStack = new Stack<int[]>(list);
            }

            int[] labels = new int[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                labels[i] = points[i].label;
            }
            redoStack.Push(labels);
        }
    }
}
