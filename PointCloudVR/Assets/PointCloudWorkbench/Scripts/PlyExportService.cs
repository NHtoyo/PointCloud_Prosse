using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PointCloudWorkbench
{
    public enum ExportPointMode
    {
        AllVisible,
        SelectedVisible,
        SelectedNonDeleted,
        CleanedVisible
    }

    public sealed class PlyExportRequest
    {
        public PointData[] Points { get; }
        public string OutputPath { get; }
        public bool AsBinary { get; }
        public bool IsCalibrated { get; }
        public ExportPointMode Mode { get; }

        public PlyExportRequest(PointData[] points, string outputPath, bool asBinary, bool isCalibrated, ExportPointMode mode)
        {
            Points = points ?? throw new ArgumentNullException(nameof(points));
            OutputPath = string.IsNullOrWhiteSpace(outputPath) ? throw new ArgumentException("PLY出力先がありません。", nameof(outputPath)) : outputPath;
            AsBinary = asBinary;
            IsCalibrated = isCalibrated;
            Mode = mode;
        }
    }

    public sealed class PlyExportResult
    {
        public string OutputPath { get; }
        public int VertexCount { get; }

        internal PlyExportResult(string outputPath, int vertexCount)
        {
            OutputPath = outputPath;
            VertexCount = vertexCount;
        }
    }

    public static class PlyExportService
    {
        private const int DeletedBit = 0x20000;
        private const int SelectedBit = 0x10000;
        private const int NoiseHiddenBit = 0x80000;
        private const int CancellationCheckInterval = 4096;

        private struct ExportPoint
        {
            public float X;
            public float Y;
            public float Z;
            public uint Color;
            public int Label;

            public ExportPoint(PointData point)
            {
                X = point.position.x;
                Y = point.position.y;
                Z = point.position.z;
                Color = point.originalColor;
                Label = point.label;
            }
        }

        public static PlyExportResult Write(PlyExportRequest request, CancellationToken cancellationToken,
            Action<float, string> reportProgress = null)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            PointData[] sourcePoints = request.Points;
            if (sourcePoints.Length == 0) throw new InvalidOperationException("エクスポートする点群がありません。");

            string outputPath = Path.GetFullPath(request.OutputPath);
            string directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(directory)) throw new IOException("PLY出力先フォルダがありません。");
            Directory.CreateDirectory(directory);
            string temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            ExportPoint[] points = CaptureSnapshot(sourcePoints, request.Mode, cancellationToken);
            int vertexCount = points.Length;
            if ((request.Mode == ExportPointMode.SelectedVisible ||
                 request.Mode == ExportPointMode.SelectedNonDeleted) && vertexCount == 0)
                throw new InvalidOperationException("エクスポート対象の選択された点がありません。");

            try
            {
                if (request.AsBinary)
                    WriteBinary(request, points, temporaryPath, cancellationToken, reportProgress);
                else
                    WriteAscii(request, points, temporaryPath, cancellationToken, reportProgress);

                cancellationToken.ThrowIfCancellationRequested();
                Commit(temporaryPath, outputPath);
                return new PlyExportResult(outputPath, vertexCount);
            }
            catch (Exception writeException)
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException("PLY書き出しに失敗し、一時ファイルも削除できませんでした。", writeException, cleanupException);
                }
                throw;
            }
        }

        public static int CountIncludedPoints(PointData[] points, ExportPointMode mode,
            CancellationToken token = default)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            int count = 0;
            for (int i = 0; i < points.Length; i++)
            {
                if ((i & (CancellationCheckInterval - 1)) == 0) token.ThrowIfCancellationRequested();
                if (IsIncluded(points[i].label, mode)) count++;
            }
            return count;
        }

        private static ExportPoint[] CaptureSnapshot(PointData[] source, ExportPointMode mode, CancellationToken token)
        {
            int count = CountIncludedPoints(source, mode, token);
            ExportPoint[] snapshot = new ExportPoint[count];
            int written = 0;
            for (int i = 0; i < source.Length; i++)
            {
                if ((i & (CancellationCheckInterval - 1)) == 0) token.ThrowIfCancellationRequested();
                PointData point = source[i];
                if (!IsIncluded(point.label, mode)) continue;
                if (written >= snapshot.Length)
                    throw new InvalidOperationException("点群がエクスポート準備中に変更されました。処理をやり直してください。");
                snapshot[written++] = new ExportPoint(point);
            }
            if (written != snapshot.Length)
                throw new InvalidOperationException("点群がエクスポート準備中に変更されました。処理をやり直してください。");
            return snapshot;
        }

        private static void WriteBinary(PlyExportRequest request, ExportPoint[] points, string path, CancellationToken token,
            Action<float, string> reportProgress)
        {
            int count = points.Length;
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.ASCII))
            {
                string header = "ply\nformat binary_little_endian 1.0\n" +
                                CalibrationComment(request.IsCalibrated) +
                                $"element vertex {count}\n" +
                                "property float x\nproperty float y\nproperty float z\n" +
                                "property uchar red\nproperty uchar green\nproperty uchar blue\nproperty int label\nend_header\n";
                writer.Write(Encoding.ASCII.GetBytes(header));
                int written = 0;
                int progressInterval = Math.Max(1024, count / 100);
                for (int i = 0; i < points.Length; i++)
                {
                    if ((i & (CancellationCheckInterval - 1)) == 0) token.ThrowIfCancellationRequested();
                WriteBinaryPoint(writer, points[i]);
                    written++;
                    if (written % progressInterval == 0)
                        reportProgress?.Invoke(count == 0 ? 1f : (float)written / count, $"データを書き出し中... ({written:N0} / {count:N0} 点)");
                }
                if (written != count)
                    throw new InvalidDataException("PLYヘッダー点数と書き込み点数が一致しません。");
                writer.Flush();
                stream.Flush(true);
            }
        }

        private static void WriteAscii(PlyExportRequest request, ExportPoint[] points, string path, CancellationToken token,
            Action<float, string> reportProgress)
        {
            int count = points.Length;
            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            using (StreamWriter writer = new StreamWriter(stream, Encoding.ASCII, 1024 * 1024, true))
            {
                writer.Write("ply\nformat ascii 1.0\n");
                writer.Write(CalibrationComment(request.IsCalibrated));
                writer.Write($"element vertex {count}\nproperty float x\nproperty float y\nproperty float z\n");
                writer.Write("property uchar red\nproperty uchar green\nproperty uchar blue\nproperty int label\nend_header\n");
                int written = 0;
                int progressInterval = Math.Max(1024, count / 100);
                for (int i = 0; i < points.Length; i++)
                {
                    if ((i & (CancellationCheckInterval - 1)) == 0) token.ThrowIfCancellationRequested();
                WriteAsciiPoint(writer, points[i]);
                    written++;
                    if (written % progressInterval == 0)
                        reportProgress?.Invoke(count == 0 ? 1f : (float)written / count, $"データを書き出し中... ({written:N0} / {count:N0} 点)");
                }
                if (written != count)
                    throw new InvalidDataException("PLYヘッダー点数と書き込み点数が一致しません。");
                writer.Flush();
                stream.Flush(true);
            }
        }

        private static void WriteBinaryPoint(BinaryWriter writer, ExportPoint point)
        {
            uint color = point.Color;
            writer.Write(point.X);
            writer.Write(point.Y);
            writer.Write(point.Z);
            writer.Write((byte)(color & 0xff));
            writer.Write((byte)((color >> 8) & 0xff));
            writer.Write((byte)((color >> 16) & 0xff));
            writer.Write(point.Label & 0xff);
        }

        private static void WriteAsciiPoint(StreamWriter writer, ExportPoint point)
        {
            uint color = point.Color;
            writer.Write(point.X.ToString("R", CultureInfo.InvariantCulture)); writer.Write(' ');
            writer.Write(point.Y.ToString("R", CultureInfo.InvariantCulture)); writer.Write(' ');
            writer.Write(point.Z.ToString("R", CultureInfo.InvariantCulture)); writer.Write(' ');
            writer.Write((color & 0xff).ToString(CultureInfo.InvariantCulture)); writer.Write(' ');
            writer.Write(((color >> 8) & 0xff).ToString(CultureInfo.InvariantCulture)); writer.Write(' ');
            writer.Write(((color >> 16) & 0xff).ToString(CultureInfo.InvariantCulture)); writer.Write(' ');
            writer.WriteLine((point.Label & 0xff).ToString(CultureInfo.InvariantCulture));
        }

        public static bool IsIncluded(int label, ExportPointMode mode)
        {
            if (mode == ExportPointMode.SelectedNonDeleted)
            {
                return (label & SelectedBit) != 0 && (label & (DeletedBit | NoiseHiddenBit)) == 0;
            }
            if ((label & (DeletedBit | NoiseHiddenBit)) != 0) return false;
            return mode != ExportPointMode.SelectedVisible || (label & SelectedBit) != 0;
        }

        private static string CalibrationComment(bool calibrated)
        {
            return calibrated ? "comment pcwb_scale_calibrated true\n" : string.Empty;
        }

        private static void Commit(string temporaryPath, string outputPath)
        {
            if (File.Exists(outputPath)) File.Replace(temporaryPath, outputPath, null);
            else File.Move(temporaryPath, outputPath);
        }
    }
}
