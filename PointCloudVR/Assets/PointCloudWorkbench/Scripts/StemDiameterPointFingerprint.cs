using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace PointCloudWorkbench
{
    public sealed class StemDiameterPointFingerprintResult
    {
        public int PointCount { get; }
        public string Fingerprint { get; }

        internal StemDiameterPointFingerprintResult(int pointCount, string fingerprint)
        {
            PointCount = pointCount;
            Fingerprint = fingerprint;
        }
    }

    public static class StemDiameterPointFingerprint
    {
        private const int PointsPerBuffer = 4096;
        private const int BytesPerPoint = 12;

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Value;
            [FieldOffset(0)] public int Bits;
        }

        public static StemDiameterPointFingerprintResult Compute(PointData[] points, ExportPointMode mode,
            CancellationToken cancellationToken = default)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));

            byte[] buffer = new byte[PointsPerBuffer * BytesPerPoint];
            int bufferedBytes = 0;
            int includedCount = 0;
            using (SHA256 sha256 = SHA256.Create())
            {
                for (int i = 0; i < points.Length; i++)
                {
                    if ((i & (PointsPerBuffer - 1)) == 0) cancellationToken.ThrowIfCancellationRequested();
                    PointData point = points[i];
                    if (!PlyExportService.IsIncluded(point.label, mode)) continue;

                    WriteFloatLittleEndian(buffer, bufferedBytes, point.position.x);
                    WriteFloatLittleEndian(buffer, bufferedBytes + 4, point.position.y);
                    WriteFloatLittleEndian(buffer, bufferedBytes + 8, point.position.z);
                    bufferedBytes += BytesPerPoint;
                    includedCount++;

                    if (bufferedBytes == buffer.Length)
                    {
                        sha256.TransformBlock(buffer, 0, bufferedBytes, buffer, 0);
                        bufferedBytes = 0;
                    }
                }

                if (bufferedBytes > 0) sha256.TransformBlock(buffer, 0, bufferedBytes, buffer, 0);
                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                string fingerprint = "sha256:" + BitConverter.ToString(sha256.Hash).Replace("-", "").ToLowerInvariant();
                return new StemDiameterPointFingerprintResult(includedCount, fingerprint);
            }
        }

        private static void WriteFloatLittleEndian(byte[] buffer, int offset, float value)
        {
            FloatBits bits = new FloatBits { Value = value };
            buffer[offset] = (byte)bits.Bits;
            buffer[offset + 1] = (byte)(bits.Bits >> 8);
            buffer[offset + 2] = (byte)(bits.Bits >> 16);
            buffer[offset + 3] = (byte)(bits.Bits >> 24);
        }
    }
}
