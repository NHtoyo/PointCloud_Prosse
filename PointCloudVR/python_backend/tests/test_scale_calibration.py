import unittest

from scale_calibration import calculate_correction


class CoordinateCorrectionTests(unittest.TestCase):
    def test_reference_diameter_correction(self):
        self.assertAlmostEqual(calculate_correction(59.4, [60.0]), 0.99)

    def test_multiple_measurements_use_median(self):
        self.assertAlmostEqual(calculate_correction(59.4, [60.0, 59.0, 600.0]), 0.99)

    def test_rejects_nonpositive_or_nonfinite_measurement(self):
        for values in ([0.0], [-0.1], [float("nan")]):
            with self.subTest(values=values), self.assertRaises(ValueError):
                calculate_correction(60.0, values)

    def test_rejects_empty_measurements(self):
        with self.assertRaises(ValueError):
            calculate_correction(60.0, [])


if __name__ == "__main__":
    unittest.main()
