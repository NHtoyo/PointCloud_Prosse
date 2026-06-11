using System.Globalization;
using System.IO;
using UnityEngine;

namespace PointCloudWorkbench
{
    public static class PointCloudScaleService
    {
        public const string DefaultReportRelativePath = "../config/scale_calibration_report.json";

        public static string GetDefaultReportPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, DefaultReportRelativePath));
        }

        public static float LoadMetersPerUnitOrDefault(string reportPath, float defaultMetersPerUnit = 1.0f)
        {
            if (!TryLoadMetersPerUnit(reportPath, out float metersPerUnit))
            {
                return defaultMetersPerUnit;
            }
            return metersPerUnit;
        }

        public static bool TryLoadMetersPerUnit(string reportPath, out float metersPerUnit)
        {
            metersPerUnit = 1.0f;
            if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath)) return false;

            try
            {
                string jsonText = File.ReadAllText(reportPath);
                if (!TryReadFloatValue(jsonText, "\"scale_mm_per_unit\":", out float mmPerUnit) &&
                    !TryReadFloatValue(jsonText, "\"mm_per_unit\":", out mmPerUnit))
                {
                    return false;
                }

                metersPerUnit = mmPerUnit / 1000f;
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ScaleService] Failed to load scale report: {ex.Message}");
                return false;
            }
        }

        public static void ApplyUniformScale(PointCloudRenderer renderer, float metersPerUnit)
        {
            if (renderer == null) return;

            Vector3 targetScale = Vector3.one * metersPerUnit;
            renderer.transform.localScale = targetScale;

            PointCloudController controller = renderer.GetComponent<PointCloudController>();
            if (controller != null)
            {
                controller.ResetInitialScale(targetScale);
            }
        }

        private static bool TryReadFloatValue(string text, string key, out float value)
        {
            value = 0f;
            int idx = text.IndexOf(key, System.StringComparison.Ordinal);
            if (idx < 0) return false;

            int start = idx + key.Length;
            int end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;

            string raw = text.Substring(start, end - start).Replace(",", "").Trim();
            return float.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }
    }
}
