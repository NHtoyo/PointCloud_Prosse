#!/usr/bin/env python3
"""Create deterministic, disposable PLY inputs for PCWB integration tests."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import struct
from pathlib import Path

POINT = struct.Struct("<fffBBBi")
GOLDEN_ANGLE = math.pi * (3.0 - math.sqrt(5.0))


def sample(kind: str, index: int, count: int) -> tuple[float, float, float, int, int, int, int]:
    phase = index * GOLDEN_ANGLE
    y_unit = 1.0 - 2.0 * (index + 0.5) / count
    radial = math.sqrt(max(0.0, 1.0 - y_unit * y_unit))
    x_unit = radial * math.cos(phase)
    z_unit = radial * math.sin(phase)
    if kind.startswith("cloud_"):
        center = {"cloud_A": (0.0, 0.0, 0.0), "cloud_B": (0.001, 0.0, 0.0), "cloud_C": (0.0, -0.001, 0.0)}[kind]
        radius = 0.025
        xyz = (center[0] + radius * x_unit, center[1] + radius * y_unit, center[2] + radius * z_unit)
    elif kind == "sphere60mm":
        xyz = (0.025 * x_unit, 0.025 * y_unit, 0.025 * z_unit)
    elif kind.startswith("stem_"):
        height = 1.0
        angle = phase
        xyz = (0.005 * math.cos(angle), height * (index + 0.5) / count, 0.005 * math.sin(angle))
    elif kind == "stress":
        xyz = (0.05 * x_unit, 0.05 * y_unit, 0.05 * z_unit)
    else:
        raise ValueError(f"unknown fixture kind: {kind}")

    red = int((x_unit + 1.0) * 127.5)
    green = int((y_unit + 1.0) * 127.5)
    blue = int((z_unit + 1.0) * 127.5)
    label = index % 9
    return (*xyz, red, green, blue, label)


def write_ply(path: Path, kind: str, count: int) -> dict[str, object]:
    path.parent.mkdir(parents=True, exist_ok=True)
    header = (
        "ply\nformat binary_little_endian 1.0\n"
        f"comment pcwb_fixture {kind}\n"
        f"element vertex {count}\n"
        "property float x\nproperty float y\nproperty float z\n"
        "property uchar red\nproperty uchar green\nproperty uchar blue\n"
        "property int label\nend_header\n"
    ).encode("ascii")
    with path.open("wb") as stream:
        stream.write(header)
        pack = POINT.pack
        for index in range(count):
            stream.write(pack(*sample(kind, index, count)))

    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    return {"file": path.name, "kind": kind, "point_count": count, "sha256": digest}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--include-load-tiers", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    fixtures = [
        write_ply(output / "cloud_A_100k.ply", "cloud_A", 100_000),
        write_ply(output / "cloud_B_100k.ply", "cloud_B", 100_000),
        write_ply(output / "cloud_C_100k.ply", "cloud_C", 100_000),
        write_ply(output / "sphere60mm_4096.ply", "sphere60mm", 4_096),
        write_ply(output / "stem12mm_100k.ply", "stem_100k", 100_000),
        write_ply(output / "edit_1k.ply", "cloud_A", 1_000),
    ]
    fixtures.extend([
        write_ply(output / "same-name-A" / "duplicate.ply", "cloud_A", 1_000),
        write_ply(output / "same-name-B" / "duplicate.ply", "cloud_B", 1_000),
    ])

    def write_custom(name: str, header: bytes, payload: bytes = b"") -> dict[str, object]:
        path = output / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(header + payload)
        return {"file": str(path.relative_to(output)).replace("\\", "/"),
                "kind": "boundary", "bytes": path.stat().st_size,
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}

    fixtures.append(write_custom(
        "boundary/zero_points.ply",
        b"ply\nformat ascii 1.0\nelement vertex 0\nproperty float x\nproperty float y\nproperty float z\nend_header\n"))
    fixtures.append(write_custom(
        "boundary/one_point.ply",
        b"ply\nformat ascii 1.0\nelement vertex 1\nproperty float x\nproperty float y\nproperty float z\nend_header\n0 0 0\n"))
    fixtures.append(write_custom("boundary/malformed_header.ply", b"not a ply\n"))
    fixtures.append(write_custom(
        "boundary/truncated_binary.ply",
        b"ply\nformat binary_little_endian 1.0\nelement vertex 2\nproperty float x\nproperty float y\nproperty float z\nend_header\n",
        struct.pack("<fff", 1.0, 2.0, 3.0)))
    fixtures.append(write_custom(
        "boundary/non_finite.ply",
        b"ply\nformat binary_little_endian 1.0\nelement vertex 1\nproperty float x\nproperty float y\nproperty float z\nend_header\n",
        struct.pack("<fff", math.nan, math.inf, 0.0)))
    (output / "sample.ply").write_bytes((output / "cloud_A_100k.ply").read_bytes())
    fixtures.append({
        "file": "sample.ply",
        "kind": "cloud_A startup alias",
        "point_count": 100_000,
        "sha256": hashlib.sha256((output / "sample.ply").read_bytes()).hexdigest(),
    })
    if args.include_load_tiers:
        for count in (10_000, 100_000, 500_000, 1_000_000, 2_200_000):
            fixtures.append(write_ply(output / f"stress_{count}.ply", "stress", count))

    manifest = {
        "format": "pcwb-fourth-audit-fixtures-v1",
        "coordinate_note": "Data-space coordinates are intentional. With the current test renderer display scale of 1200 mm/data-unit, sphere60mm has a 60 mm diameter and stem12mm has a 12 mm diameter.",
        "record_format": "binary_little_endian: float32 XYZ, uchar RGB, int32 label",
        "fixtures": fixtures,
    }
    (output / "fixtures.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
