import inspect
import math
import unittest
from pathlib import Path

import numpy as np

from stem_diameter_algorithm import (
    StemDiameterParams,
    StemDiameterStageError,
    _polyline_segment_diagnostics,
    _sample_polyline,
    analyze_stem,
)


def _basis(axis):
    axis = axis / np.linalg.norm(axis)
    ref = np.array([0.0, 0.0, 1.0])
    if abs(np.dot(axis, ref)) > 0.9:
        ref = np.array([1.0, 0.0, 0.0])
    u = np.cross(axis, ref)
    u /= np.linalg.norm(u)
    v = np.cross(axis, u)
    v /= np.linalg.norm(v)
    return u, v


def cylinder_points_mm(diameter_mm=8.0, tilt_deg=0.0, ellipse_ratio=1.0,
                       remove_sector_deg=0.0, curved=False, length_mm=160.0,
                       axial_step_mm=0.75, angular_count=128):
    axial = np.arange(-length_mm / 2, length_mm / 2 + 0.1, axial_step_mm)
    angles = np.linspace(0.0, 2.0 * np.pi, angular_count, endpoint=False)
    radius_a = diameter_mm / 2.0
    radius_b = radius_a * ellipse_ratio
    rings = []

    for t in axial:
        if curved:
            amplitude = 5.0
            center = np.array([amplitude * np.sin(2 * np.pi * t / length_mm), t, 0.0])
            slope = amplitude * (2 * np.pi / length_mm) * np.cos(2 * np.pi * t / length_mm)
            axis = np.array([slope, 1.0, 0.0])
        else:
            theta = np.deg2rad(tilt_deg)
            axis = np.array([np.sin(theta), np.cos(theta), 0.0])
            center = axis * t
        u, v = _basis(axis)
        ring = center + radius_a * np.cos(angles)[:, None] * u + radius_b * np.sin(angles)[:, None] * v
        if remove_sector_deg > 0:
            keep = angles >= np.deg2rad(remove_sector_deg)
            ring = ring[keep]
        rings.append(ring)
    return np.vstack(rings)


