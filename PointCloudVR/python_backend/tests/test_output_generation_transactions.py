from __future__ import annotations

import hashlib
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import patch

import numpy as np

import output_generations
import result_writer


def _noise_result(point_count: int) -> dict:
    zeros_u8 = np.zeros(point_count, dtype=np.uint8)
    zeros_f32 = np.zeros(point_count, dtype=np.float32)
    zeros_i32 = np.zeros(point_count, dtype=np.int32)
    return {
        "remove_mask": zeros_u8.copy(),
        "preview_mask": zeros_u8.copy(),
        "white_haze_candidate_mask": zeros_u8.copy(),
        "preview_reason": zeros_i32.copy(),
        "sor_score": zeros_f32.copy(),
        "density_score": zeros_f32.copy(),
        "radius_neighbor_count": zeros_i32.copy(),
        "cc_noise_score": zeros_f32.copy(),
        "white_haze_score": zeros_f32.copy(),
        "cluster_id": np.full(point_count, -1, dtype=np.int32),
        "reason": zeros_i32.copy(),
        "dbscan_mode": "none",
        "dbscan_voxel_size": None,
        "dbscan_analysis_count": point_count,
        "removed_by_sor_count": 0,
        "removed_by_ror_count": 0,
        "removed_by_low_density_count": 0,
        "removed_by_cc_noise_count": 0,
        "removed_by_small_cluster_count": 0,
        "removed_by_white_haze_count": 0,
        "white_haze_candidate_count": 0,
        "dbscan_timeout": False,
    }


class OutputGenerationTransactionTests(unittest.TestCase):
    def _publish_noise(self, root: Path, run_id: str):
        return result_writer.write_results(
            str(root), _noise_result(32), {"filters": []}, "full", 32, 32,
            coordinate_scale_to_mm=1200.0, operation_id=run_id,
            source_path=root / "source.ply",
        )

    def test_mid_write_failure_keeps_previous_noise_generation_intact(self):
        with tempfile.TemporaryDirectory(prefix="noise-generation-transaction-") as temporary:
            root = Path(temporary)
            first = self._publish_noise(root, "noise_ok_1")
            pointer_before = (root / "current_run.json").read_bytes()
            generation = Path(first["generation_directory"])
            manifest = output_generations.resolve_current_generation(root)["manifest"]
            old_files = {
                artifact["name"]: (generation / artifact["name"]).read_bytes()
                for artifact in manifest["artifacts"]
            }

            real_write = result_writer._write_array
            calls = 0

            def fail_after_partial_write(path, array, dtype):
                nonlocal calls
                calls += 1
                if calls == 3:
                    raise OSError("injected mid-generation write failure")
                return real_write(path, array, dtype)

            with patch.object(result_writer, "_write_array", side_effect=fail_after_partial_write):
                with self.assertRaisesRegex(OSError, "injected mid-generation"):
                    self._publish_noise(root, "noise_failed_1")

            self.assertEqual((root / "current_run.json").read_bytes(), pointer_before)
            self.assertEqual(output_generations.resolve_current_generation(root)["run_id"], "noise_ok_1")
            for name, content in old_files.items():
                self.assertEqual((generation / name).read_bytes(), content)
            self.assertEqual(list(root.glob(".staging-*")), [])

    def test_first_array_metadata_and_report_failures_keep_previous_result_set(self):
        with tempfile.TemporaryDirectory(prefix="noise-generation-stage-failures-") as temporary:
            root = Path(temporary)
            first = self._publish_noise(root, "stage_ok_1")
            pointer_before = (root / "current_run.json").read_bytes()
            generation = Path(first["generation_directory"])
            manifest = output_generations.resolve_current_generation(root)["manifest"]
            old_hashes = {
                artifact["name"]: hashlib.sha256((generation / artifact["name"]).read_bytes()).hexdigest()
                for artifact in manifest["artifacts"]
            }

            failures = (
                ("_write_array", OSError("injected first binary write failure")),
                ("_write_metadata", OSError("injected metadata failure")),
                ("_write_removal_report", OSError("injected report failure")),
            )
            for index, (function_name, failure) in enumerate(failures):
                with self.subTest(function=function_name), patch.object(
                    result_writer, function_name, side_effect=failure
                ):
                    with self.assertRaises(OSError):
                        self._publish_noise(root, f"stage_failed_{index}")
                self.assertEqual((root / "current_run.json").read_bytes(), pointer_before)
                self.assertEqual(output_generations.resolve_current_generation(root)["run_id"], "stage_ok_1")
                for filename, digest in old_hashes.items():
                    self.assertEqual(hashlib.sha256((generation / filename).read_bytes()).hexdigest(), digest)
                self.assertEqual(list(root.glob(".staging-*")), [])

    def test_pointer_replace_failure_leaves_previous_generation_current(self):
        with tempfile.TemporaryDirectory(prefix="noise-generation-pointer-") as temporary:
            root = Path(temporary)
            self._publish_noise(root, "pointer_ok_1")
            previous_pointer = (root / "current_run.json").read_bytes()

            with patch.object(output_generations, "_replace_pointer_file",
                              side_effect=OSError("injected pointer replace failure")):
                with self.assertRaisesRegex(OSError, "injected pointer replace failure"):
                    self._publish_noise(root, "pointer_failed_1")

            self.assertEqual((root / "current_run.json").read_bytes(), previous_pointer)
            self.assertEqual(output_generations.resolve_current_generation(root)["run_id"], "pointer_ok_1")
            self.assertTrue((root / "runs" / "pointer_failed_1" / "manifest.json").is_file())
            self.assertEqual(list(root.glob(".current-run-*.tmp")), [])

            retried = self._publish_noise(root, "pointer_retry_1")
            current = output_generations.resolve_current_generation(root)
            self.assertEqual(current["run_id"], "pointer_retry_1")
            self.assertEqual(Path(retried["generation_directory"]), current["generation_directory"])
            self.assertEqual(output_generations.resolve_current_generation(root)["run_id"], "pointer_retry_1")
            self.assertTrue((root / "runs" / "pointer_ok_1" / "manifest.json").is_file())
            self.assertEqual(list(root.glob(".current-run-*.tmp")), [])

    def test_concurrent_writers_publish_complete_isolated_generations(self):
        with tempfile.TemporaryDirectory(prefix="noise-generation-concurrent-") as temporary:
            root = Path(temporary)
            with ThreadPoolExecutor(max_workers=2) as executor:
                results = list(executor.map(
                    lambda run_id: self._publish_noise(root, run_id),
                    ("concurrent_a", "concurrent_b"),
                ))

            self.assertEqual({Path(item["generation_directory"]).name for item in results},
                             {"concurrent_a", "concurrent_b"})
            current = output_generations.resolve_current_generation(root)
            self.assertIn(current["run_id"], {"concurrent_a", "concurrent_b"})
            for run_id in ("concurrent_a", "concurrent_b"):
                generation = root / "runs" / run_id
                manifest = (generation / "manifest.json").read_bytes()
                self.assertTrue(manifest)
                self.assertEqual(output_generations.resolve_current_generation(root)["run_id"], current["run_id"])
                for artifact in current["manifest"]["artifacts"] if run_id == current["run_id"] else []:
                    digest = hashlib.sha256((generation / artifact["name"]).read_bytes()).hexdigest()
                    self.assertEqual(digest, artifact["sha256"])


if __name__ == "__main__":
    unittest.main()
