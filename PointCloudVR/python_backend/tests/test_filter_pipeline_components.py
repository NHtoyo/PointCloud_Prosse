import json
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np

from filter_pipeline import FilterPipeline, FilterStep
from pointcloud_components import ConnectedComponentError
from result_writer import write_results
import run_noise_filter


def make_bridge_cloud():
    left = np.column_stack((np.arange(8) * 0.1, np.zeros(8), np.zeros(8)))
    bridge = np.array([[0.9, 0.0, 0.0]])
    right = np.column_stack((1.1 + np.arange(8) * 0.1, np.zeros(8), np.zeros(8)))
    return np.vstack((left, bridge, right)).astype(np.float32)


def sor_result(points, remove_indices=()):
    mask = np.zeros(len(points), dtype=bool)
    mask[list(remove_indices)] = True
    return {"remove_mask": mask, "sor_score": np.zeros(len(points), dtype=np.float32)}


class FilterPipelineComponentTests(unittest.TestCase):
    def test_prior_exclusion_cannot_bridge_and_component_exclusion_reaches_next_step(self):
        points = make_bridge_cloud()
        calls = []

        def fake_sor(active_points, tree, **kwargs):
            calls.append(np.asarray(active_points).copy())
            if len(calls) == 1:
                return sor_result(active_points, [8])
            return sor_result(active_points)

        pipeline = FilterPipeline()
        pipeline.add_step(FilterStep("sor", fake_sor, {}, True, True))
        pipeline.add_step(FilterStep(
            "component_keep", None, {"knn_k": 1, "alpha": 2.5, "mode": "largest"}, True, True
        ))
        pipeline.add_step(FilterStep("sor", fake_sor, {}, True, False))

        result = pipeline.run(points)
        self.assertEqual(len(calls), 2)
        self.assertEqual(len(calls[0]), 17)
        self.assertEqual(len(calls[1]), 8)
        self.assertEqual(result["component_keep_runs"][0]["input_point_count"], 16)
        self.assertEqual(result["component_keep_runs"][0]["component_count"], 2)
        self.assertEqual(result["removed_by_component_keep_count"], 8)
        self.assertEqual(int(np.count_nonzero(result["remove_mask"])), 9)
        self.assertEqual(int(result["reason"][8]), 1)
        self.assertTrue(np.all(result["reason"][9:] == 8))

    def test_selected_mask_uses_original_indices_after_deleted_mask(self):
        points = make_bridge_cloud()
        selection = np.zeros(len(points), dtype=bool)
        selection[12] = True
        deleted = np.zeros(len(points), dtype=bool)
        deleted[8] = True
        received = []

        def fake_sor(active_points, tree, **kwargs):
            received.append(np.asarray(active_points).copy())
            return sor_result(active_points)

        pipeline = FilterPipeline()
        pipeline.add_step(FilterStep(
            "component_keep", None, {"knn_k": 1, "alpha": 2.5, "mode": "selected"}, True, True
        ))
        pipeline.add_step(FilterStep("sor", fake_sor, {}, True, False))
        result = pipeline.run(points, deleted_mask=deleted, selection_mask=selection)
        self.assertEqual(len(received[0]), 8)
        np.testing.assert_allclose(received[0], points[9:])
        self.assertTrue(np.all(result["remove_mask"][:8]))
        self.assertFalse(np.any(result["remove_mask"][9:]))
        self.assertEqual(int(result["remove_mask"][8]), 0)

    def test_selection_removed_by_previous_stage_fails_instead_of_largest_fallback(self):
        points = make_bridge_cloud()
        selection = np.zeros(len(points), dtype=bool)
        selection[8] = True

        def remove_bridge(active_points, tree, **kwargs):
            return sor_result(active_points, [8])

        pipeline = FilterPipeline()
        pipeline.add_step(FilterStep("sor", remove_bridge, {}, True, True))
        pipeline.add_step(FilterStep(
            "component_keep", None, {"knn_k": 1, "alpha": 2.5, "mode": "selected"}, True, True
        ))
        with self.assertRaisesRegex(ConnectedComponentError, "no active selected points"):
            pipeline.run(points, selection_mask=selection)

    def test_selection_hidden_by_initial_deleted_mask_fails(self):
        points = make_bridge_cloud()
        selection = np.zeros(len(points), dtype=bool)
        selection[8] = True
        deleted = np.ones(len(points), dtype=bool)
        pipeline = FilterPipeline()
        pipeline.add_step(FilterStep(
            "component_keep", None, {"knn_k": 1, "alpha": 2.5, "mode": "selected"}, True, True
        ))
        with self.assertRaisesRegex(ConnectedComponentError, "no active selected points"):
            pipeline.run(points, deleted_mask=deleted, selection_mask=selection)

    def test_all_preexisting_deleted_points_are_excluded(self):
        points = np.vstack((make_bridge_cloud(), [[100.0, 0.0, 0.0]])).astype(np.float32)
        deleted = np.zeros(len(points), dtype=bool)
        deleted[8] = True
        deleted[-1] = True
        captured = []

        def fake_sor(active_points, tree, **kwargs):
            captured.append(np.asarray(active_points).copy())
            return sor_result(active_points)

        pipeline = FilterPipeline()
        pipeline.add_step(FilterStep(
            "component_keep", None, {"knn_k": 1, "alpha": 2.5, "mode": "largest"}, True, True
        ))
        pipeline.add_step(FilterStep("sor", fake_sor, {}, True, False))
        pipeline.run(points, deleted_mask=deleted)
        self.assertEqual(len(captured[0]), 8)
        self.assertFalse(np.any(np.all(captured[0] == points[-1], axis=1)))

    def test_missing_deleted_mask_fails_closed_in_full_mode(self):
        points = make_bridge_cloud()
        with tempfile.TemporaryDirectory() as directory:
            args = SimpleNamespace(
                deleted_mask=str(Path(directory) / "missing-mask.bin"),
                selection_mask=None,
                output_dir=str(Path(directory) / "output"),
                coordinate_scale_to_mm=1.0,
                operation_id="missing-mask-test",
                input="input.ply",
            )
            with self.assertRaisesRegex(FileNotFoundError, "deleted_maskが見つかりません"):
                run_noise_filter.run_full_mode(
                    points, None, {}, set(), FilterPipeline(), args, len(points)
                )

    def test_same_component_filter_can_be_repeated(self):
        points = np.vstack((make_bridge_cloud()[:8], make_bridge_cloud()[9:])).astype(np.float32)
        pipeline = FilterPipeline()
        for _ in range(2):
            pipeline.add_step(FilterStep(
                "component_keep", None, {"knn_k": 1, "alpha": 2.5, "mode": "largest"}, True, True
            ))
        result = pipeline.run(points)
        self.assertEqual(len(result["component_keep_runs"]), 2)
        self.assertEqual(result["component_keep_runs"][0]["kept_point_count"], 8)
        self.assertEqual(result["component_keep_runs"][1]["removed_point_count"], 0)

    def test_json_config_defaults_new_filter_to_exclude_following_steps(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "pipeline.json"
            path.write_text(json.dumps({"steps": [{"name": "component_keep", "enabled": True,
                "params": {"knn_k": 8, "alpha": 2.5, "mode": "largest"}}]}), encoding="utf-8")
            pipeline, params, enabled = run_noise_filter.build_pipeline_from_json(str(path))
        self.assertTrue(pipeline.steps[0].exclude_from_next)
        self.assertEqual(pipeline.steps[0].params["mode"], "largest")
        self.assertIn("component_keep", enabled)
        self.assertEqual(params["component_keep"]["knn_k"], 8)

    def test_downsample_mode_rejects_exact_component_filter(self):
        pipeline = FilterPipeline()
        pipeline.add_step(FilterStep("component_keep", None, {}, True, True))
        args = SimpleNamespace()
        with self.assertRaisesRegex(ValueError, "Fullモード"):
            run_noise_filter.run_downsample_mode(
                np.zeros((8, 3)), None, {}, set(), pipeline, args, original_count=8
            )

    def test_result_metadata_records_component_run_statistics(self):
        n = 4
        result = {
            "remove_mask": np.array([0, 1, 1, 0], dtype=np.uint8),
            "preview_mask": np.array([0, 1, 1, 0], dtype=np.uint8),
            "white_haze_candidate_mask": np.zeros(n, dtype=np.uint8),
            "preview_reason": np.array([0, 8, 8, 0], dtype=np.int32),
            "reason": np.array([0, 8, 8, 0], dtype=np.int32),
            "dbscan_mode": "none", "dbscan_voxel_size": None, "dbscan_analysis_count": n,
            "sor_score": np.zeros(n, dtype=np.float32), "density_score": np.zeros(n, dtype=np.float32),
            "radius_neighbor_count": np.zeros(n, dtype=np.int32), "cc_noise_score": np.zeros(n, dtype=np.float32),
            "white_haze_score": np.zeros(n, dtype=np.float32), "cluster_id": np.full(n, -1, dtype=np.int32),
            "dbscan_timeout": False, "removed_by_component_keep_count": 2,
            "component_keep_runs": [{"knn_k": 8, "alpha": 2.5, "mode": "largest", "edge_count": 6}],
            "removed_by_sor_count": 0, "removed_by_ror_count": 0, "removed_by_low_density_count": 0,
            "removed_by_small_cluster_count": 0, "removed_by_cc_noise_count": 0,
            "removed_by_white_haze_count": 0, "white_haze_candidate_count": 0,
        }
        with tempfile.TemporaryDirectory() as directory:
            published = write_results(directory, result, {}, "full", n, n, operation_id="component-test")
            generation = Path(published["generation_directory"])
            metadata = json.loads((generation / "metadata.json").read_text(encoding="utf-8"))
            report = json.loads((generation / "removal_report.json").read_text(encoding="utf-8"))
        self.assertEqual(metadata["component_keep_runs"][0]["edge_count"], 6)
        self.assertEqual(report["removed_by_component_keep"], 2)


if __name__ == "__main__":
    unittest.main()
