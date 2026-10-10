using System;
using System.Threading;

namespace PointCloudWorkbench
{
    public struct PointCloudPoint3
    {
        public float X;
        public float Y;
        public float Z;

        public PointCloudPoint3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    public sealed class ExactNearestNeighbor3D
    {
        private struct Node
        {
            public int PointIndex;
            public int Left;
            public int Right;
            public float MinX;
            public float MinY;
            public float MinZ;
            public float MaxX;
            public float MaxY;
            public float MaxZ;
        }

        private readonly PointCloudPoint3[] points;
        private readonly int[] pointIndices;
        private readonly Node[] nodes;
        private readonly CancellationToken cancellationToken;
        private int nextNode;
        private readonly int root;

        public int Count => points.Length;

        public ExactNearestNeighbor3D(PointCloudPoint3[] points, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            if (points.Length == 0) throw new ArgumentException("At least one reference point is required.", nameof(points));
            this.points = points;
            this.cancellationToken = cancellationToken;
            pointIndices = new int[points.Length];
            nodes = new Node[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!IsFinite(points[i].X) || !IsFinite(points[i].Y) || !IsFinite(points[i].Z))
                    throw new ArgumentException("Reference points must contain finite coordinates.", nameof(points));
                pointIndices[i] = i;
            }
            root = Build(0, points.Length);
        }

        public int FindNearest(PointCloudPoint3 query, out float distanceSquared)
        {
            int index = FindNearest(query, out double exactDistanceSquared);
            distanceSquared = (float)exactDistanceSquared;
            return index;
        }

        public int FindNearest(PointCloudPoint3 query, out double distanceSquared)
        {
            if (!IsFinite(query.X) || !IsFinite(query.Y) || !IsFinite(query.Z))
                throw new ArgumentException("Query point must contain finite coordinates.", nameof(query));

            int bestIndex = -1;
            distanceSquared = double.PositiveInfinity;
            Search(root, query, ref bestIndex, ref distanceSquared);
            return bestIndex;
        }

        private int Build(int start, int end)
        {
            if (start >= end) return -1;

            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            for (int i = start; i < end; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                PointCloudPoint3 p = points[pointIndices[i]];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Z < minZ) minZ = p.Z;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
                if (p.Z > maxZ) maxZ = p.Z;
            }

            double rangeX = (double)maxX - minX;
            double rangeY = (double)maxY - minY;
            double rangeZ = (double)maxZ - minZ;
            int axis = rangeX >= rangeY && rangeX >= rangeZ ? 0 :
                (rangeY >= rangeZ ? 1 : 2);
            int middle = start + (end - start) / 2;
            SelectMedian(start, end - 1, middle, axis);
            int nodeIndex = nextNode++;
            int left = Build(start, middle);
            int right = Build(middle + 1, end);

            PointCloudPoint3 point = points[pointIndices[middle]];
            Node node = new Node
            {
                PointIndex = pointIndices[middle],
                Left = left,
                Right = right,
                MinX = point.X,
                MinY = point.Y,
                MinZ = point.Z,
                MaxX = point.X,
                MaxY = point.Y,
                MaxZ = point.Z
            };
            IncludeBounds(ref node, left);
            IncludeBounds(ref node, right);
            nodes[nodeIndex] = node;
            return nodeIndex;
        }

        private void SelectMedian(int low, int high, int target, int axis)
        {
            while (low < high)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int pivot = pointIndices[low + (high - low) / 2];
                int left = low;
                int right = high;
                while (left <= right)
                {
                    while (Compare(pointIndices[left], pivot, axis) < 0) left++;
                    while (Compare(pointIndices[right], pivot, axis) > 0) right--;
                    if (left <= right)
                    {
                        int swap = pointIndices[left];
                        pointIndices[left] = pointIndices[right];
                        pointIndices[right] = swap;
                        left++;
                        right--;
                    }
                }
                if (target <= right) high = right;
                else if (target >= left) low = left;
                else return;
            }
        }

        private int Compare(int a, int b, int axis)
        {
            PointCloudPoint3 pa = points[a];
            PointCloudPoint3 pb = points[b];
            float va = axis == 0 ? pa.X : axis == 1 ? pa.Y : pa.Z;
            float vb = axis == 0 ? pb.X : axis == 1 ? pb.Y : pb.Z;
            int comparison = va.CompareTo(vb);
            return comparison != 0 ? comparison : a.CompareTo(b);
        }

        private void IncludeBounds(ref Node node, int childIndex)
        {
            if (childIndex < 0) return;
            Node child = nodes[childIndex];
            node.MinX = Math.Min(node.MinX, child.MinX);
            node.MinY = Math.Min(node.MinY, child.MinY);
            node.MinZ = Math.Min(node.MinZ, child.MinZ);
            node.MaxX = Math.Max(node.MaxX, child.MaxX);
            node.MaxY = Math.Max(node.MaxY, child.MaxY);
            node.MaxZ = Math.Max(node.MaxZ, child.MaxZ);
        }

        private void Search(int nodeIndex, PointCloudPoint3 query, ref int bestIndex, ref double bestDistanceSquared)
        {
            if (nodeIndex < 0) return;
            Node node = nodes[nodeIndex];
            if (BoundsDistanceSquared(node, query) > bestDistanceSquared) return;

            PointCloudPoint3 point = points[node.PointIndex];
            double dx = (double)query.X - point.X;
            double dy = (double)query.Y - point.Y;
            double dz = (double)query.Z - point.Z;
            double distanceSquared = dx * dx + dy * dy + dz * dz;
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                bestIndex = node.PointIndex;
            }

            double leftDistance = node.Left < 0 ? double.PositiveInfinity : BoundsDistanceSquared(nodes[node.Left], query);
            double rightDistance = node.Right < 0 ? double.PositiveInfinity : BoundsDistanceSquared(nodes[node.Right], query);
            if (leftDistance <= rightDistance)
            {
                if (leftDistance <= bestDistanceSquared) Search(node.Left, query, ref bestIndex, ref bestDistanceSquared);
                if (rightDistance <= bestDistanceSquared) Search(node.Right, query, ref bestIndex, ref bestDistanceSquared);
            }
            else
            {
                if (rightDistance <= bestDistanceSquared) Search(node.Right, query, ref bestIndex, ref bestDistanceSquared);
                if (leftDistance <= bestDistanceSquared) Search(node.Left, query, ref bestIndex, ref bestDistanceSquared);
            }
        }

        private static double BoundsDistanceSquared(Node node, PointCloudPoint3 p)
        {
            double dx = p.X < node.MinX ? (double)node.MinX - p.X : p.X > node.MaxX ? (double)p.X - node.MaxX : 0.0;
            double dy = p.Y < node.MinY ? (double)node.MinY - p.Y : p.Y > node.MaxY ? (double)p.Y - node.MaxY : 0.0;
            double dz = p.Z < node.MinZ ? (double)node.MinZ - p.Z : p.Z > node.MaxZ ? (double)p.Z - node.MaxZ : 0.0;
            return dx * dx + dy * dy + dz * dz;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
