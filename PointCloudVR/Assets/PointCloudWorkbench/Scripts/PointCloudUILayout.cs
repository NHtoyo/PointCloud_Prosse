using UnityEngine;

namespace PointCloudWorkbench
{
    public struct PointCloudUIRegions
    {
        public Rect LeftPanel;
        public Rect CenterPanel;
        public Rect RightPanel;
        public Rect DiagnosticButton;
        public Rect DiagnosticDetails;
    }

    public static class PointCloudUILayout
    {
        private const float SideMargin = 20f;
        private const float PanelGap = 10f;
        private const float TopReservedHeight = 48f;
        private const float CompactToolsWidth = 390f;

        public static bool UsesCompactTools(float leftPanelWidth)
        {
            return leftPanelWidth < CompactToolsWidth;
        }

        public static PointCloudUIRegions Calculate(float screenWidth, float screenHeight)
        {
            float width = Mathf.Max(0f, screenWidth);
            float height = Mathf.Max(0f, screenHeight);
            float leftWidth = Mathf.Min(460f, Mathf.Max(180f, width * 0.28f));
            float rightWidth = Mathf.Min(460f, width * 0.25f);
            float sideSpace = Mathf.Max(0f, width - SideMargin * 2f - PanelGap * 2f);
            float desiredSideSpace = leftWidth + rightWidth;
            if (desiredSideSpace > sideSpace && desiredSideSpace > 0f)
            {
                float scale = sideSpace / desiredSideSpace;
                leftWidth *= scale;
                rightWidth *= scale;
            }

            Rect left = new Rect(SideMargin, 20f, leftWidth,
                Mathf.Min(930f, Mathf.Max(0f, height - 40f)));
            Rect right = new Rect(Mathf.Max(left.xMax + PanelGap, width - SideMargin - rightWidth),
                TopReservedHeight, rightWidth,
                Mathf.Min(930f, Mathf.Max(0f, height - TopReservedHeight - 20f)));
            float centerX = left.xMax + PanelGap;
            float centerRight = Mathf.Max(centerX, right.x - PanelGap);
            Rect center = new Rect(centerX, 15f, centerRight - centerX, Mathf.Max(0f, height - 30f));

            float buttonWidth = Mathf.Min(242f, Mathf.Max(0f, width - 32f));
            Rect diagnostic = new Rect(Mathf.Max(16f, width - 16f - buttonWidth), 8f,
                buttonWidth, 32f);
            float detailsWidth = Mathf.Min(600f, Mathf.Max(0f, width - 32f));
            float detailsHeight = Mathf.Min(550f, Mathf.Max(0f, height - 56f));
            Rect diagnosticDetails = new Rect(Mathf.Max(16f, width - detailsWidth - 16f), 48f,
                detailsWidth, detailsHeight);

            return new PointCloudUIRegions
            {
                LeftPanel = left,
                CenterPanel = center,
                RightPanel = right,
                DiagnosticButton = diagnostic,
                DiagnosticDetails = diagnosticDetails
            };
        }

        public static bool BlocksUnderlyingInput(bool detailsOpen, Rect diagnosticButton, Vector2 pointer)
        {
            return detailsOpen || diagnosticButton.Contains(pointer);
        }
    }
}
