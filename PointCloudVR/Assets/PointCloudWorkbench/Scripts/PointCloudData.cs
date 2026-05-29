using UnityEngine;
using System.Runtime.InteropServices;

namespace PointCloudWorkbench
{
    [System.Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct PointData
    {
        public Vector3 position;
        public uint originalColor; // Packed Color32 (RGBA)
        public int label;
        public float distance;

        public PointData(Vector3 pos, Color32 col, int lbl = 0, float dist = 0f)
        {
            position = pos;
            originalColor = PackColor(col);
            label = lbl;
            distance = dist;
        }

        // Helper to pack Color32 to uint (RGBA format matches HLSL unpacker)
        public static uint PackColor(Color32 color)
        {
            return (uint)color.r | ((uint)color.g << 8) | ((uint)color.b << 16) | ((uint)color.a << 24);
        }

        // Helper to unpack uint to Color32
        public static Color32 UnpackColor(uint packedColor)
        {
            byte r = (byte)(packedColor & 0xFF);
            byte g = (byte)((packedColor >> 8) & 0xFF);
            byte b = (byte)((packedColor >> 16) & 0xFF);
            byte a = (byte)((packedColor >> 24) & 0xFF);
            return new Color32(r, g, b, a);
        }
    }
}
