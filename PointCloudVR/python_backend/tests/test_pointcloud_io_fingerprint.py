import unittest

import numpy as np

import pointcloud_io


class PointCloudFingerprintTests(unittest.TestCase):
    def test_fingerprint_uses_ordered_little_endian_float32_xyz(self):
        points = np.array([[1.0, -2.5, 3.25], [4.5, 5.0, -6.0]], dtype=np.float64)
        expected = np.asarray(points, dtype="<f4", order="C").tobytes()
        import hashlib
        self.assertEqual(pointcloud_io.xyz_sha256(points), hashlib.sha256(expected).hexdigest())
        self.assertEqual(pointcloud_io.validate_xyz_sha256(points, pointcloud_io.xyz_sha256(points)),
                         pointcloud_io.xyz_sha256(points))

    def test_reordered_or_changed_coordinates_do_not_match(self):
        points = np.array([[0.1, 0.2, 0.3], [1.1, 1.2, 1.3]], dtype=np.float32)
        fingerprint = pointcloud_io.xyz_sha256(points)
        with self.assertRaises(ValueError):
            pointcloud_io.validate_xyz_sha256(points[::-1], fingerprint)
        changed = points.copy()
        changed[1, 2] += 0.001
        with self.assertRaises(ValueError):
            pointcloud_io.validate_xyz_sha256(changed, fingerprint)


if __name__ == "__main__":
    unittest.main()
