using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PointCloudWorkbench
{
    [System.Serializable]
    public class NoiseFilterPresetData
    {
        public string processMode;
        public float voxelSize;
        public List<string> pipelineTypes = new List<string>();
        public List<string> pipelineJsons = new List<string>();
    }

    public static class NoiseFilterPresetManager
    {
        private static string presetDir => Path.Combine(Application.persistentDataPath, "Presets", "NoiseFilters");

        public static void Init()
        {
            if (!Directory.Exists(presetDir))
            {
                Directory.CreateDirectory(presetDir);
            }
        }

        public static void SavePreset(string presetName, NoiseFilterParams parameters)
        {
            Init();
            string path = Path.Combine(presetDir, presetName + ".json");
            
            var data = new NoiseFilterPresetData();
            data.processMode = parameters.processMode;
            data.voxelSize = parameters.voxelSize;

            var pipeline = parameters.customPipeline;
            if (pipeline == null || pipeline.Count == 0)
            {
                pipeline = parameters.GetPipeline();
            }

            foreach (var step in pipeline)
            {
                data.pipelineTypes.Add(step.name);
                data.pipelineJsons.Add(JsonUtility.ToJson(step));
            }

            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(path, json);
            Debug.Log($"[NoiseFilterPresetManager] Saved preset to: {path}");
        }

        public static void LoadPreset(string presetName, NoiseFilterParams currentParams)
        {
            Init();
            string path = Path.Combine(presetDir, presetName + ".json");
            if (!File.Exists(path)) return;

            string json = File.ReadAllText(path);
            var data = JsonUtility.FromJson<NoiseFilterPresetData>(json);
            Debug.Log($"[NoiseFilterPresetManager] Loaded preset: {presetName}");

            currentParams.processMode = data.processMode;
            currentParams.voxelSize = data.voxelSize;
            currentParams.customPipeline.Clear();

            bool wh = false, cc = false, sor = false, ror = false, den = false, dbs = false;

            for (int i = 0; i < data.pipelineTypes.Count; i++)
            {
                string type = data.pipelineTypes[i];
                string stepJson = data.pipelineJsons[i];
                FilterStepConfig step = null;

                switch (type)
                {
                    case "white_haze": step = wh ? new WhiteHazeConfig() : currentParams.whiteHaze; wh = true; break;
                    case "cc_noise": step = cc ? new CcConfig() : currentParams.cc; cc = true; break;
                    case "sor": step = sor ? new SorConfig() : currentParams.sor; sor = true; break;
                    case "ror": step = ror ? new RorConfig() : currentParams.ror; ror = true; break;
                    case "density": step = den ? new DensityConfig() : currentParams.density; den = true; break;
                    case "dbscan": step = dbs ? new DbscanConfig() : currentParams.dbscan; dbs = true; break;
                    default: step = new FilterStepConfig(); break;
                }

                if (step != null)
                {
                    JsonUtility.FromJsonOverwrite(stepJson, step);
                    currentParams.customPipeline.Add(step);
                }
            }
        }

        public static List<string> GetPresetNames()
        {
            Init();
            if (!Directory.Exists(presetDir)) return new List<string>();
            var files = Directory.GetFiles(presetDir, "*.json");
            return files.Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n).ToList();
        }

        public static void DeletePreset(string presetName)
        {
            Init();
            string path = Path.Combine(presetDir, presetName + ".json");
            if (File.Exists(path))
            {
                File.Delete(path);
                Debug.Log($"[NoiseFilterPresetManager] Deleted preset: {presetName}");
            }
        }
    }
}
