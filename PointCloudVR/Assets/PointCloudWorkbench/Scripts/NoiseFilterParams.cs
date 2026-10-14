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
        public float radius = 50f;
        public bool removeIsolated = false;
        public bool useRelative = true;
        public float sigma = 1.0f;
        public float error = 10f;

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
        public float threshold = 0.0f; // density score threshold in 1/mm
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

    [System.Serializable]
    public class ComponentKeepConfig : FilterStepConfig
    {
        public int knnK = 8;
        public float alpha = 2.5f;
        public string mode = "largest";

        public ComponentKeepConfig()
        {
            name = "component_keep";
            enabled = true;
            excludeFromNext = true;
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
        public ComponentKeepConfig componentKeep = new ComponentKeepConfig();

        // 実行モード
        public string processMode = "full";
        public float voxelSize = 5f;

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

        public bool HasEnabledComponentKeep()
        {
            List<FilterStepConfig> pipeline = GetPipeline();
            for (int i = 0; i < pipeline.Count; i++)
            {
                if (pipeline[i] != null && pipeline[i].name == "component_keep" && pipeline[i].enabled)
                    return true;
            }
            return false;
        }

        public NoiseFilterParams DeepCopy()
        {
            var copy = new NoiseFilterParams
            {
                processMode = processMode,
                voxelSize = voxelSize,
                whiteHaze = (WhiteHazeConfig)Copy(whiteHaze),
                cc = (CcConfig)Copy(cc),
                sor = (SorConfig)Copy(sor),
                ror = (RorConfig)Copy(ror),
                density = (DensityConfig)Copy(density),
                dbscan = (DbscanConfig)Copy(dbscan),
                componentKeep = (ComponentKeepConfig)Copy(componentKeep),
                customPipeline = new List<FilterStepConfig>()
            };

            List<FilterStepConfig> pipeline = GetPipeline();
            for (int i = 0; pipeline != null && i < pipeline.Count; i++)
                copy.customPipeline.Add(Copy(pipeline[i]));
            return copy;
        }

        private static FilterStepConfig Copy(FilterStepConfig step)
        {
            if (step == null) return null;
            if (step is WhiteHazeConfig wh)
                return new WhiteHazeConfig { name = wh.name, enabled = wh.enabled, excludeFromNext = wh.excludeFromNext,
                    brightness = wh.brightness, saturation = wh.saturation };
            if (step is CcConfig cc)
                return new CcConfig { name = cc.name, enabled = cc.enabled, excludeFromNext = cc.excludeFromNext,
                    useKnn = cc.useKnn, k = cc.k, radius = cc.radius, removeIsolated = cc.removeIsolated,
                    useRelative = cc.useRelative, sigma = cc.sigma, error = cc.error };
            if (step is SorConfig sor)
                return new SorConfig { name = sor.name, enabled = sor.enabled, excludeFromNext = sor.excludeFromNext,
                    nb = sor.nb, std = sor.std };
            if (step is RorConfig ror)
                return new RorConfig { name = ror.name, enabled = ror.enabled, excludeFromNext = ror.excludeFromNext,
                    mul = ror.mul, min = ror.min };
            if (step is DensityConfig density)
                return new DensityConfig { name = density.name, enabled = density.enabled,
                    excludeFromNext = density.excludeFromNext, k = density.k,
                    threshold = density.threshold, percentile = density.percentile };
            if (step is DbscanConfig dbscan)
                return new DbscanConfig { name = dbscan.name, enabled = dbscan.enabled,
                    excludeFromNext = dbscan.excludeFromNext, eps = dbscan.eps, min = dbscan.min,
                    cluster = dbscan.cluster, target = dbscan.target, timeout = dbscan.timeout };
            if (step is ComponentKeepConfig component)
                return new ComponentKeepConfig { name = component.name, enabled = component.enabled,
                    excludeFromNext = component.excludeFromNext, knnK = component.knnK,
                    alpha = component.alpha, mode = component.mode };
            return new FilterStepConfig { name = step.name, enabled = step.enabled, excludeFromNext = step.excludeFromNext };
        }
    }
}
