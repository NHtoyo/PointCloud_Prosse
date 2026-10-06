"""Calculate a coordinate correction from a reference and measured lengths in mm."""

import argparse
import math
import statistics
import sys


def calculate_correction(real_diameter_mm: float, measurements_mm: list[float]) -> float:
    if not math.isfinite(real_diameter_mm) or real_diameter_mm <= 0:
        raise ValueError("real_diameter_mm must be finite and positive")
    if not measurements_mm or any(not math.isfinite(value) or value <= 0 for value in measurements_mm):
        raise ValueError("every measured diameter in mm must be finite and positive")
    return real_diameter_mm / statistics.median(measurements_mm)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Calculate a one-time coordinate correction from measurements in mm."
    )
    parser.add_argument("--real_diameter_mm", type=float, required=True)
    parser.add_argument("--measurements_mm", type=str, required=True,
                        help="Comma-separated measured diameters, all in millimeters")
    args = parser.parse_args()

    try:
        measurements_mm = [float(value.strip()) for value in args.measurements_mm.split(",") if value.strip()]
        factor = calculate_correction(args.real_diameter_mm, measurements_mm)
    except (ValueError, OverflowError) as exc:
        print(f"Calibration input error: {exc}", file=sys.stderr)
        return 2

    print(f"Coordinate correction factor from mm measurements (apply once to XYZ): {factor:.9g}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
