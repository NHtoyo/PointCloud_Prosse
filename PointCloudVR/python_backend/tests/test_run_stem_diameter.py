import json
import tempfile
import unittest
from pathlib import Path

import numpy as np

import pointcloud_io
from run_stem_diameter import main
from test_stem_diameter_algorithm import cylinder_points_mm


class StemDiameterCliTests(unittest.TestCase):
    def test_ply_cli_writes_four_outputs_and_unity_json(self):
        points = cylinder_points_mm().astype(np.float32)
        colors = np.zeros_like(points, dtype=np.uint8)
        colors[:, 1] = 180

        with tempfile.TemporaryDirectory(prefix="stem-diameter-cli-") as temp_dir:
            root = Path(temp_dir)
            input_path = root / "synthetic_stem.ply"
            output_dir = root / "synthetic_stem_stem_diameter"
            pointcloud_io.save_ply(str(input_path), points, colors)
            output_dir.mkdir()
            keep_file = output_dir / "user-notes.txt"
            keep_file.write_text("preserve this file", encoding="utf-8")

            code = main([
                "--input", str(input_path),
                "--output_dir", str(output_dir),
                "--query-workers", "1",
            ])

            self.assertEqual(code, 0)
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
            self.assertGreater(len(payload["sections"]), 10)
            self.assertEqual(len(payload["centerline"]["display_points_xyz_mm"][0]), 3)
            section = payload["sections"][len(payload["sections"]) // 2]
            self.assertIsInstance(section["center_xyz_mm"], dict)
            self.assertIsInstance(section["slice_results"][0]["contour_uv_mm"][0], dict)


if __name__ == "__main__":
    unittest.main()
