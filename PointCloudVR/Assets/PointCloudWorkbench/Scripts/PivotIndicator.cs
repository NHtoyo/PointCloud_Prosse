using UnityEngine;

namespace PointCloudWorkbench
{
    public class PivotIndicator : MonoBehaviour
    {
        public Color indicatorColor = new Color(0f, 1f, 0f, 0.8f);

        private GameObject pivotVisual;
        private Material indicatorMaterial;
        private float visibleEndTime = 0f;

        void Start()
        {
            CreatePivotVisual();
        }

        void CreatePivotVisual()
        {
            // 振動バグを防ぐため、カメラ(transform)の子に設定しないようにする
            pivotVisual = new GameObject("CC_Pivot_Indicator");

            indicatorMaterial = new Material(Shader.Find("Sprites/Default"));
            indicatorMaterial.color = indicatorColor;

            float size = 0.15f;
            float thickness = 0.005f;

            CreateLine("Line_X", Vector3.right * size, new Vector3(size, thickness, thickness));
            CreateLine("Line_Y", Vector3.up * size, new Vector3(thickness, size, thickness));
            CreateLine("Line_Z", Vector3.forward * size, new Vector3(thickness, thickness, size));

            pivotVisual.SetActive(false);
        }

        void CreateLine(string name, Vector3 offset, Vector3 scale)
        {
            GameObject line = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            line.name = name;
            Destroy(line.GetComponent<CapsuleCollider>()); 
            line.transform.SetParent(pivotVisual.transform);
            line.transform.localPosition = Vector3.zero;
            line.transform.localScale = scale;
            line.GetComponent<MeshRenderer>().sharedMaterial = indicatorMaterial;
        }

        public void Show(Vector3 pivotPoint, float distanceToPivot, float duration = 1.0f)
        {
            if (pivotVisual == null) CreatePivotVisual();

            pivotVisual.SetActive(true);
            pivotVisual.transform.position = pivotPoint;
            pivotVisual.transform.localScale = Vector3.one * (distanceToPivot * 0.1f);
            visibleEndTime = Time.time + duration;
        }

        public void UpdatePosition(Vector3 pivotPoint, float distanceToPivot)
        {
            if (pivotVisual != null && pivotVisual.activeSelf)
            {
                pivotVisual.transform.position = pivotPoint;
                pivotVisual.transform.localScale = Vector3.one * (distanceToPivot * 0.1f);
            }
        }

        void Update()
        {
            if (pivotVisual != null && pivotVisual.activeSelf)
            {
                if (Time.time > visibleEndTime)
                {
                    pivotVisual.SetActive(false);
                }
            }
        }

        void OnDestroy()
        {
            if (pivotVisual != null) Destroy(pivotVisual);
            if (indicatorMaterial != null) Destroy(indicatorMaterial);
        }
    }
}
