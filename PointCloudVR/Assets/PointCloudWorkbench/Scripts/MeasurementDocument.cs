using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace PointCloudWorkbench
{
    [Serializable]
    public sealed class MeasurementDocument
    {
        public int schemaVersion = 1;
        public string coordinateSpace = "pointcloud_local";
        public float pointCoordinateScale = 1f;
        public string cloudId;
        public string sourceFileName;
        public string sourceSha256;
        public string savedUtc;
        public List<MeasurementRecord> measurements = new List<MeasurementRecord>();
    }

    [Serializable]
    public sealed class MeasurementRecord
    {
        public string id;
        public string name;
        public int mode;
        public string interpolation = MeasurementPath.CurveAlgorithmId;
        public List<Vector3> points = new List<Vector3>();
        public bool visible = true;
        public Color color = Color.yellow;
        public string createdUtc;
        public string modifiedUtc;
    }

    public static class MeasurementDocumentStore
    {
        public const int CurrentSchemaVersion = 1;
        public const string SidecarSuffix = ".pcwb.json";

        public static string GetSidecarPath(string pointCloudPath)
        {
            return Path.GetFullPath(pointCloudPath) + SidecarSuffix;
        }

        public static MeasurementDocument Create(string pointCloudPath)
        {
            return new MeasurementDocument
            {
                schemaVersion = CurrentSchemaVersion,
                coordinateSpace = "pointcloud_local",
                cloudId = Guid.NewGuid().ToString("N"),
                sourceFileName = Path.GetFileName(pointCloudPath),
                sourceSha256 = string.Empty,
                savedUtc = DateTime.UtcNow.ToString("o")
            };
        }

        public static MeasurementDocument LoadOrCreate(string pointCloudPath, out bool existed)
        {
            string sidecarPath = GetSidecarPath(pointCloudPath);
            existed = File.Exists(sidecarPath);
            if (!existed) return Create(pointCloudPath);

            string json = File.ReadAllText(sidecarPath, Encoding.UTF8);
            MeasurementDocument document = JsonUtility.FromJson<MeasurementDocument>(json);
            if (document == null)
            {
                throw new InvalidDataException("計測JSONを読み取れませんでした。");
            }
            if (document.schemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException($"未対応の計測JSONバージョンです: {document.schemaVersion}");
            }
            if (string.IsNullOrEmpty(document.coordinateSpace)) document.coordinateSpace = "pointcloud_local";
            if (document.coordinateSpace != "pointcloud_local")
            {
                throw new InvalidDataException($"未対応の計測座標系です: {document.coordinateSpace}");
            }
            if (string.IsNullOrEmpty(document.cloudId)) document.cloudId = Guid.NewGuid().ToString("N");
            if (document.measurements == null) document.measurements = new List<MeasurementRecord>();
            for (int i = document.measurements.Count - 1; i >= 0; i--)
            {
                MeasurementRecord record = document.measurements[i];
                if (record == null)
                {
                    document.measurements.RemoveAt(i);
                    continue;
                }
                if (record.points == null) record.points = new List<Vector3>();
                if (string.IsNullOrEmpty(record.id)) record.id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrEmpty(record.name)) record.name = "計測";
                if (!Enum.IsDefined(typeof(PointCloudEditor.MeasurementMode), record.mode))
                {
                    throw new InvalidDataException($"計測モードが不正です: {record.mode}");
                }
                if (string.IsNullOrEmpty(record.interpolation)) record.interpolation = MeasurementPath.CurveAlgorithmId;
                if (record.mode == (int)PointCloudEditor.MeasurementMode.SmoothCurve &&
                    record.interpolation != MeasurementPath.CurveAlgorithmId)
                {
                    throw new InvalidDataException($"未対応の曲線方式です: {record.interpolation}");
                }
            }
            return document;
        }

        public static MeasurementDocument Clone(MeasurementDocument document)
        {
            if (document == null) return null;
            return JsonUtility.FromJson<MeasurementDocument>(JsonUtility.ToJson(document));
        }

        public static bool NormalizeLegacyCoordinates(MeasurementDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));

            float sourceScale = document.pointCoordinateScale;
            bool metadataChanged = float.IsNaN(sourceScale) || float.IsInfinity(sourceScale) || sourceScale <= 0f ||
                                  !Mathf.Approximately(sourceScale, 1f);
            if (float.IsNaN(sourceScale) || float.IsInfinity(sourceScale) || sourceScale <= 0f)
                sourceScale = 1f;

            if (!Mathf.Approximately(sourceScale, 1f))
            {
                float ratio = 1f / sourceScale;
                for (int i = 0; i < document.measurements.Count; i++)
                {
                    MeasurementRecord record = document.measurements[i];
                    if (record == null || record.points == null) continue;
                    for (int p = 0; p < record.points.Count; p++)
                        record.points[p] *= ratio;
                }
            }

            document.pointCoordinateScale = 1f;
            return metadataChanged;
        }

        public static string ComputeSha256(string filePath)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(stream);
                var result = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++) result.Append(digest[i].ToString("x2"));
                return result.ToString();
            }
        }

        public static void Save(string pointCloudPath, MeasurementDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            document.schemaVersion = CurrentSchemaVersion;
            document.pointCoordinateScale = 1f;
            document.sourceFileName = Path.GetFileName(pointCloudPath);
            document.savedUtc = DateTime.UtcNow.ToString("o");
            WriteAtomic(GetSidecarPath(pointCloudPath), JsonUtility.ToJson(document, true));
        }

        public static MeasurementDocument CreateDerivedDocument(MeasurementDocument source, string outputPath)
        {
            if (source == null) return null;

            MeasurementDocument derived = Clone(source);
            derived.cloudId = Guid.NewGuid().ToString("N");
            derived.sourceFileName = Path.GetFileName(outputPath);
            derived.sourceSha256 = string.Empty;
            derived.pointCoordinateScale = 1f;
            derived.savedUtc = DateTime.UtcNow.ToString("o");
            return derived;
        }

        public static void WriteDerivedSidecar(string outputPath, MeasurementDocument snapshot)
        {
            MeasurementDocument derived = CreateDerivedDocument(snapshot, outputPath);
            if (derived == null) return;
            derived.sourceSha256 = ComputeSha256(outputPath);
            Save(outputPath, derived);
        }

        private static void WriteAtomic(string path, string contents)
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) throw new IOException("計測JSONの保存先フォルダがありません。");
            Directory.CreateDirectory(directory);

            string temporaryPath = path + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporaryPath, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        string backupPath = path + "." + DateTime.UtcNow.Ticks + ".bak";
                        File.Copy(path, backupPath, false);
                        File.Copy(temporaryPath, path, true);
                        File.Delete(temporaryPath);
                    }
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
}
