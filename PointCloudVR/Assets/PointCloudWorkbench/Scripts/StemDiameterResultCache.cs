using System;
using System.IO;

namespace PointCloudWorkbench
{
    public static class StemDiameterResultCache
    {
        public static string GetResultDirectory(string pointCloudDataDirectory, string sourcePointCloudPath)
        {
            if (string.IsNullOrWhiteSpace(pointCloudDataDirectory))
                throw new ArgumentException("PointCloudDataフォルダが指定されていません。", nameof(pointCloudDataDirectory));
            if (string.IsNullOrWhiteSpace(sourcePointCloudPath))
                throw new ArgumentException("元点群パスが指定されていません。", nameof(sourcePointCloudPath));

            string sourceName = Path.GetFileNameWithoutExtension(sourcePointCloudPath);
            return Path.Combine(pointCloudDataDirectory, sourceName + "_stem_diameter");
        }

        public static string GetResultJsonPath(string pointCloudDataDirectory, string sourcePointCloudPath)
        {
            return Path.Combine(GetResultDirectory(pointCloudDataDirectory, sourcePointCloudPath), "stem_diameter.json");
        }

        public static bool SourceMatches(string resultSourcePath, string resultSourceFilename, string currentSourcePath)
        {
            bool hasSourcePath = !string.IsNullOrWhiteSpace(resultSourcePath);
            bool hasSourceFilename = !string.IsNullOrWhiteSpace(resultSourceFilename);
            if (!hasSourcePath && !hasSourceFilename) return true;
            if (string.IsNullOrWhiteSpace(currentSourcePath)) return false;

            if (hasSourceFilename && !string.Equals(
                    resultSourceFilename, Path.GetFileName(currentSourcePath), StringComparison.OrdinalIgnoreCase))
                return false;
            return !hasSourcePath || PathsEqual(resultSourcePath, currentSourcePath);
        }

        public static bool PathsEqual(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        public static bool IsStale(int analysisVisiblePointCount, int currentVisiblePointCount)
        {
            return analysisVisiblePointCount > 0 && currentVisiblePointCount >= 0 &&
                   analysisVisiblePointCount != currentVisiblePointCount;
        }
    }
}
