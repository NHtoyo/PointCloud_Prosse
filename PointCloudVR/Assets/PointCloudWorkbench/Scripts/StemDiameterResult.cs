using System;

namespace PointCloudWorkbench
{
    [Serializable]
    public sealed class StemDiameterResult
    {
        public int schema_version;
        public string input_path;
        public int point_count;
        public float scale_mm_per_unit;
        public float centerline_length_mm;
        public StemCenterlineResult centerline;
        public StemDiameterSection[] sections;
    }

    [Serializable]
    public sealed class StemCenterlineResult
    {
        public StemVector3[] support_points_xyz_units;
        public StemVector3[] display_points_xyz_units;
    }

    [Serializable]
    public sealed class StemDiameterSection
    {
        public int index;
        public float position_mm;
        public StemVector3 center_xyz_units;
        public StemVector3 centerline_tangent_xyz;
        public StemVector3 local_axis_xyz;
        public StemVector3 basis_u_xyz;
        public StemVector3 basis_v_xyz;
        public float local_axis_angle_deg;
        public StemEigenvalues local_pca_eigenvalues;
        public float local_pca_linearity;
        public StemDiameterSlice[] slice_results;
        public float equivalent_diameter_mm;
        public float cross_section_area_mm2;
        public float diameter_3mm;
        public float diameter_5mm;
        public float diameter_7mm;
        public float slice_diameter_range_mm;
        public float slice_diameter_std_mm;
        public string calculation_status;
    }

    [Serializable]
    public sealed class StemDiameterSlice
    {
        public float thickness_mm;
        public float equivalent_diameter_mm;
        public float area_mm2;
        public int raw_point_count;
        public int used_point_count;
        public float outlier_fraction;
        public float angular_coverage;
        public float max_gap_deg;
        public float interpolated_fraction;
        public float shape_axis_ratio;
        public float circularity;
        public StemContourPoint[] contour_uv_mm;
        public string calculation_status;
    }

    [Serializable]
    public sealed class StemVector3
    {
        public float x;
        public float y;
        public float z;

        public UnityEngine.Vector3 ToUnity()
        {
            return new UnityEngine.Vector3(x, y, z);
        }
    }

    [Serializable]
    public sealed class StemEigenvalues
    {
        public float lambda1;
        public float lambda2;
        public float lambda3;
    }

    [Serializable]
    public sealed class StemContourPoint
    {
        public float u_mm;
        public float v_mm;
    }
}
