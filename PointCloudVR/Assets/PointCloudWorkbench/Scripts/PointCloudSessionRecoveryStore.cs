using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PointCloudWorkbench
{
    public static class PointCloudSessionRecoveryStore
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PCWBR001");
        private static readonly object SessionLock = new object();
        private static bool sessionStarted;
        private static bool previousSessionWasUnclean;

        public static bool PreviousSessionWasUnclean
        {
            get { lock (SessionLock) return previousSessionWasUnclean; }
        }

        public static bool BeginSession(string persistentDataPath)
        {
            lock (SessionLock)
            {
                if (sessionStarted) return previousSessionWasUnclean;
                string markerPath = GetMarkerPath(persistentDataPath);
                previousSessionWasUnclean = File.Exists(markerPath);
                Directory.CreateDirectory(Path.GetDirectoryName(markerPath));
                WriteAtomicBytes(markerPath, Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O") + "\n" + Guid.NewGuid().ToString("N")));
                sessionStarted = true;
                return previousSessionWasUnclean;
            }
        }

        public static bool EndSession(string persistentDataPath)
        {
            lock (SessionLock)
            {
                if (!sessionStarted) return true;
                try
                {
                    string markerPath = GetMarkerPath(persistentDataPath);
                    if (File.Exists(markerPath)) File.Delete(markerPath);
                    sessionStarted = false;
                    previousSessionWasUnclean = false;
                    return true;
                }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
        }

        public static string GetCheckpointPath(string persistentDataPath, string sourcePath)
        {
            string normalizedPath = Path.GetFullPath(sourcePath).ToUpperInvariant();
            byte[] pathHash;
            using (SHA256 sha = SHA256.Create()) pathHash = sha.ComputeHash(Encoding.UTF8.GetBytes(normalizedPath));
            string key = BitConverter.ToString(pathHash).Replace("-", string.Empty).ToLowerInvariant();
            return Path.Combine(persistentDataPath, "PointCloudVR", "Recovery", key + ".pcwbr");
        }

        public static string ComputeFileSha256(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        public static void WriteAtomic(string path, string sourceSha256, int[] labels, long revision)
        {
            if (labels == null) throw new ArgumentNullException(nameof(labels));
            byte[] sourceHash = ParseHash(sourceSha256);
            byte[] checksum = ComputeLabelsChecksum(labels);
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                using (BinaryWriter writer = new BinaryWriter(file, Encoding.UTF8, true))
                {
                    writer.Write(Magic);
                    writer.Write(1);
                    writer.Write(labels.Length);
                    writer.Write(revision);
                    writer.Write(sourceHash);
                    writer.Write(checksum);
                    writer.Flush();
                    using (DeflateStream compressed = new DeflateStream(file, CompressionLevel.Optimal, true))
                        WriteLabels(compressed, labels);
                    file.Flush(true);
                }

                if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, null);
                else File.Move(temporaryPath, fullPath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        public static bool TryRead(string path, string expectedSourceSha256, int expectedPointCount,
            out int[] labels, out long revision, out string failureReason)
        {
            labels = null;
            revision = 0;
            failureReason = string.Empty;
            byte[] expectedHash = ParseHash(expectedSourceSha256);
            try
            {
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (BinaryReader reader = new BinaryReader(file, Encoding.UTF8, true))
                {
                    byte[] magic = reader.ReadBytes(Magic.Length);
                    if (!EqualBytes(magic, Magic)) throw new InvalidDataException("未知の復旧ファイル形式です。");
                    if (reader.ReadInt32() != 1) throw new InvalidDataException("復旧ファイルのバージョンに対応していません。");
                    int pointCount = reader.ReadInt32();
                    long savedRevision = reader.ReadInt64();
                    byte[] sourceHash = reader.ReadBytes(32);
                    byte[] checksum = reader.ReadBytes(32);
                    if (pointCount != expectedPointCount || pointCount < 0)
                        throw new InvalidDataException("復旧データの点数が現在の点群と一致しません。");
                    if (!EqualBytes(sourceHash, expectedHash))
                        throw new InvalidDataException("復旧データの元PLYチェックサムが一致しません。");

                    int[] restored = new int[pointCount];
                    byte[] buffer = new byte[65536];
                    using (DeflateStream compressed = new DeflateStream(file, CompressionMode.Decompress, true))
                    using (SHA256 sha = SHA256.Create())
                    {
                        int index = 0;
                        while (index < pointCount)
                        {
                            int count = Math.Min(pointCount - index, buffer.Length / 4);
                            int bytesNeeded = count * 4;
                            ReadExactly(compressed, buffer, bytesNeeded);
                            sha.TransformBlock(buffer, 0, bytesNeeded, buffer, 0);
                            for (int i = 0, offset = 0; i < count; i++, offset += 4)
                                restored[index + i] = buffer[offset] | buffer[offset + 1] << 8 |
                                    buffer[offset + 2] << 16 | buffer[offset + 3] << 24;
                            index += count;
                        }
                        if (compressed.ReadByte() != -1) throw new InvalidDataException("復旧データに余分な内容があります。");
                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        if (!EqualBytes(sha.Hash, checksum)) throw new InvalidDataException("復旧データのチェックサムが一致しません。");
                    }
                    labels = restored;
                    revision = savedRevision;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is CryptographicException || ex is OverflowException)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        private static string GetMarkerPath(string persistentDataPath) =>
            Path.Combine(persistentDataPath, "PointCloudVR", "Recovery", "session.active");

        private static void WriteAtomicBytes(string path, byte[] data)
        {
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static byte[] ParseHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
                throw new ArgumentException("SHA-256は64桁の16進数で指定してください。", nameof(value));
            byte[] bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static byte[] ComputeLabelsChecksum(int[] labels)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[65536];
                int index = 0;
                while (index < labels.Length)
                {
                    int count = Math.Min(labels.Length - index, buffer.Length / 4);
                    int bytes = count * 4;
                    for (int i = 0, offset = 0; i < count; i++, offset += 4)
                    {
                        int value = labels[index + i];
                        buffer[offset] = (byte)value;
                        buffer[offset + 1] = (byte)(value >> 8);
                        buffer[offset + 2] = (byte)(value >> 16);
                        buffer[offset + 3] = (byte)(value >> 24);
                    }
                    sha.TransformBlock(buffer, 0, bytes, buffer, 0);
                    index += count;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return sha.Hash;
            }
        }

        private static void WriteLabels(Stream stream, int[] labels)
        {
            byte[] buffer = new byte[65536];
            int index = 0;
            while (index < labels.Length)
            {
                int count = Math.Min(labels.Length - index, buffer.Length / 4);
                int bytes = count * 4;
                for (int i = 0, offset = 0; i < count; i++, offset += 4)
                {
                    int value = labels[index + i];
                    buffer[offset] = (byte)value;
                    buffer[offset + 1] = (byte)(value >> 8);
                    buffer[offset + 2] = (byte)(value >> 16);
                    buffer[offset + 3] = (byte)(value >> 24);
                }
                stream.Write(buffer, 0, bytes);
                index += count;
            }
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int length)
        {
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(buffer, offset, length - offset);
                if (read <= 0) throw new EndOfStreamException("復旧データが途中で切れています。");
                offset += read;
            }
        }

        private static bool EqualBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }
}
