using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PointCloudWorkbench
{
    public sealed class StemDiameterInputExport
    {
        public const int MinimumPointCount = 100;

        private readonly PointData[] points;
        private readonly bool isCalibrated;
        private bool writeStarted;

        public int LoadedPointCount { get; }
        public int VisiblePointCount { get; private set; }
        public string TemporaryDirectory { get; }
        public string InputPath { get; }

        private StemDiameterInputExport(PointData[] points, bool isCalibrated, string temporaryDirectory)
        {
            this.points = points;
            this.isCalibrated = isCalibrated;
            LoadedPointCount = points.Length;
            TemporaryDirectory = temporaryDirectory;
            InputPath = Path.Combine(temporaryDirectory, "analysis_input.ply");
        }

        public static StemDiameterInputExport Create(PointData[] points, bool isCalibrated)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));

            string temporaryDirectory = Path.Combine(Path.GetTempPath(), "PointCloudVR",
                "stem_diameter_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            return new StemDiameterInputExport(points, isCalibrated, temporaryDirectory);
        }

        public Task<PlyExportResult> WriteAsync(CancellationToken cancellationToken,
            Action<float, string> reportProgress = null)
        {
            if (writeStarted) throw new InvalidOperationException("一時PLYは既に書き出し済みです。");
            writeStarted = true;
            PlyExportRequest request = new PlyExportRequest(points, InputPath, true, isCalibrated,
                ExportPointMode.AllVisible);

            return Task.Run(() =>
            {
                PlyExportResult result = PlyExportService.Write(request, cancellationToken, reportProgress);
                VisiblePointCount = result.VertexCount;
                return result;
            }, cancellationToken);
        }

        public void ValidateComponentK(int componentK)
        {
            ValidateMinimumPointCount();
            if (componentK < 1 || componentK >= VisiblePointCount)
            {
                throw new InvalidOperationException("近傍数Kは1以上かつ書き出した可視点数未満にしてください。");
            }
        }

        public void ValidateMinimumPointCount()
        {
            if (VisiblePointCount < MinimumPointCount)
            {
                throw new InvalidOperationException(
                    $"解析に必要な可視点が不足しています。可視点数: {VisiblePointCount} / 必要: {MinimumPointCount}");
            }
        }

        public static void ValidatePythonPointCount(int expectedPointCount, int actualPointCount)
        {
            if (expectedPointCount != actualPointCount)
            {
                throw new InvalidDataException(
                    $"一時PLYとPythonの入力点数が一致しません。Unity書き出し: {expectedPointCount} / Python読込: {actualPointCount}");
            }
        }

        public void Cleanup()
        {
            if (Directory.Exists(TemporaryDirectory)) Directory.Delete(TemporaryDirectory, true);
        }
    }
}
