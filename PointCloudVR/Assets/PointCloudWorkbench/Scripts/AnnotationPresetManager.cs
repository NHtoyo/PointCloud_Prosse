using System.Collections.Generic;
using UnityEngine;
using System.IO;

namespace PointCloudWorkbench
{
    [System.Serializable]
    public class AnnotationClassData
    {
        public int id;
        public string name;
        public string colorHex;

        public Color GetColor()
        {
            Color color;
            if (ColorUtility.TryParseHtmlString(colorHex, out color))
            {
                return color;
            }
            return Color.grey;
        }

        public void SetColor(Color color)
        {
            colorHex = "#" + ColorUtility.ToHtmlStringRGB(color);
        }
    }

    [System.Serializable]
    public class AnnotationPresetData
    {
        public string presetName;
        public List<AnnotationClassData> classes = new List<AnnotationClassData>();
    }

    [System.Serializable]
    public class AnnotationPresetListWrapper
    {
        public List<AnnotationPresetData> presets = new List<AnnotationPresetData>();
    }

    public static class AnnotationPresetManager
    {
        public static readonly Color[] ColorPalette = new Color[]
        {
            new Color(0.9f, 0.1f, 0.1f, 1f), // Red
            new Color(0.1f, 0.7f, 0.2f, 1f), // Green
            new Color(0.0f, 0.6f, 0.9f, 1f), // Blue
            new Color(1.0f, 0.9f, 0.0f, 1f), // Yellow
            new Color(0.9f, 0.0f, 0.9f, 1f), // Magenta
            new Color(0.0f, 0.8f, 0.8f, 1f), // Cyan
            new Color(1.0f, 0.5f, 0.0f, 1f), // Orange
            new Color(0.5f, 0.0f, 0.5f, 1f), // Purple
            new Color(0.5f, 0.5f, 0.0f, 1f), // Olive
            new Color(0.0f, 0.5f, 0.5f, 1f), // Teal
            new Color(0.8f, 0.4f, 0.0f, 1f), // Brown
            new Color(0.6f, 0.8f, 0.2f, 1f), // Lime Green
            new Color(0.2f, 0.2f, 0.6f, 1f), // Navy
            new Color(0.9f, 0.5f, 0.5f, 1f), // Pink
            new Color(0.5f, 0.9f, 0.5f, 1f), // Mint Green
            new Color(0.5f, 0.5f, 0.9f, 1f), // Light Blue
        };

        private static string GetSavePath()
        {
            return Path.Combine(Application.persistentDataPath, "annotation_presets.json");
        }

        public static AnnotationPresetListWrapper LoadPresets()
        {
            string path = GetSavePath();
            if (!File.Exists(path))
            {
                var wrapper = new AnnotationPresetListWrapper();
                wrapper.presets.Add(CreateDefaultPreset());
                SavePresets(wrapper);
                return wrapper;
            }

            try
            {
                string json = File.ReadAllText(path);
                var wrapper = JsonUtility.FromJson<AnnotationPresetListWrapper>(json);
                if (wrapper == null || wrapper.presets == null)
                {
                    wrapper = new AnnotationPresetListWrapper { presets = new List<AnnotationPresetData>() };
                }
                if (wrapper.presets.Count == 0)
                {
                    wrapper.presets.Add(CreateDefaultPreset());
                    SavePresets(wrapper);
                }
                return wrapper;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[AnnotationPresetManager] Failed to load presets: {ex.Message}");
                var wrapper = new AnnotationPresetListWrapper();
                wrapper.presets.Add(CreateDefaultPreset());
                return wrapper;
            }
        }

        public static void SavePresets(AnnotationPresetListWrapper wrapper)
        {
            try
            {
                string json = JsonUtility.ToJson(wrapper, true);
                File.WriteAllText(GetSavePath(), json);
                Debug.Log($"[AnnotationPresetManager] Saved presets to {GetSavePath()}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[AnnotationPresetManager] Failed to save presets: {ex.Message}");
            }
        }

        public static AnnotationPresetData CreateDefaultPreset()
        {
            var preset = new AnnotationPresetData { presetName = "Default (Plant)" };
            preset.classes.Add(new AnnotationClassData { id = 0, name = "未分類", colorHex = "#B3B3B3" }); // 0.7, 0.7, 0.7
            preset.classes.Add(new AnnotationClassData { id = 1, name = "茎", colorHex = "#8C5926" }); // 0.55, 0.35, 0.15
            preset.classes.Add(new AnnotationClassData { id = 2, name = "葉", colorHex = "#1AB333" }); // 0.1, 0.7, 0.2
            preset.classes.Add(new AnnotationClassData { id = 3, name = "果実", colorHex = "#FF1A1A" }); // 1.0, 0.1, 0.1
            preset.classes.Add(new AnnotationClassData { id = 4, name = "花", colorHex = "#FFE600" }); // 1.0, 0.9, 0.0
            preset.classes.Add(new AnnotationClassData { id = 5, name = "支柱", colorHex = "#0099E6" }); // 0.0, 0.6, 0.9
            return preset;
        }
    }
}
