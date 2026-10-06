using UnityEngine;

namespace PointCloudWorkbench
{
    public class PointCloudPicker : MonoBehaviour
    {
        public float pickingRadius = 80f;

        public bool TryPickPoint(Camera camera, Vector3 mousePos, Vector3 fallbackPivot, out Vector3 pickedPoint)
        {
            pickedPoint = fallbackPivot;
            if (camera == null) return false;

            Ray worldRay = camera.ScreenPointToRay(mousePos);
            float minCameraDist = float.MaxValue;
            bool found = false;

            var renderers = Object.FindObjectsByType<PointCloudRenderer>(FindObjectsInactive.Exclude);
            foreach (var renderer in renderers)
            {
                PointData[] points = renderer.GetPointData();
                if (points == null || points.Length == 0) continue;

                Transform display = renderer.DisplayTransform;
                Matrix4x4 worldToLocal = display.worldToLocalMatrix;
                Vector3 localOrigin = worldToLocal.MultiplyPoint(worldRay.origin);
                Vector3 localDir = worldToLocal.MultiplyVector(worldRay.direction).normalized;
                Ray localRay = new Ray(localOrigin, localDir);

                float scaleX = display.lossyScale.x;
                float localThreshold = pickingRadius / (scaleX > 0.001f ? scaleX : 1f);

                for (int i = 0; i < points.Length; i++)
                {
                    Vector3 p = points[i].position;
                    Vector3 v = p - localRay.origin;
                    float proj = Vector3.Dot(v, localRay.direction);
                    if (proj < 0) continue;

                    Vector3 closestPointOnRay = localRay.origin + localRay.direction * proj;
                    float distSq = (p - closestPointOnRay).sqrMagnitude;
                    if (distSq < localThreshold * localThreshold)
                    {
                        if (proj < minCameraDist)
                        {
                            minCameraDist = proj;
                            pickedPoint = display.TransformPoint(p);
                            found = true;
                        }
                    }
                }
            }

            return found;
        }
    }
}
