using System;

namespace PointCloudWorkbench
{
    /// <summary>
    /// 点がノイズ・削除候補と判定された詳細な理由。
    /// </summary>
    public enum RemovalReason
    {
        None = 0,
        SOR = 1,
        ROR = 2,
        LowDensity = 3,
        SmallCluster = 4,
        CC_Noise = 5,
        Manual = 6
    }

    /// <summary>
    /// Pythonバックエンドからロードされたノイズフィルタの全スコア・マスクデータを保持するクラス。
    /// </summary>
    public class NoiseFilterResult
    {
        /// <summary>
        /// 点群の総点数
        /// </summary>
        public int pointCount { get; private set; }

        /// <summary>
        /// 削除候補マスク (1 = 削除候補, 0 = 残存)
        /// </summary>
        public byte[] removeMask { get; private set; }

        /// <summary>
        /// SOR (Statistical Outlier Removal) のスコア配列
        /// </summary>
        public float[] sorScore { get; private set; }

        /// <summary>
        /// 局所密度推定スコア配列
        /// </summary>
        public float[] densityScore { get; private set; }

        /// <summary>
        /// ROR (Radius Outlier Removal) の近傍点数配列
        /// </summary>
        public int[] radiusNeighborCount { get; private set; }

        /// <summary>
        /// CC (CloudCompare) 平面残差ノイズスコア配列
        /// </summary>
        public float[] ccNoiseScore { get; private set; }

        /// <summary>
        /// DBSCANによるクラスタID配列 (-1はノイズ)
        /// </summary>
        public int[] clusterId { get; private set; }

        /// <summary>
        /// 削除理由のインデックス配列 (RemovalReasonに対応)
        /// </summary>
        public int[] reason { get; private set; }

        public NoiseFilterResult(int count)
        {
            pointCount = count;
            removeMask = new byte[count];
            sorScore = new float[count];
            densityScore = new float[count];
            radiusNeighborCount = new int[count];
            ccNoiseScore = new float[count];
            clusterId = new int[count];
            reason = new int[count];
        }

        public NoiseFilterResult(int count, byte[] mask, float[] sor, float[] density, int[] ror, float[] cc, int[] clusters, int[] reasons)
        {
            pointCount = count;
            removeMask = mask;
            sorScore = sor;
            densityScore = density;
            radiusNeighborCount = ror;
            ccNoiseScore = cc;
            clusterId = clusters;
            reason = reasons;
        }
    }
}
