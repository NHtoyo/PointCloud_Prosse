import csv
import io
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from contextlib import redirect_stderr
from pathlib import Path
from unittest.mock import patch

import numpy as np

import pointcloud_io
import run_stem_diameter
from run_stem_diameter import _convert_points_to_mm, build_parser, main
from test_stem_diameter_algorithm import cylinder_points_mm


class StemDiameterCliTests(unittest.TestCase):
    def test_coordinate_conversion_uses_the_requested_factor(self):
        points_raw = np.array([[0.0, 0.0, 0.0], [0.0, 0.73837, 0.0]])
        points_mm = _convert_points_to_mm(points_raw, 1200.0)
        self.assertAlmostEqual(float(np.ptp(points_mm[:, 1])), 886.044, places=6)

    def test_ply_cli_writes_four_outputs_and_unity_json(self):
        points = cylinder_points_mm().astype(np.float32)
        colors = np.zeros_like(points, dtype=np.uint8)
        colors[:, 1] = 180

        with tempfile.TemporaryDirectory(prefix="stem-diameter-cli-") as temp_dir:
            root = Path(temp_dir)
            input_path = root / "synthetic_stem.ply"
            source_path = root / "original_plant.ply"
            output_dir = root / "synthetic_stem_stem_diameter"
            pointcloud_io.save_ply(str(input_path), points, colors)
            output_dir.mkdir()
            keep_file = output_dir / "user-notes.txt"
            keep_file.write_text("preserve this file", encoding="utf-8")

            output = io.StringIO()
            with redirect_stdout(output):
                code = main([
                    "--input", str(input_path),
                    "--output_dir", str(output_dir),
                    "--source-point-cloud-path", str(source_path),
                    "--source-loaded-point-count", str(len(points) + 2),
                    "--analysis-visible-point-count", str(len(points)),
                    "--analysis-visible-point-fingerprint", "sha256:" + "a" * 64,
                    "--coordinate-scale-to-mm", "1",
                    "--query-workers", "1",
                ])

            self.assertEqual(code, 0)
            self.assertIn("[StemDiameterInput] raw_xyz_span=", output.getvalue())
            self.assertIn("[StemDiameterInput] coordinate_scale_to_mm=1", output.getvalue())
            self.assertIn("[StemDiameterInput] mm_xyz_span=", output.getvalue())
            expected = {
                "stem_diameter.json", "stem_diameter.csv",
                "diameter_profile.png", "quality_profile.png", "user-notes.txt",
            }
            self.assertEqual({path.name for path in output_dir.iterdir()}, expected)
            self.assertEqual(keep_file.read_text(encoding="utf-8"), "preserve this file")
            payload = json.loads((output_dir / "stem_diameter.json").read_text(encoding="utf-8"))
            self.assertEqual(payload["schema_version"], 2)
            self.assertNotIn("scale_mm_per_unit", payload)
            self.assertEqual(payload["point_count"], len(points))
            self.assertEqual(payload["input_path"], str(input_path.resolve()))
            self.assertEqual(payload["source_point_cloud_path"], str(source_path.resolve()))
            self.assertEqual(payload["source_point_cloud_filename"], source_path.name)
            self.assertEqual(payload["source_loaded_point_count"], len(points) + 2)
            self.assertEqual(payload["analysis_visible_point_count"], len(points))
            self.assertEqual(payload["analysis_input_point_count"], len(points))
            self.assertEqual(payload["analysis_visible_point_fingerprint"], "sha256:" + "a" * 64)
            self.assertEqual(payload["centerline_axis_mode"], "pca")
            self.assertEqual(payload["centerline_model"], "polyline")
            self.assertTrue(payload["use_largest_component"])
            self.assertEqual(payload["component_knn_k"], 8)
            self.assertEqual(payload["component_alpha"], 2.5)
            self.assertEqual(payload["component_input_point_count"], len(points))
            self.assertEqual(payload["component_point_count"] + payload["component_removed_count"], len(points))
            self.assertGreater(payload["median_knn_distance_mm"], 0.0)
            self.assertGreater(payload["connectivity_epsilon_mm"], 0.0)
            self.assertIn("centerline_binning", payload["diagnostics"])
            self.assertIn("local_axis", payload["diagnostics"])
            self.assertIn("slice_measurement", payload["diagnostics"])
            self.assertAlmostEqual(payload["centerline_length_mm"], 160.0, delta=15.0)
            self.assertGreater(len(payload["sections"]), 10)
            self.assertEqual(len(payload["centerline"]["display_points_xyz_mm"][0]), 3)
            section = payload["sections"][len(payload["sections"]) // 2]
            self.assertIsInstance(section["center_xyz_mm"], dict)
            self.assertIsInstance(section["slice_results"][0]["contour_uv_mm"][0], dict)
            with (output_dir / "stem_diameter.csv").open(encoding="utf-8-sig") as csv_file:
                rows = list(csv.DictReader(csv_file))
            self.assertEqual(len(rows), len(payload["sections"]))
            for row, section in zip(rows, payload["sections"]):
                for field in ("position_mm", "equivalent_diameter_mm", "cross_section_area_mm2",
                              "diameter_3mm", "diameter_5mm", "diameter_7mm",
                              "slice_diameter_range_mm", "slice_diameter_std_mm"):
                    expected_value = section[field]
                    if expected_value is None:
                        self.assertEqual(row[field], "")
                    else:
                        self.assertAlmostEqual(float(row[field]), expected_value, places=10)
                primary_slice = next(item for item in section["slice_results"]
                                     if item["thickness_mm"] == 5.0)
                self.assertGreater(primary_slice["perimeter_mm"], 0.0)
                self.assertAlmostEqual(section["perimeter_mm"], primary_slice["perimeter_mm"], places=10)
                self.assertAlmostEqual(float(row["perimeter_mm"]), primary_slice["perimeter_mm"], places=10)
            self.assertGreater((output_dir / "diameter_profile.png").stat().st_size, 0)
            self.assertGreater((output_dir / "quality_profile.png").stat().st_size, 0)

    def test_raw_and_corrected_ply_are_scaled_before_analysis(self):
        display_scale = 1200.0
        correction_factor = 0.99
        original_points_mm = cylinder_points_mm().astype(np.float64)

        with tempfile.TemporaryDirectory(prefix="stem-diameter-scale-") as temp_dir:
            root = Path(temp_dir)
            inputs = (
                ("raw", original_points_mm / display_scale, 1.0),
                ("corrected", original_points_mm * correction_factor / display_scale, correction_factor),
            )
            measured_lengths = []

            for name, points_raw, expected_factor in inputs:
                input_path = root / f"synthetic_{name}.ply"
                output_dir = root / f"{name}_output"
                pointcloud_io.save_ply(
                    str(input_path), points_raw.astype(np.float32),
                    np.full(points_raw.shape, 128, dtype=np.uint8),
                )
                original_file_bytes = input_path.read_bytes()
                points_from_ply, _ = pointcloud_io.load_ply(str(input_path))
                expected_points_mm = np.asarray(points_from_ply, dtype=np.float64) * display_scale
                captured = {}
                real_analyze_stem = run_stem_diameter.analyze_stem

                def capture_mm_input(points_mm, params, progress_callback, diagnostic_callback=None):
                    captured["points_mm"] = np.array(points_mm, copy=True)
                    return real_analyze_stem(points_mm, params, progress_callback, diagnostic_callback)

                output = io.StringIO()
                with patch("run_stem_diameter.analyze_stem", side_effect=capture_mm_input):
                    with redirect_stdout(output):
                        code = main([
                            "--input", str(input_path),
                            "--output_dir", str(output_dir),
                            "--coordinate-scale-to-mm", str(display_scale),
                            "--query-workers", "1",
                        ])

                self.assertEqual(code, 0, output.getvalue())
                np.testing.assert_allclose(captured["points_mm"], expected_points_mm, rtol=0, atol=1e-6)
                self.assertIn("[StemDiameterInput] coordinate_scale_to_mm=1200", output.getvalue())
                self.assertIn("[StemDiameterInput] raw_xyz_span=", output.getvalue())
                self.assertIn("[StemDiameterInput] mm_xyz_span=", output.getvalue())
                self.assertEqual(input_path.read_bytes(), original_file_bytes)

                payload = json.loads((output_dir / "stem_diameter.json").read_text(encoding="utf-8"))
                self.assertGreaterEqual(len(payload["centerline"]["support_points_xyz_mm"]), 4)
                support = np.array([
                    [point["x"], point["y"], point["z"]]
                    for point in payload["centerline"]["support_points_xyz_mm"]
                ])
                support_steps = np.linalg.norm(np.diff(support, axis=0), axis=1)
                self.assertAlmostEqual(float(np.median(support_steps)), 5.0, delta=0.5)

                section_positions = np.array([section["position_mm"] for section in payload["sections"]])
                self.assertGreater(len(section_positions), 10)
                self.assertTrue(np.allclose(np.diff(section_positions), 10.0, atol=0.02))
                measured_lengths.append(payload["centerline_length_mm"])

            self.assertAlmostEqual(measured_lengths[1] / measured_lengths[0], correction_factor, delta=0.015)

    def test_coordinate_scale_argument_is_required(self):
        action = next(
            action for action in build_parser()._actions
            if "--coordinate-scale-to-mm" in action.option_strings
        )
        self.assertTrue(action.required)

    def test_visible_count_metadata_must_match_analysis_input(self):
        points = cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96).astype(np.float32)
        with tempfile.TemporaryDirectory(prefix="stem-diameter-count-metadata-") as temp_dir:
            root = Path(temp_dir)
            input_path = root / "stem.ply"
            output_dir = root / "out"
            pointcloud_io.save_ply(str(input_path), points, np.zeros_like(points, dtype=np.uint8))
            with redirect_stderr(io.StringIO()):
                code = main([
                    "--input", str(input_path), "--output_dir", str(output_dir),
                    "--coordinate-scale-to-mm", "1", "--query-workers", "1",
                    "--analysis-visible-point-count", str(len(points) - 1),
                ])
            self.assertEqual(code, 2)
            report = json.loads((output_dir / "stem_diameter_error.json").read_text(encoding="utf-8"))
            self.assertIn("analysis_visible_point_count must match", report["message"])

    def test_cli_model_and_component_options_are_saved(self):
        points = cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96).astype(np.float32)
        with tempfile.TemporaryDirectory(prefix="stem-diameter-options-") as temp_dir:
            root = Path(temp_dir)
            input_path = root / "stem.ply"
            output_dir = root / "out"
            pointcloud_io.save_ply(str(input_path), points, np.zeros_like(points, dtype=np.uint8))
            output = io.StringIO()
            with redirect_stdout(output):
                code = main([
                    "--input", str(input_path), "--output_dir", str(output_dir),
                    "--coordinate-scale-to-mm", "1", "--query-workers", "1",
                    "--centerline-axis", "y", "--centerline-model", "spline",
                    "--no-largest-component", "--component-knn-k", str(len(points) + 1),
                    "--component-alpha", "100", "--centerline-step-mm", "4",
                    "--min-centerline-bin-points", "20",
                ])
            self.assertEqual(code, 0, output.getvalue())
            payload = json.loads((output_dir / "stem_diameter.json").read_text(encoding="utf-8"))
            self.assertEqual(payload["centerline_axis_mode"], "y")
            self.assertEqual(payload["centerline_model"], "spline")
            self.assertFalse(payload["use_largest_component"])
            self.assertEqual(payload["component_knn_k"], len(points) + 1)
            self.assertEqual(payload["component_alpha"], 100.0)
            self.assertEqual(payload["component_point_count"], len(points))
            self.assertEqual(payload["component_removed_count"], 0)
            self.assertFalse(payload["diagnostics"]["connected_component"]["enabled"])
            self.assertEqual(payload["diagnostics"]["connected_component"]["kept_points"], len(points))
            self.assertEqual(payload["diagnostics"]["connected_component"]["removed_points"], 0)
            self.assertEqual(payload["parameters"]["centerline_step_mm"], 4.0)
            self.assertEqual(payload["parameters"]["min_centerline_bin_points"], 20)

    def test_fatal_stage_writes_failure_json_and_traceback(self):
        points = cylinder_points_mm(length_mm=80.0, axial_step_mm=1.0, angular_count=96).astype(np.float32)
        with tempfile.TemporaryDirectory(prefix="stem-diameter-failure-") as temp_dir:
            root = Path(temp_dir)
            input_path = root / "stem.ply"
            output_dir = root / "out"
            pointcloud_io.save_ply(str(input_path), points, np.zeros_like(points, dtype=np.uint8))
            stdout = io.StringIO()
            stderr = io.StringIO()
            with redirect_stdout(stdout), redirect_stderr(stderr):
                code = main([
                    "--input", str(input_path), "--output_dir", str(output_dir),
                    "--coordinate-scale-to-mm", "1", "--query-workers", "1",
                    "--centerline-axis", "y", "--no-largest-component",
                    "--min-centerline-bin-points", str(len(points) + 1),
                ])
            self.assertEqual(code, 2)
            self.assertIn("Traceback", stderr.getvalue())
            self.assertIn("CENTERLINE_BINNING", stdout.getvalue())
            report = json.loads((output_dir / "stem_diameter_error.json").read_text(encoding="utf-8"))
            self.assertEqual(report["stage"], "CENTERLINE_BINNING")
            self.assertEqual(report["diagnostics"]["valid_support_bins"], 0)
            self.assertEqual(report["diagnostics"]["required_support_points"], 4)
            self.assertEqual(report["parameters"]["centerline_axis_mode"], "y")

    def test_unity_uses_completed_mm_result_and_inverse_scales_overlays(self):
        project_root = Path(__file__).resolve().parents[2]
        ui_source = (project_root / "Assets/PointCloudWorkbench/Scripts/StemDiameterUI.cs").read_text(encoding="utf-8")
        input_export_source = (project_root / "Assets/PointCloudWorkbench/Scripts/StemDiameterInputExport.cs").read_text(encoding="utf-8")
        renderer_source = (project_root / "Assets/PointCloudRenderer.cs").read_text(encoding="utf-8")
        visualizer_source = (project_root / "Assets/PointCloudWorkbench/Scripts/StemDiameterVisualizer.cs").read_text(encoding="utf-8")
        result_source = (project_root / "Assets/PointCloudWorkbench/Scripts/StemDiameterResult.cs").read_text(encoding="utf-8")

        self.assertIn("JsonUtility.FromJson<StemDiameterResult>", ui_source)
        self.assertIn("StemDiameterInputExport.Create(currentPoints", ui_source)
        self.assertIn('"--input", Quote(activeInputExport.InputPath)', ui_source)
        self.assertNotIn('"--input", Quote(inputPath)', ui_source)
        self.assertIn("activeInputExport.ValidateComponentK(componentK);", ui_source)
        self.assertIn("StemDiameterInputExport.ValidatePythonPointCount(expectedPointCount, parsed.point_count);", ui_source)
        self.assertIn("loaded={activeInputExport.LoadedPointCount:N0}", ui_source)
        self.assertIn("visible={exported.VertexCount:N0} excluded={excludedPointCount:N0}", ui_source)
        self.assertIn("CleanupTemporaryInput();", ui_source)
        self.assertIn("OnPointCloudLoaded(string path)", ui_source)
        self.assertIn("StemDiameterResultCache.GetResultDirectory(pointCloudDataDirectory, inputPath)", ui_source)
        self.assertIn("LoadResultAsync(resultPath, CancellationToken.None, -1", ui_source)
        self.assertIn('"--source-point-cloud-path", Quote(runSourcePath)', ui_source)
        self.assertIn('"--source-loaded-point-count", activeInputExport.LoadedPointCount.ToString', ui_source)
        self.assertIn('"--analysis-visible-point-count", exported.VertexCount.ToString', ui_source)
        self.assertIn("loadGeneration != sourceGeneration", ui_source)
        self.assertIn("if (useLargestComponent) activeInputExport.ValidateComponentK(componentK);", ui_source)
        self.assertIn("現在の点群編集状態と解析時の点数が異なります。再解析を推奨します。", ui_source)
        self.assertIn("既存の茎径解析結果を読み込めませんでした。", ui_source)
        self.assertIn("ValidateResultPayload(parsed);", ui_source)
        self.assertIn("private void Start()", ui_source)
        self.assertIn("targetRenderer.GetPointData() == null", ui_source)
        self.assertIn("HandlePointCloudLoaded(loader.CurrentFilePath);", ui_source)
        self.assertIn("if (!startInitializationComplete)", ui_source)
        self.assertIn("hasPreStartPointCloudEvent", ui_source)
        self.assertIn("GUI.enabled = optionsEnabled && useLargestComponent;", ui_source)
        self.assertIn("analysis_visible_point_fingerprint", ui_source)
        self.assertIn('"--analysis-visible-point-fingerprint", Quote(visibleFingerprint.Fingerprint)', ui_source)
        self.assertIn("StemDiameterPointFingerprint.Compute(currentPoints, ExportPointMode.AllVisible", ui_source)
        self.assertIn("visualizer.SelectSection(selectedIndex);", ui_source)
        self.assertIn("visualizer.SetVisible(showOverlay);", ui_source)
        self.assertIn("ExportPointMode.AllVisible", input_export_source)
        self.assertIn('"stem_diameter_" + Guid.NewGuid().ToString("N")', input_export_source)
        self.assertIn("public void Cleanup()", input_export_source)
        self.assertIn("visualizer.SetResult(targetRenderer, result);", ui_source)
        self.assertIn("DrawGraph(chart);", ui_source)
        self.assertIn("targetRenderer.DisplayScale.ToString(\"R\", CultureInfo.InvariantCulture)", ui_source)
        self.assertIn("return lengthMillimeters / DisplayScale;", renderer_source)
        self.assertIn("return pointMillimeters / DisplayScale;", renderer_source)
        self.assertIn("overlayRoot.SetParent(targetRenderer.DisplayTransform, false);", visualizer_source)
        self.assertIn("targetRenderer.MillimetersToDataPoint(line[i].ToUnity())", visualizer_source)
        self.assertIn("targetRenderer.MillimetersToDataLength(axisHalfLengthMm)", visualizer_source)
        self.assertIn("targetRenderer.MillimetersToDataLength(point.u_mm)", visualizer_source)
        self.assertIn("public float perimeter_mm;", result_source)
        self.assertIn('DrawDiameterSeries(plot, min, max, "diameter_5mm"', ui_source)
        self.assertNotIn('DrawDiameterSeries(plot, min, max, "diameter_3mm"', ui_source)
        self.assertIn("周長：", ui_source)
        self.assertIn("等価茎径：", ui_source)

        result_mm = np.array([1200.0, -600.0, 75.0])
        local_data = result_mm / 1200.0
        np.testing.assert_allclose(local_data * 1200.0, result_mm)


if __name__ == "__main__":
    unittest.main()
