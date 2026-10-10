using UnityEngine;

namespace PointCloudWorkbench
{
    public struct PointData
    {
        public Vector3 position;
        public uint originalColor;
        public int label;

        public PointData(float x, float y, float z, uint color, int label)
        {
            position = new Vector3(x, y, z);
            originalColor = color;
            this.label = label;
        }

        public static Color32 UnpackColor(uint color) => new(
            (byte)(color & 0xff), (byte)((color >> 8) & 0xff),
            (byte)((color >> 16) & 0xff), (byte)((color >> 24) & 0xff));
    }
}

public sealed class PointCloudRenderer
{
    private readonly PointCloudWorkbench.PointData[] data;
    public long DatasetGeneration { get; set; } = 1;
    public long ContentRevision { get; private set; } = 1;
    public bool ThrowNextBufferUpdate { get; set; }

    public PointCloudRenderer(params PointCloudWorkbench.PointData[] points) => data = points;
    public PointCloudWorkbench.PointData[] GetPointData() => data;

    public float LastAppliedCorrection { get; private set; }
    public bool ApplyPointCoordinateCorrection(float correctionFactor)
    {
        LastAppliedCorrection = correctionFactor;
        return true;
    }

    public bool TryUpdatePointBuffer()
    {
        if (ThrowNextBufferUpdate)
        {
            ThrowNextBufferUpdate = false;
            throw new InvalidOperationException("injected GPU buffer update failure");
        }
        ContentRevision++;
        return true;
    }
}
