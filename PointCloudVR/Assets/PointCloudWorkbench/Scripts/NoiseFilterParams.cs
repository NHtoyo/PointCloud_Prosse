using System;
using System.Collections.Generic;

namespace PointCloudWorkbench
{
    /// <summary>
    /// 各フィルタのパラメータとパイプライン制御フラグをまとめるデータ基底クラス。
    /// </summary>
    [System.Serializable]
    public class FilterStepConfig
    {
        public string name;           // フィルタ識別子 ("white_haze", "sor", etc.)
        public bool enabled;          // 有効フラグ
        public bool excludeFromNext;  // 候補を以降から除外するか
    }

    [System.Serializable]
    public class WhiteHazeConfig : FilterStepConfig
    {
        public float brightness = 190.0f;
        public float saturation = 0.20f;

        public WhiteHazeConfig()
        {
            name = "white_haze";
            enabled = true;
            excludeFromNext = true;
        }
    }

    [System.Serializable]
    public class CcConfig : FilterStepConfig
    {
        public bool useKnn = true;
        public int k = 20;
        public float radius = 0.05f;
        public bool removeIsolated = false;
        public bool useRelative = true;
        public float sigma = 1.0f;
        public float error = 0.01f;

        public CcConfig()
        {
            name = "cc_noise";
            enabled = true;
            excludeFromNext = true; // デフォルトで次段から除外
        }
    }

    [System.Serializable]
    public class SorConfig : FilterStepConfig
    {
        public int nb = 20;
        public float std = 1.5f;

        public SorConfig()
        {
            name = "sor";
            enabled = true;
            excludeFromNext = true; // デフォルトで次段から除外
        }
    }

    [System.Serializable]
    public class RorConfig : FilterStepConfig
    {
        public float mul = 3.0f;
        public int min = 8;

        public RorConfig()
        {
            name = "ror";
            enabled = true;
            excludeFromNext = true; // デフォルトで次段から除外
        }
    }

    [System.Serializable]
    public class DensityConfig : FilterStepConfig
    {
        public int k = 8;
        public float threshold = 0.0f;
        public float percentile = 3.0f;

        public DensityConfig()
        {
            name = "density";
            enabled = true;
            excludeFromNext = true; // デフォルトで次段から除外
        }
    }

    [System.Serializable]
    public class DbscanConfig : FilterStepConfig
    {
        public float eps = 4.0f;
        public int min = 10;
        public int cluster = 200;
        public int target = 200000;
        public int timeout = 120;

        public DbscanConfig()
        {
            name = "dbscan";
            enabled = true;
            excludeFromNext = true; // デフォルトで次段から除外
        }
    }

    /// <summary>
    /// 全フィルタパラメータとパイプライン構成を保持する統合パラメータクラス。
    /// </summary>
    [System.Serializable]
    public class NoiseFilterParams
    {
        // 個々の設定オブジェクトへの参照
        public WhiteHazeConfig whiteHaze = new WhiteHazeConfig();
        public CcConfig cc = new CcConfig();
        public SorConfig sor = new SorConfig();
        public RorConfig ror = new RorConfig();
        public DensityConfig density = new DensityConfig();
        public DbscanConfig dbscan = new DbscanConfig();

        // 実行モード
        public string processMode = "full";
        public float voxelSize = 0.005f;

        // 動的なパイプライン順序を保持するリスト
        public List<FilterStepConfig> customPipeline = new List<FilterStepConfig>();

        /// <summary>
        /// パイプラインの順序定義リストを生成します。
        /// </summary>
        public List<FilterStepConfig> GetPipeline()
        {
            if (customPipeline != null && customPipeline.Count > 0)
            {
                return customPipeline;
            }
            return new List<FilterStepConfig>
            {
                whiteHaze,
                cc,
                sor,
                ror,
                density,
                dbscan
            };
        }
    }
}
