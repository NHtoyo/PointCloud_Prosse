using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace PointCloudWorkbench
{
    [Serializable]
    public sealed class OutputGenerationPointer
    {
        public int schema_version;
        public string status;
        public string run_id;
        public string manifest;
        public string manifest_sha256;
    }

    [Serializable]
    public sealed class OutputGenerationManifest
    {
        public int schema_version;
        public string status;
        public string run_id;
        public OutputGenerationArtifact[] artifacts;
    }

    [Serializable]
    public sealed class OutputGenerationArtifact
    {
        public string name;
        public long size_bytes;
        public string sha256;
    }

    public static class OutputGenerationStore
    {
        public static string ResolveCurrentGeneration(string outputRoot)
        {
            string root = Path.GetFullPath(outputRoot);
            string pointerPath = Path.Combine(root, "current_run.json");
            if (!File.Exists(pointerPath)) throw new FileNotFoundException("解析結果の世代ポインタがありません。", pointerPath);
            OutputGenerationPointer pointer = JsonUtility.FromJson<OutputGenerationPointer>(File.ReadAllText(pointerPath, Encoding.UTF8));
            if (pointer == null || pointer.schema_version != 1 || pointer.status != "complete" ||
                !IsSafeRunId(pointer.run_id) || pointer.manifest != $"runs/{pointer.run_id}/manifest.json")
                throw new InvalidDataException("解析結果の世代ポインタが不正です。");
            string generationDirectory = Path.Combine(root, "runs", pointer.run_id);
            ValidateGeneration(generationDirectory, pointer.run_id, pointer.manifest_sha256);
            return generationDirectory;
        }

        public static string ResolveGeneration(string outputRoot, string runId)
        {
            if (!IsSafeRunId(runId)) throw new InvalidDataException("解析実行IDが不正です。");
            string generationDirectory = Path.Combine(Path.GetFullPath(outputRoot), "runs", runId);
            ValidateGeneration(generationDirectory, runId, null);
            return generationDirectory;
        }

        private static void ValidateGeneration(string generationDirectory, string expectedRunId, string expectedManifestHash)
        {
            string manifestPath = Path.Combine(generationDirectory, "manifest.json");
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("解析世代の完了マニフェストがありません。", manifestPath);
            if (!string.IsNullOrEmpty(expectedManifestHash) &&
                !string.Equals(ComputeSha256(manifestPath), expectedManifestHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("解析世代マニフェストのチェックサムが一致しません。");

            OutputGenerationManifest manifest = JsonUtility.FromJson<OutputGenerationManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (manifest == null || manifest.schema_version != 1 || manifest.status != "complete" ||
                !string.Equals(manifest.run_id, expectedRunId, StringComparison.Ordinal) || manifest.artifacts == null)
                throw new InvalidDataException("解析世代マニフェストが不正です。");

            for (int i = 0; i < manifest.artifacts.Length; i++)
            {
                OutputGenerationArtifact artifact = manifest.artifacts[i];
                if (artifact == null || string.IsNullOrWhiteSpace(artifact.name) ||
                    !string.Equals(Path.GetFileName(artifact.name), artifact.name, StringComparison.Ordinal) ||
                    artifact.size_bytes < 0 || string.IsNullOrWhiteSpace(artifact.sha256))
                    throw new InvalidDataException("解析世代に不正なファイル項目があります。");
                string path = Path.Combine(generationDirectory, artifact.name);
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length != artifact.size_bytes)
                    throw new InvalidDataException($"解析結果ファイルが欠落またはサイズ不一致です: {artifact.name}");
                if (!string.Equals(ComputeSha256(path), artifact.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"解析結果ファイルのチェックサムが一致しません: {artifact.name}");
            }
        }

        private static string ComputeSha256(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.SequentialScan))
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static bool IsSafeRunId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 80) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                    !(c >= '0' && c <= '9') && c != '_' && c != '-') return false;
            }
            return true;
        }
    }
}
