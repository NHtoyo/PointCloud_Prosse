import json
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path

import numpy as np

import pointcloud_io
from run_reference_sphere import main
from test_reference_sphere_algorithm import fibonacci_sphere


class ReferenceSphereCliTests(unittest.TestCase):
    def test_ply_cli_writes_data_space_result_json(self):
        center = np.array([0.12, -0.74, 0.03], dtype=np.float64)
        points = fibonacci_sphere(count=1000, center=center, radius=0.0258).astype(np.float32)
        colors = np.full(points.shape, 160, dtype=np.uint8)

        with tempfile.TemporaryDirectory(prefix="reference-sphere-cli-") as temp_dir:
            input_path = Path(temp_dir) / "selected_reference_sphere.ply"
            output_path = Path(temp_dir) / "result.json"
            pointcloud_io.save_ply(str(input_path), points, colors)
            output = StringIO()
            with redirect_stdout(output):
                code = main([
                    "--input", str(input_path),
                    "--output", str(output_path),
                    "--knn-k", "8",
                    "--connectivity-alpha", "2.5",
                ])

            self.assertEqual(code, 0, output.getvalue())
            self.assertIn("[Progress]", output.getvalue())
            result = json.loads(output_path.read_text(encoding="utf-8"))
            self.assertEqual(result["method_name"], "maalek_lichti_reproduction")
            self.assertEqual(result["input_point_count"], len(points))
            self.assertEqual(result["component_point_count"], len(points))
            self.assertEqual(result["knn_k"], 8)
            self.assertAlmostEqual(result["connectivity_alpha"], 2.5)
            self.assertIn("fit_inlier_count", result)
            self.assertNotIn("maalek_inlier_count", result)
            np.testing.assert_allclose(result["center"], center, atol=2e-6)
            self.assertAlmostEqual(result["diameter"], 2 * result["radius"], places=10)

    def test_cli_failure_does_not_write_result_json(self):
        points = fibonacci_sphere(count=40).astype(np.float32)
        with tempfile.TemporaryDirectory(prefix="reference-sphere-cli-error-") as temp_dir:
            input_path = Path(temp_dir) / "selected.ply"
            output_path = Path(temp_dir) / "result.json"
            pointcloud_io.save_ply(str(input_path), points, np.zeros(points.shape, dtype=np.uint8))
            with redirect_stdout(StringIO()):
                code = main(["--input", str(input_path), "--output", str(output_path), "--knn-k", "40"])
            self.assertEqual(code, 2)
            self.assertFalse(output_path.exists())

    def test_npz_cli_uses_same_data_space_algorithm(self):
        points = fibonacci_sphere(count=700, center=(0.3, -0.2, 0.1), radius=0.04).astype(np.float32)
        with tempfile.TemporaryDirectory(prefix="reference-sphere-npz-") as temp_dir:
            input_path = Path(temp_dir) / "selected.npz"
            output_path = Path(temp_dir) / "result.json"
            pointcloud_io.save_npz(str(input_path), points, np.zeros(points.shape, dtype=np.uint8))
            with redirect_stdout(StringIO()):
                code = main(["--input", str(input_path), "--output", str(output_path)])
            self.assertEqual(code, 0)
            result = json.loads(output_path.read_text(encoding="utf-8"))
            np.testing.assert_allclose(result["center"], [0.3, -0.2, 0.1], atol=2e-6)
            self.assertAlmostEqual(result["radius"], 0.04, delta=2e-6)


if __name__ == "__main__":
    unittest.main()
