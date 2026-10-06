using System;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;

namespace PointCloudWorkbench
{
    public static class PointCloudScaleService
    {
        private const string CalibratedSuffix = "_calibrated_mm";

        public static bool TryCalculateCoordinateCorrectionFromMillimeters(float realDiameterMm, string measuredLengthsMm, out float correctionFactor)
        {
            correctionFactor = 0f;
            if (float.IsNaN(realDiameterMm) || float.IsInfinity(realDiameterMm) || realDiameterMm <= 0f ||
                string.IsNullOrWhiteSpace(measuredLengthsMm)) return false;
            string[] values = measuredLengthsMm.Split(',');
            float[] lengths = new float[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                if (!float.TryParse(values[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float length) ||
                    float.IsNaN(length) || float.IsInfinity(length) || length <= 0f) return false;
                lengths[i] = length;
            }

            Array.Sort(lengths);
            double medianLength = lengths.Length % 2 == 0
                ? ((double)lengths[lengths.Length / 2 - 1] + lengths[lengths.Length / 2]) * 0.5
                : lengths[lengths.Length / 2];
            return TryCalculateCoordinateCorrectionFromMillimeters(realDiameterMm, medianLength, out correctionFactor);
        }

        public static bool TryCalculateCoordinateCorrectionFromMillimeters(float realDiameterMm, float measuredLengthMm, out float correctionFactor)
        {
            return TryCalculateCoordinateCorrectionFromMillimeters(realDiameterMm, (double)measuredLengthMm, out correctionFactor);
        }

        private static bool TryCalculateCoordinateCorrectionFromMillimeters(double realLengthMm, double measuredLengthMm, out float correctionFactor)
        {
            correctionFactor = 0f;
            if (double.IsNaN(realLengthMm) || double.IsInfinity(realLengthMm) || realLengthMm <= 0.0 ||
                double.IsNaN(measuredLengthMm) || double.IsInfinity(measuredLengthMm) || measuredLengthMm <= 0.0)
                return false;

            double factor = realLengthMm / measuredLengthMm;
            if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0.0 || factor > float.MaxValue) return false;
            correctionFactor = (float)factor;
            return true;
        }

        public static bool ApplyPointCoordinateCorrection(PointCloudRenderer renderer, float correctionFactor)
        {
            return renderer != null && renderer.ApplyPointCoordinateCorrection(correctionFactor);
        }

        public static bool IsCalibratedPointCloud(string pointCloudPath)
        {
            if (string.IsNullOrWhiteSpace(pointCloudPath)) return false;
            string name = Path.GetFileNameWithoutExtension(pointCloudPath);
            foreach (string marker in new[] { "_calibrated_mm", "_calibrated_m", "_calibrated" })
            {
                if (HasMarkerSuffix(name, marker)) return true;
            }
            return false;
        }

        private static bool HasMarkerSuffix(string name, string marker)
        {
            int index = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            string suffix = name.Substring(index + marker.Length);
            return suffix.Length == 0 || suffix.StartsWith("_");
        }

        public static string BuildCalibratedOutputPath(string sourcePath, string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new System.ArgumentException("Source PLY path is required.", nameof(sourcePath));
            if (string.IsNullOrWhiteSpace(outputDirectory)) outputDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));

            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            foreach (string marker in new[] { "_calibrated_mm", "_calibrated_m", "_calibrated" })
            {
                int suffixIndex = baseName.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (suffixIndex < 0) continue;
                string trailing = baseName.Substring(suffixIndex + marker.Length);
                if (trailing.Length == 0 || (trailing.StartsWith("_") && int.TryParse(trailing.Substring(1), out _)))
                {
                    baseName = baseName.Substring(0, suffixIndex);
                    break;
                }
            }

            Directory.CreateDirectory(outputDirectory);
            string firstPath = Path.Combine(outputDirectory, baseName + CalibratedSuffix + ".ply");
            if (IsAvailableOutputPath(firstPath)) return firstPath;

            for (int version = 2; ; version++)
            {
                string candidate = Path.Combine(outputDirectory, $"{baseName}{CalibratedSuffix}_{version}.ply");
                if (IsAvailableOutputPath(candidate)) return candidate;
            }
        }

        private static bool IsAvailableOutputPath(string path)
        {
            return !File.Exists(path) && !File.Exists(path + ".pcwb.json");
        }

        public static void WriteCalibratedPlyAtomic(PointData[] points, string outputPath, CancellationToken cancellationToken)
        {
            if (points == null || points.Length == 0) throw new System.ArgumentException("No point data to write.", nameof(points));
            if (string.IsNullOrWhiteSpace(outputPath)) throw new System.ArgumentException("Output path is required.", nameof(outputPath));

            string fullPath = Path.GetFullPath(outputPath);
            string directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);
            string temporaryPath = fullPath + "." + System.Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    string header = "ply\n" +
                                    "format binary_little_endian 1.0\n" +
                                    "comment pcwb_scale_calibrated true\n" +
                                    $"element vertex {points.Length}\n" +
                                    "property float x\n" +
                                    "property float y\n" +
                                    "property float z\n" +
                                    "property uchar red\n" +
                                    "property uchar green\n" +
                                    "property uchar blue\n" +
                                    "property int label\n" +
                                    "end_header\n";
                    writer.Write(System.Text.Encoding.ASCII.GetBytes(header));

                    for (int i = 0; i < points.Length; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        PointData point = points[i];
                        Color32 color = PointData.UnpackColor(point.originalColor);
                        writer.Write(point.position.x);
                        writer.Write(point.position.y);
                        writer.Write(point.position.z);
                        writer.Write(color.r);
                        writer.Write(color.g);
                        writer.Write(color.b);
                        writer.Write(point.label);
                    }
                    writer.Flush();
                    stream.Flush(true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, fullPath);
            }
            catch
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                throw;
            }
        }

    }
}
