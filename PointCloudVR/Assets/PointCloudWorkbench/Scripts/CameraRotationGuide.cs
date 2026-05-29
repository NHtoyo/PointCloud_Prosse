using UnityEngine;

namespace PointCloudWorkbench
{
    public class CameraRotationGuide : MonoBehaviour
    {
        private GameObject rotationGuide;
        private LineRenderer ringX, ringY, ringZ, ringViewport;
        private GameObject centerSphere;

        void Start()
        {
            CreateRotationGuide();
        }

        void CreateRotationGuide()
        {
            // 振動と回転のバグを防ぐため、カメラ(transform)の子にせずワールドルートに配置する
            rotationGuide = new GameObject("CC_Rotation_Guide");

            centerSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            centerSphere.name = "Center_Sphere";
            Destroy(centerSphere.GetComponent<SphereCollider>());
            centerSphere.transform.SetParent(rotationGuide.transform);
            centerSphere.transform.localPosition = Vector3.zero;
            centerSphere.transform.localScale = Vector3.one * 0.04f;

            var sphereMat = new Material(Shader.Find("Hidden/Internal-Colored"));
            sphereMat.SetInt("_ZTest", 8); // Always
            sphereMat.color = Color.yellow;
            centerSphere.GetComponent<MeshRenderer>().sharedMaterial = sphereMat;

            ringX = CreateRing("Ring_X", Color.red, Vector3.right);
            ringY = CreateRing("Ring_Y", Color.green, Vector3.up);
            ringZ = CreateRing("Ring_Z", Color.cyan, Vector3.forward);
            ringViewport = CreateRing("Ring_Viewport", new Color(0.5f, 1.0f, 0.2f, 0.8f), Vector3.forward);

            rotationGuide.SetActive(false);
        }

        LineRenderer CreateRing(string name, Color color, Vector3 normal)
        {
            GameObject ringGo = new GameObject(name);
            ringGo.transform.SetParent(rotationGuide.transform);
            ringGo.transform.localPosition = Vector3.zero;
            ringGo.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, normal);

            LineRenderer lr = ringGo.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.startWidth = 0.005f;
            lr.endWidth = 0.005f;

            var lineMat = new Material(Shader.Find("Hidden/Internal-Colored"));
            lineMat.SetInt("_ZTest", 8); // Always
            lineMat.color = color;
            lr.sharedMaterial = lineMat;

            int segments = 64;
            lr.positionCount = segments;
            Vector3[] points = new Vector3[segments];
            float radius = 1.0f;
            for (int i = 0; i < segments; i++)
            {
                float angle = (float)i / segments * Mathf.PI * 2f;
                points[i] = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f);
            }
            lr.SetPositions(points);

            return lr;
        }

        public void SetActive(bool active)
        {
            if (rotationGuide == null) CreateRotationGuide();
            if (rotationGuide.activeSelf != active)
            {
                rotationGuide.SetActive(active);
            }
        }

        public void UpdatePositionAndRotation(Vector3 pivotPoint, float distanceToPivot, Camera camera)
        {
            if (rotationGuide == null || !rotationGuide.activeSelf) return;

            rotationGuide.transform.position = pivotPoint;

            float scaleFactor = distanceToPivot * 0.5f; 
            rotationGuide.transform.localScale = Vector3.one * scaleFactor;

            if (camera != null && ringViewport != null)
            {
                ringViewport.transform.rotation = Quaternion.LookRotation(camera.transform.forward, camera.transform.up);
            }

            float lineWidth = distanceToPivot * 0.003f;
            ringX.startWidth = ringX.endWidth = lineWidth;
            ringY.startWidth = ringY.endWidth = lineWidth;
            ringZ.startWidth = ringZ.endWidth = lineWidth;
            if (ringViewport != null)
            {
                ringViewport.startWidth = ringViewport.endWidth = lineWidth;
            }

            centerSphere.transform.localScale = Vector3.one * 0.03f;
        }

        void OnDestroy()
        {
            if (rotationGuide != null) Destroy(rotationGuide);
        }
    }
}
