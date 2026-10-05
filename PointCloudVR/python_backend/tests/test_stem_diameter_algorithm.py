import inspect
import math
import unittest
from pathlib import Path

import numpy as np

from stem_diameter_algorithm import StemDiameterParams, analyze_stem


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
                       remove_sector_deg=0.0, curved=False):
    length_mm = 160.0
    axial = np.arange(-length_mm / 2, length_mm / 2 + 0.1, 0.75)
    angles = np.linspace(0.0, 2.0 * np.pi, 128, endpoint=False)
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
        return analyze_stem(points_mm / 1000.0, 1000.0, params)

    def test_algorithm_has_no_io_or_unity_dependencies(self):
        source = Path(inspect.getsourcefile(analyze_stem)).read_text(encoding="utf-8")
        for forbidden in ("open3d", "matplotlib", "UnityEngine", "subprocess", "pointcloud_io"):
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


if __name__ == "__main__":
    unittest.main()