class StemDiameterAlgorithmTests(unittest.TestCase):
    def analyze(self, points_mm, **overrides):
        params = StemDiameterParams(query_workers=1, **overrides)
        return analyze_stem(points_mm, params)

    def test_algorithm_has_no_io_or_unity_dependencies(self):
        source = Path(inspect.getsourcefile(analyze_stem)).read_text(encoding="utf-8")
        for forbidden in ("open3d", "matplotlib", "UnityEngine", "subprocess", "pointcloud_io", "1200"):
            self.assertNotIn(forbidden, source.lower())

    def test_known_diameter_and_tilt_invariance(self):
        for tilt in (0.0, 15.0, 30.0, 45.0):
            with self.subTest(tilt=tilt):
                result = self.analyze(cylinder_points_mm(tilt_deg=tilt))
                self.assertGreaterEqual(len(result.sections), 15)
                self.assertAlmostEqual(result.sections[0].position_mm, 0.0, places=6)
                self.assertAlmostEqual(result.sections[1].position_mm, 10.0, places=2)
                values = [
                    section.equivalent_diameter_mm for section in result.sections[2:-2]
                    if section.equivalent_diameter_mm is not None
                ]
                self.assertGreater(len(values), 5)
                self.assertAlmostEqual(float(np.median(values)), 8.0, delta=0.35)
                self.assertTrue(all(section.diameter_3mm is not None and
                                    section.diameter_5mm is not None and
                                    section.diameter_7mm is not None
                                    for section in result.sections[2:-2]))

    def test_ellipse_reports_area_equivalent_diameter(self):
        a, b = 5.0, 3.0
        result = self.analyze(cylinder_points_mm(diameter_mm=2 * a, ellipse_ratio=b / a))
        expected = 2.0 * math.sqrt(a * b)
        values = [s.equivalent_diameter_mm for s in result.sections[2:-2]
                  if s.equivalent_diameter_mm is not None]
        self.assertAlmostEqual(float(np.median(values)), expected, delta=0.4)

    def test_missing_arc_changes_coverage_without_rejecting_section(self):
        full = self.analyze(cylinder_points_mm())
        result = self.analyze(cylinder_points_mm(remove_sector_deg=90.0))
        section = result.sections[len(result.sections) // 2]
        full_section = full.sections[len(full.sections) // 2]
        primary = next(s for s in section.slice_results if s.thickness_mm == 5.0)
        full_primary = next(s for s in full_section.slice_results if s.thickness_mm == 5.0)
        self.assertEqual(section.calculation_status, "ok")
        self.assertLess(primary.angular_coverage, full_primary.angular_coverage - 0.1)
        self.assertGreaterEqual(primary.max_gap_deg, 40.0)

    def test_curved_stem_has_stable_diameter(self):
        result = self.analyze(cylinder_points_mm(curved=True))
        values = [s.equivalent_diameter_mm for s in result.sections[3:-3]
                  if s.equivalent_diameter_mm is not None]
        self.assertGreater(len(values), 5)
        self.assertAlmostEqual(float(np.median(values)), 8.0, delta=0.6)

    def test_pca_polyline_tracks_straight_cylinder_arc_length(self):
        result = self.analyze(
            cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96),
            centerline_axis_mode="pca", centerline_model="polyline",
        )
        self.assertGreater(result.centerline_length_mm, 70.0)
        self.assertLess(result.centerline_length_mm, 85.0)
        self.assertEqual(result.sections[0].position_mm, 0.0)
        self.assertAlmostEqual(result.sections[1].position_mm, 10.0, places=6)
        self.assertEqual(result.diagnostics["centerline_model"]["model"], "polyline")
        self.assertEqual(result.diagnostics["arc_length_sampling"]["section_count"], len(result.sections))

    def test_y_polyline_tracks_straight_cylinder(self):
        result = self.analyze(
            cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96),
            centerline_axis_mode="y", centerline_model="polyline",
        )
        self.assertGreater(result.centerline_length_mm, 70.0)
        self.assertLess(result.centerline_length_mm, 85.0)
        self.assertEqual(result.diagnostics["axis_estimation"]["axis_mode"], "y")
        np.testing.assert_array_equal(result.diagnostics["axis_estimation"]["axis_xyz"], [0.0, 1.0, 0.0])

    def test_pca_and_y_spline_modes_run(self):
        points = cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96, curved=True)
        for axis_mode in ("pca", "y"):
            with self.subTest(axis_mode=axis_mode):
                result = self.analyze(points, centerline_axis_mode=axis_mode, centerline_model="spline")
                self.assertGreater(result.centerline_length_mm, 70.0)
                self.assertGreater(len(result.sections), 5)
                self.assertEqual(result.diagnostics["centerline_model"]["model"], "spline")

    def test_largest_component_removes_separated_floating_cluster(self):
        stem = cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96)
        floating = np.array([[500.0 + i * 0.01, 500.0, 500.0] for i in range(20)])
        result = self.analyze(np.vstack((stem, floating)), centerline_axis_mode="y")
        component = result.diagnostics["connected_component"]
        self.assertEqual(component["input_points"], len(stem) + len(floating))
        self.assertGreaterEqual(component["removed_points"], len(floating))
        self.assertEqual(component["kept_points"], result.diagnostics["centerline_binning"]["input_points"])
        self.assertGreater(component["component_count"], 1)
        self.assertGreater(component["median_knn_distance_mm"], 0.0)
        self.assertAlmostEqual(component["epsilon_mm"], 2.5 * component["median_knn_distance_mm"])

    def test_disabling_largest_component_preserves_all_input_points(self):
        points = cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96)
        result = self.analyze(points, use_largest_component=False, centerline_axis_mode="y")
        component = result.diagnostics["connected_component"]
        self.assertEqual(component["input_points"], len(points))
        self.assertEqual(component["kept_points"], len(points))
        self.assertEqual(component["removed_points"], 0)
        self.assertIsNone(component["median_knn_distance_mm"])

    def test_polyline_support_outlier_is_local_to_adjacent_segments(self):
        params = StemDiameterParams(measurement_interval_mm=2.0)
        base = np.array([[0.0, y, 0.0] for y in (0.0, 10.0, 20.0, 30.0, 40.0)])
        shifted = base.copy()
        shifted[1, 0] = 10.0
        _, base_centers, _, _, _ = _sample_polyline(base, params)
        _, shifted_centers, _, _, _ = _sample_polyline(shifted, params)
        far_points = shifted_centers[shifted_centers[:, 1] >= 25.0]
        self.assertGreater(len(far_points), 3)
        self.assertTrue(np.all(np.abs(far_points[:, 0]) < 1e-9))
        self.assertEqual(base_centers[-1, 0], 0.0)

    def test_component_settings_are_separate_from_radial_mad_sigma(self):
        points = cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96)
        result = self.analyze(points, component_knn_k=6, component_alpha=3.0)
        self.assertEqual(result.parameters.radial_mad_sigma, 3.0)
        self.assertEqual(result.diagnostics["connected_component"]["K"], 6)
        self.assertEqual(result.diagnostics["connected_component"]["alpha"], 3.0)
        self.assertEqual(result.diagnostics["slice_measurement"]["radial_mad_sigma"], 3.0)

    def test_connected_component_stage_logs_reference_sphere_k_and_alpha(self):
        points = cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96)
        messages = []
        analyze_stem(
            points,
            StemDiameterParams(query_workers=1, component_knn_k=7, component_alpha=3.5),
            diagnostic_callback=messages.append,
        )
        start = next(message for message in messages if "[START] CONNECTED_COMPONENT" in message)
        success = next(message for message in messages if "[OK] CONNECTED_COMPONENT" in message)
        self.assertIn("K=7", start)
        self.assertIn("alpha=3.5", start)
        self.assertIn("K=7", success)
        self.assertIn("alpha=3.5", success)
        self.assertIn("median_knn_distance_mm=", success)
        self.assertIn("epsilon_mm=", success)

    def test_centerline_binning_failure_has_stage_counts_and_parameters(self):
        points = cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96)
        with self.assertRaises(StemDiameterStageError) as caught:
            self.analyze(points, use_largest_component=False, centerline_axis_mode="y",
                         min_centerline_bin_points=len(points) + 1)
        error = caught.exception
        self.assertEqual(error.stage, "CENTERLINE_BINNING")
        self.assertIn("valid support bins", str(error))
        self.assertEqual(error.diagnostics["valid_support_bins"], 0)
        self.assertEqual(error.diagnostics["required_support_points"], 4)
        self.assertIn("centerline_step_mm", error.parameters)

    def test_section_local_axis_rejections_do_not_stop_other_sections(self):
        points = cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96)
        result = self.analyze(points, min_local_axis_points=100_000)
        self.assertGreater(len(result.sections), 5)
        self.assertTrue(all(section.calculation_status == "insufficient_local_axis_points"
                            for section in result.sections))
        self.assertTrue(all(section.actual_local_axis_point_count < section.required_local_axis_point_count
                            for section in result.sections))

    def test_long_polyline_segment_emits_warning_diagnostic(self):
        points = cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96)
        points[(points[:, 1] >= -5.0) & (points[:, 1] < 0.0), 0] += 40.0
        warnings = []
        result = analyze_stem(
            points,
            StemDiameterParams(query_workers=1, centerline_axis_mode="y", centerline_model="polyline",
                               use_largest_component=False),
            diagnostic_callback=warnings.append,
        )
        self.assertGreater(result.diagnostics["centerline_model"]["max_median_segment_ratio"], 3.0)
        self.assertTrue(any("unusually long centerline segment detected" in item for item in result.warnings))
        self.assertTrue(any("[StemDiameter][WARNING]" in item for item in warnings))


if __name__ == "__main__":
    unittest.main()
