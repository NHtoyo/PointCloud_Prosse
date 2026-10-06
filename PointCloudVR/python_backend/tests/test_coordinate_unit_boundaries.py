import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np

import pointcloud_io
import run_noise_filter
import run_support_cylinder
from coordinate_units import (
    convert_points_from_mm_to_data,
    convert_points_to_mm_in_place,
    millimeters_to_data_length,
)


def load_downsample_module():
    path = Path(__file__).resolve().parents[1] / "2_downsample.py"
    spec = importlib.util.spec_from_file_location("pointcloud_downsample", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def write_calibrated_labeled_ply(path: Path) -> None:
    path.write_text(
        "ply\n"
        "format ascii 1.0\n"
        "comment pcwb_scale_calibrated true\n"
        "element vertex 4\n"
        "property float x\n"
        "property float y\n"
        "property float z\n"
        "property uchar red\n"
        "property uchar green\n"
        "property uchar blue\n"
        "property int label\n"
        "end_header\n"
        "0 0 0 128 128 128 0\n"
        "0.001 0 0 128 128 128 0\n"
        "0.003 0 0 128 128 128 0\n"
        "0.006 0 0 128 128 128 0\n",
        encoding="ascii",
    )


class CoordinateBoundaryTests(unittest.TestCase):
    def test_mm_data_conversions_follow_each_display_scale(self):
        for scale, expected_radius in ((1200.0, 50.0 / 1200.0), (600.0, 50.0 / 600.0)):
            with self.subTest(scale=scale):
                self.assertAlmostEqual(millimeters_to_data_length(50.0, scale), expected_radius)

    def test_noise_runner_converts_raw_coordinates_at_its_boundary(self):
        points_raw = np.array([[0, 0, 0], [0, 0.5, 0]], dtype=np.float32)
        colors = np.zeros_like(points_raw, dtype=np.uint8)
        captured = {}
        original_run_full = run_noise_filter.run_full_mode

        def capture_pipeline_input(*args):
            points_mm = args[0]
            count = args[-1]
            captured["span"] = float(np.ptp(points_mm[:, 1]))
            captured["count"] = count
            return original_run_full(*args)

        with tempfile.TemporaryDirectory(prefix="noise-units-") as temp_dir:
            root = Path(temp_dir)
            input_path = root / "input.ply"
            pointcloud_io.save_ply(str(input_path), points_raw, colors)

            for scale, expected_span in ((1200.0, 600.0), (600.0, 300.0)):
                with self.subTest(scale=scale), patch.object(
                    run_noise_filter, "run_full_mode", side_effect=capture_pipeline_input
                ), patch.object(run_noise_filter, "print_summary"), contextlib.redirect_stdout(io.StringIO()):
                    captured.clear()
                    result = run_noise_filter.main([
                        "--input", str(input_path),
                        "--output_dir", str(root / "noise-output"),
                        "--coordinate-scale-to-mm", str(scale),
                        "--filters", "none",
                    ])
                    self.assertIsNone(result)
                    self.assertAlmostEqual(captured["span"], expected_span, places=4)
                    self.assertEqual(captured["count"], 2)
                    metadata = json.loads((root / "noise-output" / "metadata.json").read_text(encoding="utf-8"))
                    self.assertEqual(metadata["coordinate_unit"], "mm")
                    self.assertEqual(metadata["coordinate_scale_to_mm"], scale)
                    self.assertEqual(metadata["scalar_units"]["density_score"], "1/mm")

    def test_noise_preview_can_be_written_back_in_data_space(self):
        points_raw = np.array([[0, 0, 0], [0, 0.5, 0]], dtype=np.float32)
        points_mm = convert_points_to_mm_in_place(points_raw.copy(), 1200.0)
        points_data = convert_points_from_mm_to_data(points_mm, 1200.0)
        np.testing.assert_allclose(points_data, points_raw, rtol=1e-6, atol=1e-7)

    def test_noise_downsample_preview_ply_keeps_data_space_coordinates(self):
        points_raw = np.array([[0, 0, 0], [0, 0.5, 0], [0, 1.0, 0], [0, 1.5, 0]], dtype=np.float32)
        colors = np.zeros_like(points_raw, dtype=np.uint8)
        with tempfile.TemporaryDirectory(prefix="noise-preview-units-") as temp_dir:
            root = Path(temp_dir)
            input_path = root / "input.ply"
            output_dir = root / "noise-output"
            pointcloud_io.save_ply(str(input_path), points_raw, colors)
            with contextlib.redirect_stdout(io.StringIO()):
                run_noise_filter.main([
                    "--input", str(input_path),
                    "--output_dir", str(output_dir),
                    "--coordinate-scale-to-mm", "1200",
                    "--mode", "downsample",
                    "--voxel_size", "1000",
                    "--filters", "none",
                ])

            preview = np.asarray(run_noise_filter.o3d.io.read_point_cloud(str(output_dir / "preview.ply")).points)
            self.assertGreater(float(np.ptp(preview[:, 1])), 0.0)
            self.assertLess(float(np.max(np.abs(preview))), 1.6)

    def test_support_runner_passes_mm_coordinates_to_the_algorithm(self):
        points_raw = np.array([[0, 0, 0], [0, 0.001, 0], [0, 0.002, 0]], dtype=np.float32)
        colors = np.full((3, 3), 120, dtype=np.uint8)
        captured = {}

        def capture_support(points_mm, _colors, seed_indices, _params):
            captured["span"] = float(np.ptp(points_mm[:, 1]))
            captured["seed_count"] = len(seed_indices)
            return np.zeros(len(points_mm), dtype=np.uint8), {
                "point_count": len(points_mm), "seed_count": len(seed_indices),
                "candidate_count": 0, "selected_count": 0, "spacing": 1.2,
                "tube_radius": 2.0,
            }

        with tempfile.TemporaryDirectory(prefix="support-units-") as temp_dir:
            root = Path(temp_dir)
            input_path = root / "input.ply"
            pointcloud_io.save_ply(str(input_path), points_raw, colors)
            seed_path = root / "seed.bin"
            np.array([0, 1, 2], dtype="<i4").tofile(seed_path)
            with patch.object(run_support_cylinder, "extract_support_mask", side_effect=capture_support), contextlib.redirect_stdout(io.StringIO()):
                code = run_support_cylinder.main([
                    "--input", str(input_path),
                    "--seed_indices", str(seed_path),
                    "--output_dir", str(root / "support-output"),
                    "--coordinate-scale-to-mm", "1200",
                ])

        self.assertEqual(code, 0)
        self.assertAlmostEqual(captured["span"], 2.4, places=5)
        self.assertEqual(captured["seed_count"], 3)

    def test_standalone_downsample_uses_data_voxel_and_keeps_calibration_marker(self):
        downsample = load_downsample_module()
        with tempfile.TemporaryDirectory(prefix="downsample-units-") as temp_dir:
            root = Path(temp_dir)
            input_path = root / "source_labeled.ply"
            output_path = root / "exact_result_ds5mm.ply"
            output_dir = root / "reports"
            write_calibrated_labeled_ply(input_path)

            with contextlib.redirect_stdout(io.StringIO()):
                downsample.main([
                    "--input", str(input_path),
                    "--output", str(output_dir),
                    "--mode", "1",
                    "--voxel_size", "5",
                    "--coordinate-scale-to-mm", "1200",
                    "--merged-output", str(output_path),
                ])

            self.assertTrue(output_path.is_file())
            self.assertTrue(downsample.has_calibrated_metadata(output_path))
            loaded = downsample.o3d.io.read_point_cloud(str(output_path))
            output_points = np.asarray(loaded.points)
            self.assertLess(float(np.max(np.abs(output_points))), 0.01)
            self.assertGreater(float(np.ptp(output_points[:, 0])), 0.0)
            report_path = output_dir / "source_labeled_downsample_runlog.json"
            report = json.loads(report_path.read_text(encoding="utf-8"))
            self.assertAlmostEqual(report["voxel"]["voxel_size_data"], 5.0 / 1200.0)
            self.assertEqual(report["voxel"]["coordinate_basis"], "data-space")
            self.assertTrue(report["voxel"]["source_scale_calibrated"])


if __name__ == "__main__":
    unittest.main()
