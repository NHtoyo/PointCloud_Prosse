namespace PointCloudWorkbench
{
    public static class PointCloudRevisionBinding
    {
        public static bool RequiresReset(object boundSource, long boundGeneration, long boundRevision,
            object source, long generation, long revision)
        {
            return !ReferenceEquals(boundSource, source) || boundGeneration != generation || boundRevision != revision;
        }
    }
}
