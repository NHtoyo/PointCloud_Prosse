using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace PointCloudWorkbench
{
    public sealed class StemDiameterVisualizer : MonoBehaviour
    {
        private const float OverlayWidth = 0.35f;
        private Transform overlayRoot;
        private PointCloudRenderer targetRenderer;
        private StemDiameterResult result;
        private int selectedIndex = -1;
        private bool visible = true;
        private LineRenderer centerline;
        private LineRenderer sectionContour;
        private LineRenderer sectionAxis;
        private Material centerlineMaterial;
        private Material sectionMaterial;

        public void SetResult(PointCloudRenderer renderer, StemDiameterResult value)
        {
            Clear();
            if (renderer == null || value == null || value.centerline == null) return;

            targetRenderer = renderer;
            result = value;
            selectedIndex = -1;
            overlayRoot = new GameObject("StemDiameterOverlays").transform;
            overlayRoot.SetParent(targetRenderer.DisplayTransform, false);
            centerlineMaterial = CreateMaterial(new Color(0.15f, 0.85f, 1f, 1f));
            sectionMaterial = CreateMaterial(new Color(1f, 0.58f, 0.12f, 1f));
            centerline = CreateLine("Stem centerline", centerlineMaterial, OverlayWidth);
            sectionContour = CreateLine("Selected section contour", sectionMaterial, OverlayWidth * 1.5f);
            sectionAxis = CreateLine("Selected local axis", sectionMaterial, OverlayWidth * 1.5f);

            StemVector3[] line = value.centerline.display_points_xyz_mm;
            if (line != null && line.Length > 1)
            {
                centerline.positionCount = line.Length;
                for (int i = 0; i < line.Length; i++)
                    centerline.SetPosition(i, targetRenderer.MillimetersToDataPoint(line[i].ToUnity()));
            }
            SetVisible(visible);
        }

        public void SelectSection(int index)
        {
            if (result == null || result.sections == null || index < 0 || index >= result.sections.Length) return;
            selectedIndex = index;
            DrawSelectedSection();
        }

        public void SetVisible(bool value)
        {
            visible = value;
            if (overlayRoot != null) overlayRoot.gameObject.SetActive(value);
        }

        public void Clear()
        {
            if (overlayRoot != null) Destroy(overlayRoot.gameObject);
            if (centerlineMaterial != null) Destroy(centerlineMaterial);
            if (sectionMaterial != null) Destroy(sectionMaterial);
            overlayRoot = null;
            targetRenderer = null;
            result = null;
            centerline = null;
            sectionContour = null;
            sectionAxis = null;
            centerlineMaterial = null;
            sectionMaterial = null;
            selectedIndex = -1;
        }

        private void DrawSelectedSection()
        {
            if (selectedIndex < 0 || sectionContour == null || sectionAxis == null) return;
            StemDiameterSection section = result.sections[selectedIndex];
            if (section.center_xyz_mm == null || section.local_axis_xyz == null) return;

            Vector3 centerMillimeters = section.center_xyz_mm.ToUnity();
            Vector3 center = targetRenderer.MillimetersToDataPoint(centerMillimeters);
            Vector3 axis = section.local_axis_xyz.ToUnity().normalized;
            sectionAxis.positionCount = 2;
            const float axisHalfLengthMm = 10f;
            float axisHalfLengthData = targetRenderer.MillimetersToDataLength(axisHalfLengthMm);
            sectionAxis.SetPosition(0, center - axis * axisHalfLengthData);
            sectionAxis.SetPosition(1, center + axis * axisHalfLengthData);

            StemDiameterSlice slice = null;
            if (section.slice_results != null)
            {
                for (int i = 0; i < section.slice_results.Length; i++)
                    if (Mathf.Abs(section.slice_results[i].thickness_mm - 5f) < 0.001f)
                    {
                        slice = section.slice_results[i];
                        break;
                    }
            }
            if (slice == null || slice.contour_uv_mm == null || slice.contour_uv_mm.Length < 3 ||
                section.basis_u_xyz == null || section.basis_v_xyz == null)
            {
                sectionContour.positionCount = 0;
                return;
            }

            Vector3 u = section.basis_u_xyz.ToUnity().normalized;
            Vector3 v = section.basis_v_xyz.ToUnity().normalized;
            sectionContour.positionCount = slice.contour_uv_mm.Length + 1;
            for (int i = 0; i < slice.contour_uv_mm.Length; i++)
            {
                StemContourPoint point = slice.contour_uv_mm[i];
                sectionContour.SetPosition(i, center +
                    u * targetRenderer.MillimetersToDataLength(point.u_mm) +
                    v * targetRenderer.MillimetersToDataLength(point.v_mm));
            }
            sectionContour.SetPosition(slice.contour_uv_mm.Length, sectionContour.GetPosition(0));
        }

        private LineRenderer CreateLine(string objectName, Material material, float width)
        {
            GameObject lineObject = new GameObject(objectName);
            lineObject.transform.SetParent(overlayRoot, false);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = false;
            line.alignment = LineAlignment.View;
            line.numCapVertices = 0;
            line.numCornerVertices = 0;
            line.widthMultiplier = width;
            line.sharedMaterial = material;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        private static Material CreateMaterial(Color color)
        {
            Shader shader = Shader.Find("PointCloudWorkbench/OverlayColor");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null)
                throw new InvalidOperationException("茎径オーバーレイ用シェーダーを利用できません。Playerのシェーダー設定を確認してください。");
            Material material = new Material(shader);
            material.color = color;
            return material;
        }

        private void OnDestroy()
        {
            Clear();
        }
    }
}
