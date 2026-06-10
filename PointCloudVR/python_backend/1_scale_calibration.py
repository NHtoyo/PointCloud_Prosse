import argparse
import json
import os
import sys
import numpy as np

def generate_scale_json(real_diameter_mm, measurements, output_json_path):
    print(f"Real sphere diameter: {real_diameter_mm} mm")
    print(f"Measurements ({len(measurements)} items): {measurements}")
    
    # Calculate scales: real diameter / measured diameter
    scales = [real_diameter_mm / m for m in measurements if m > 0]
    
    if not scales:
        print("Error: No valid measurements (> 0) provided.")
        sys.exit(1)
        
    median_scale = float(np.median(scales))
    print(f"Calculated scale (median): {median_scale:.6f} mm/unit")
    
    report = {
        "mode": "sphere_diameter_calibration",
        "dist_list_path": "direct_ui_input",
        "scale_mm_per_unit": median_scale,
        "robust_stats": {
            "n": len(measurements),
            "median_kept": median_scale,
            "min": float(np.min(scales)),
            "max": float(np.max(scales))
        }
    }
    
    # Create directory if it doesn't exist
    os.makedirs(os.path.dirname(os.path.abspath(output_json_path)), exist_ok=True)
    with open(output_json_path, 'w', encoding='utf-8') as f:
        json.dump(report, f, indent=2, ensure_ascii=False)
        
    print(f"Successfully saved scale report JSON: {output_json_path}")

def main():
    parser = argparse.ArgumentParser(description="Scale Calibration script (direct input version)")
    parser.add_argument("--real_diameter", type=float, default=60.0, help="Real sphere diameter in mm")
    parser.add_argument("--measurements", type=str, required=True, help="Comma-separated measurements in unit")
    parser.add_argument("--output", type=str, default="config/scale_calibration_report.json", help="Output path for the report JSON")
    
    args = parser.parse_args()
    
    try:
        measurements = [float(x.strip()) for x in args.measurements.split(",") if x.strip()]
    except Exception as e:
        print(f"Error: Failed to parse measurements. Please provide a comma-separated list of numbers. ({e})")
        sys.exit(1)
        
    if not measurements:
        print("Error: Measurements list is empty.")
        sys.exit(1)
        
    generate_scale_json(args.real_diameter, measurements, args.output)

if __name__ == "__main__":
    main()
