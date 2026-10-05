"""Calculate a one-time coordinate correction from a known physical diameter."""

import argparse
import math
import statistics
import sys


def calculate_correction(real_diameter_mm: float, measurements: list[float]) -> float:
    if not math.isfinite(real_diameter_mm) or real_diameter_mm <= 0:
        raise ValueError("real_diameter_mm must be finite and positive")
    if not measurements or any(not math.isfinite(value) or value <= 0 for value in measurements):
        raise ValueError("every measured diameter must be finite and positive")
    return real_diameter_mm / statistics.median(measurements)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Calculate a one-time coordinate correction; does not write a scale report."
    )
    parser.add_argument("--real_diameter_mm", type=float, required=True)
    parser.add_argument("--measurements", type=str, required=True,
                        help="Comma-separated point-cloud measurements of the same diameter")
    args = parser.parse_args()

    try:
        measurements = [float(value.strip()) for value in args.measurements.split(",") if value.strip()]
        factor = calculate_correction(args.real_diameter_mm, measurements)
    except (ValueError, OverflowError) as exc:
        print(f"Calibration input error: {exc}", file=sys.stderr)
        return 2

    print(f"Coordinate correction factor (apply once to XYZ): {factor:.9g}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
