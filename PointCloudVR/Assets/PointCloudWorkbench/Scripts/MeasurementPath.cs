using System.Collections.Generic;
using UnityEngine;

public sealed class MeasurementPath
{
    private const int CurveSamplesPerSegment = 12;

    public readonly List<Vector3> Points = new List<Vector3>();
    public PointCloudEditor.MeasurementMode Mode { get; private set; } = PointCloudEditor.MeasurementMode.TwoPoint;

    public int Count => Points.Count;

    public void SetMode(PointCloudEditor.MeasurementMode mode)
    {
        Mode = mode;
        if (Mode == PointCloudEditor.MeasurementMode.TwoPoint && Points.Count > 2)
        {
            Points.RemoveRange(2, Points.Count - 2);
        }
    }

    public void AddPoint(Vector3 point)
    {
        if (Mode == PointCloudEditor.MeasurementMode.TwoPoint && Points.Count >= 2)
        {
            Points.Clear();
        }
        Points.Add(point);
    }

    public void RemoveLastPoint()
    {
        if (Points.Count == 0) return;
        Points.RemoveAt(Points.Count - 1);
    }

    public void Clear()
    {
        Points.Clear();
    }

    public float GetLength()
    {
        if (Points.Count < 2) return 0f;
        if (Mode == PointCloudEditor.MeasurementMode.SmoothCurve && Points.Count >= 3)
        {
            return GetSmoothCurveLength();
        }
        return GetPolylineLength();
    }

    public List<Vector3> BuildWorldLinePoints(Transform pointCloudTransform)
    {
        List<Vector3> result = new List<Vector3>();
        if (pointCloudTransform == null || Points.Count < 2) return result;

        if (Mode == PointCloudEditor.MeasurementMode.SmoothCurve && Points.Count >= 3)
        {
            result.Add(pointCloudTransform.TransformPoint(Points[0]));
            for (int i = 0; i < Points.Count - 1; i++)
            {
                for (int s = 1; s <= CurveSamplesPerSegment; s++)
                {
                    float t = (float)s / CurveSamplesPerSegment;
                    result.Add(pointCloudTransform.TransformPoint(CatmullRom(i, t)));
                }
            }
        }
        else
        {
            int count = Mode == PointCloudEditor.MeasurementMode.TwoPoint ? Mathf.Min(2, Points.Count) : Points.Count;
            for (int i = 0; i < count; i++)
            {
                result.Add(pointCloudTransform.TransformPoint(Points[i]));
            }
        }
        return result;
    }

    private float GetPolylineLength()
    {
        float length = 0f;
        int count = Mode == PointCloudEditor.MeasurementMode.TwoPoint ? Mathf.Min(2, Points.Count) : Points.Count;
        for (int i = 1; i < count; i++)
        {
            length += Vector3.Distance(Points[i - 1], Points[i]);
        }
        return length;
    }

    private float GetSmoothCurveLength()
    {
        float length = 0f;
        Vector3 previous = Points[0];
        for (int i = 0; i < Points.Count - 1; i++)
        {
            for (int s = 1; s <= CurveSamplesPerSegment; s++)
            {
                float t = (float)s / CurveSamplesPerSegment;
                Vector3 next = CatmullRom(i, t);
                length += Vector3.Distance(previous, next);
                previous = next;
            }
        }
        return length;
    }

    private Vector3 CatmullRom(int segmentIndex, float t)
    {
        int p1Index = Mathf.Clamp(segmentIndex, 0, Points.Count - 1);
        int p0Index = Mathf.Clamp(p1Index - 1, 0, Points.Count - 1);
        int p2Index = Mathf.Clamp(p1Index + 1, 0, Points.Count - 1);
        int p3Index = Mathf.Clamp(p1Index + 2, 0, Points.Count - 1);

        Vector3 p0 = Points[p0Index];
        Vector3 p1 = Points[p1Index];
        Vector3 p2 = Points[p2Index];
        Vector3 p3 = Points[p3Index];
        float t2 = t * t;
        float t3 = t2 * t;

        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }
}
