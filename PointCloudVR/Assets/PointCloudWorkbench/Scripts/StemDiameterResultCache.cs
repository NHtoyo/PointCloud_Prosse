using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

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
            string sourceKey = ComputeSourceKey(sourcePointCloudPath);
            return Path.Combine(pointCloudDataDirectory, sourceName + "_stem_diameter_" + sourceKey);
        }

        public static string GetLegacyResultDirectory(string pointCloudDataDirectory, string sourcePointCloudPath)
        {
            if (string.IsNullOrWhiteSpace(pointCloudDataDirectory))
                throw new ArgumentException("PointCloudDataフォルダが指定されていません。", nameof(pointCloudDataDirectory));
            if (string.IsNullOrWhiteSpace(sourcePointCloudPath))
                throw new ArgumentException("元点群パスが指定されていません。", nameof(sourcePointCloudPath));
            return Path.Combine(pointCloudDataDirectory, Path.GetFileNameWithoutExtension(sourcePointCloudPath) + "_stem_diameter");
        }

        public static string GetResultJsonPath(string pointCloudDataDirectory, string sourcePointCloudPath)
        {
            return Path.Combine(GetResultDirectory(pointCloudDataDirectory, sourcePointCloudPath), "stem_diameter.json");
        }

        public static string GetGenerationJsonPath(string resultDirectory, string runId)
        {
            if (string.IsNullOrWhiteSpace(runId) || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                runId.Contains("..") || runId.Contains("/") || runId.Contains("\\"))
                throw new ArgumentException("解析実行IDが不正です。", nameof(runId));
            return Path.Combine(resultDirectory, "runs", runId, "stem_diameter.json");
        }

        private static string ComputeSourceKey(string sourcePath)
        {
            string normalized = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                return BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        public static bool SourceMatches(string resultSourcePath, string resultSourceFilename, string currentSourcePath)
        {
            bool hasSourcePath = !string.IsNullOrWhiteSpace(resultSourcePath);
            bool hasSourceFilename = !string.IsNullOrWhiteSpace(resultSourceFilename);
            if (!hasSourcePath && !hasSourceFilename) return false;
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
            return analysisVisiblePointCount <= 0 || currentVisiblePointCount < 0 ||
                   analysisVisiblePointCount != currentVisiblePointCount;
        }

        public static bool IsStale(int analysisVisiblePointCount, int currentVisiblePointCount,
            string analysisFingerprint, string currentFingerprint)
        {
            if (analysisVisiblePointCount <= 0 || currentVisiblePointCount < 0 ||
                currentVisiblePointCount != analysisVisiblePointCount ||
                string.IsNullOrWhiteSpace(analysisFingerprint) || string.IsNullOrWhiteSpace(currentFingerprint)) return true;
            return !string.Equals(analysisFingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase);
        }
    }
}
