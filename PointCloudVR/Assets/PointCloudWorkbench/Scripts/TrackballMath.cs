using UnityEngine;

namespace PointCloudWorkbench
{
    public static class TrackballMath
    {
        public static Vector3 ConvertMousePositionToOrientation(Vector2 mousePos, Vector2 center, float screenHeight, float fieldOfView, float distanceToPivot)
        {
            Vector3 v = new Vector3(mousePos.x - center.x, mousePos.y - center.y, 0f);

            // トラックボールの3D空間上での半径 R = distanceToPivot * 0.5f の、
            // 画面上での投影サイズ（ピクセル）を正確に計算する
            float fovRad = fieldOfView * Mathf.Deg2Rad;
            
            // R / (2 * distanceToPivot * tan(FOV/2)) = 0.25f / tan(FOV/2)
            float ratio = 0.25f / Mathf.Tan(fovRad * 0.5f);
            float radiusInPixels = screenHeight * ratio;

            // アスペクト比やサイズに歪みが出ないよう、ピクセル単位の半径で正規化する
            v.x /= radiusInPixels;
            v.y /= radiusInPixels;

            float d2 = v.x * v.x + v.y * v.y;
            if (d2 > 1.0f)
            {
                float d = Mathf.Sqrt(d2);
                v.x /= d;
                v.y /= d;
                v.z = 0f;
            }
            else
            {
                v.z = Mathf.Sqrt(1.0f - d2);
            }

            return v;
        }
    }
}
