using System;
using System.Globalization;
using System.IO;

namespace PointCloudWorkbench
{
    public struct DownsamplePaths
    {
        public readonly string SourcePath;
        public readonly string BaseDirectory;
        public readonly string CleanBaseName;
        public readonly string RunId;
        public readonly string WorkDirectory;
        public readonly string TemporaryLabeledPath;
        public readonly string OutputDirectory;
        public readonly string StagedOutputPath;
        public readonly string CombinedOutputPath;

        public DownsamplePaths(string sourcePath, string baseDirectory, string cleanBaseName, string runId,
            string workDirectory, string temporaryLabeledPath, string outputDirectory, string stagedOutputPath,
            string combinedOutputPath)
        {
            SourcePath = sourcePath;
            BaseDirectory = baseDirectory;
            CleanBaseName = cleanBaseName;
            RunId = runId;
            WorkDirectory = workDirectory;
            TemporaryLabeledPath = temporaryLabeledPath;
            OutputDirectory = outputDirectory;
            StagedOutputPath = stagedOutputPath;
            CombinedOutputPath = combinedOutputPath;
        }
    }

    public static class PointCloudDownsampleService
    {
        public static DownsamplePaths BuildPaths(string inputPath, float voxelSizeMm = 0f)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) throw new ArgumentException("入力PLYのパスがありません。", nameof(inputPath));
            if (float.IsNaN(voxelSizeMm) || float.IsInfinity(voxelSizeMm) || voxelSizeMm < 0f)
                throw new ArgumentOutOfRangeException(nameof(voxelSizeMm));

            string sourcePath = Path.GetFullPath(inputPath);
            string baseDirectory = Path.GetDirectoryName(sourcePath);
            string cleanBaseName = GetCleanBaseName(sourcePath);
            string runId = Guid.NewGuid().ToString("N");
            string workDirectory = Path.Combine(baseDirectory, ".pcwb-downsample", runId);
            string temporaryLabeledPath = Path.Combine(workDirectory, "input_labeled.ply");
            string stagedOutputPath = Path.Combine(workDirectory, "downsampled.ply");
            string outputDirectory = workDirectory;
            string suffix = voxelSizeMm > 0f
                ? "_ds" + ToFileToken(voxelSizeMm.ToString("R", CultureInfo.InvariantCulture)) + "mm"
                : "_downsampled";
            string combinedOutputPath = Path.Combine(baseDirectory,
                cleanBaseName + suffix + "_" + runId + ".ply");

            return new DownsamplePaths(sourcePath, baseDirectory, cleanBaseName, runId, workDirectory,
                temporaryLabeledPath, outputDirectory, stagedOutputPath, combinedOutputPath);
        }

        public static string GetLoaderRelativePath(string downsampledPath)
        {
            return Path.GetFileName(downsampledPath);
        }

        public static void CleanupWorkDirectory(string workDirectory)
        {
            if (string.IsNullOrWhiteSpace(workDirectory)) return;
            string fullPath = Path.GetFullPath(workDirectory);
            DirectoryInfo work = new DirectoryInfo(fullPath);
            DirectoryInfo parent = work.Parent;
            if (parent == null || !string.Equals(parent.Name, ".pcwb-downsample", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(work.Name, "N", out _))
                throw new InvalidOperationException("生成元を確認できないため、ダウンサンプリング作業フォルダを削除しません。");
            if (!Directory.Exists(fullPath)) return;
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("再解析ポイントの作業フォルダは安全のため削除しません。");
            Directory.Delete(fullPath, true);
        }

        private static string GetCleanBaseName(string inputPath)
        {
            string cleanBaseName = Path.GetFileNameWithoutExtension(inputPath);
            if (cleanBaseName.EndsWith("_downsampled", StringComparison.OrdinalIgnoreCase))
                cleanBaseName = cleanBaseName.Substring(0, cleanBaseName.Length - "_downsampled".Length);
            if (cleanBaseName.EndsWith("_labeled", StringComparison.OrdinalIgnoreCase))
                cleanBaseName = cleanBaseName.Substring(0, cleanBaseName.Length - "_labeled".Length);
            return cleanBaseName;
        }

        private static string ToFileToken(string value)
        {
            return value.Replace("-", "m").Replace("+", "p").Replace(".", "p");
        }
    }
}
