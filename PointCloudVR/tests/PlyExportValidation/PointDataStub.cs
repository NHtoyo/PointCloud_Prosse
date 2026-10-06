namespace PointCloudWorkbench
{
    public struct TestPosition
    {
        public float x;
        public float y;
        public float z;
    }

    public struct PointData
    {
        public TestPosition position;
        public uint originalColor;
        public int label;

        public PointData(float x, float y, float z, uint color, int label)
        {
            position = new TestPosition { x = x, y = y, z = z };
            originalColor = color;
            this.label = label;
        }
    }
}
