using System.IO;

namespace PointCloudWorkbench
{
    public struct DownsamplePaths
    {
        public readonly string SourcePath;
        public readonly string BaseDirectory;
        public readonly string CleanBaseName;
        public readonly string TemporaryLabeledPath;
        public readonly string OutputDirectory;
        public readonly string CombinedOutputPath;

        public DownsamplePaths(
            string sourcePath,
            string baseDirectory,
            string cleanBaseName,
            string temporaryLabeledPath,
            string outputDirectory,
            string combinedOutputPath)
        {
            SourcePath = sourcePath;
            BaseDirectory = baseDirectory;
            CleanBaseName = cleanBaseName;
            TemporaryLabeledPath = temporaryLabeledPath;
            OutputDirectory = outputDirectory;
            CombinedOutputPath = combinedOutputPath;
        }
    }

    public static class PointCloudDownsampleService
    {
        /// <summary>
        /// ボクセルサイズ（mm単位）を含んだダウンサンプリングパスを構築します。
        /// </summary>
        public static DownsamplePaths BuildPaths(string inputPath, float voxelSizeMm = 0f)
        {
            string baseDirectory = GetBaseDirectory(inputPath);
            string cleanBaseName = GetCleanBaseName(inputPath);
            // 出力先は元データと同じフォルダ（サブフォルダなし）
            string outputDirectory = baseDirectory;
            string temporaryLabeledPath = Path.Combine(baseDirectory, $"{cleanBaseName}_labeled.ply");
            // ファイル名にボクセルサイズを明記（例: leaf_ds5.0mm.ply）
            string sizeTag = voxelSizeMm > 0f ? $"_ds{voxelSizeMm:0.#}mm" : "_downsampled";
            string combinedOutputPath = Path.Combine(outputDirectory, $"{cleanBaseName}{sizeTag}.ply");

            return new DownsamplePaths(
                inputPath,
                baseDirectory,
                cleanBaseName,
                temporaryLabeledPath,
                outputDirectory,
                combinedOutputPath);
        }

        public static string GetLoaderRelativePath(string downsampledPath)
        {
            // 出力先がPointCloudData直下になったため、ファイル名のみ返す
            return Path.GetFileName(downsampledPath);
        }

        private static string GetBaseDirectory(string inputPath)
        {
            string directory = Path.GetDirectoryName(inputPath);
            string folderName = Path.GetFileName(directory);
            if (folderName != null && folderName.Equals("downsample", System.StringComparison.OrdinalIgnoreCase))
            {
                directory = Path.GetDirectoryName(directory);
            }
            return directory;
        }

        private static string GetCleanBaseName(string inputPath)
        {
            string cleanBaseName = Path.GetFileNameWithoutExtension(inputPath);
            if (cleanBaseName.EndsWith("_downsampled", System.StringComparison.OrdinalIgnoreCase))
            {
                cleanBaseName = cleanBaseName.Substring(0, cleanBaseName.Length - "_downsampled".Length);
            }
            if (cleanBaseName.EndsWith("_labeled", System.StringComparison.OrdinalIgnoreCase))
            {
                cleanBaseName = cleanBaseName.Substring(0, cleanBaseName.Length - "_labeled".Length);
            }
            return cleanBaseName;
        }
    }
}
