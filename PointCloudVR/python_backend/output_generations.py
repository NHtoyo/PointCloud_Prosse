"""Atomic publication of immutable multi-file output generations."""

from __future__ import annotations

import hashlib
import json
import os
import re
import tempfile
import uuid
from pathlib import Path


_RUN_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{1,80}$")


def new_run_id() -> str:
    return uuid.uuid4().hex


def validate_run_id(run_id: str) -> str:
    value = str(run_id or "")
    if not _RUN_ID_PATTERN.fullmatch(value):
        raise ValueError("run_id must contain 1-80 ASCII letters, digits, '_' or '-'.")
    return value


def create_staging_directory(output_root: str | Path, run_id: str) -> Path:
    root = Path(output_root).expanduser().resolve()
    run_id = validate_run_id(run_id)
    root.mkdir(parents=True, exist_ok=True)
    staging = root / f".staging-{run_id}"
    staging.mkdir(exist_ok=False)
    return staging


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _fsync_file(path: Path) -> None:
    with path.open("r+b") as stream:
        os.fsync(stream.fileno())


def _fsync_directory(path: Path) -> None:
    if os.name == "nt":
        return
    try:
        descriptor = os.open(path, os.O_RDONLY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    except OSError:
        pass


def _write_json_fsynced(path: Path, payload: dict) -> None:
    with path.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(payload, stream, ensure_ascii=False, allow_nan=False, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())


def _replace_pointer_file(staged_pointer: Path, pointer_path: Path) -> None:
    os.replace(staged_pointer, pointer_path)


def publish_generation(output_root: str | Path, staging_directory: str | Path,
                       run_id: str, artifact_names: tuple[str, ...] | list[str],
                       metadata: dict | None = None) -> dict:
    root = Path(output_root).expanduser().resolve()
    staging = Path(staging_directory).expanduser().resolve()
    run_id = validate_run_id(run_id)
    if staging.parent != root or not staging.name.startswith(".staging-"):
        raise ValueError("Staging directory must be a direct child of the output root.")

    artifacts = []
    for name in artifact_names:
        if Path(name).name != name or name in ("", ".", ".."):
            raise ValueError(f"Invalid artifact filename: {name!r}")
        path = staging / name
        if not path.is_file():
            raise FileNotFoundError(f"Required output artifact is missing: {path}")
        _fsync_file(path)
        artifacts.append({
            "name": name,
            "size_bytes": path.stat().st_size,
            "sha256": _sha256(path),
        })

    manifest = {
        "schema_version": 1,
        "status": "complete",
        "run_id": run_id,
        "artifacts": artifacts,
        "metadata": metadata or {},
    }
    manifest_path = staging / "manifest.json"
    _write_json_fsynced(manifest_path, manifest)
    manifest_hash = _sha256(manifest_path)

    runs_directory = root / "runs"
    runs_directory.mkdir(parents=True, exist_ok=True)
    generation_directory = runs_directory / run_id
    if generation_directory.exists():
        raise FileExistsError(f"Output generation already exists: {generation_directory}")
    os.replace(staging, generation_directory)
    _fsync_directory(runs_directory)

    pointer = {
        "schema_version": 1,
        "status": "complete",
        "run_id": run_id,
        "manifest": f"runs/{run_id}/manifest.json",
        "manifest_sha256": manifest_hash,
    }
    pointer_path = root / "current_run.json"
    descriptor, temporary_name = tempfile.mkstemp(prefix=".current-run-", suffix=".tmp", dir=root)
    temporary_pointer = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(pointer, stream, ensure_ascii=False, allow_nan=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        _replace_pointer_file(temporary_pointer, pointer_path)
        _fsync_directory(root)
    finally:
        try:
            temporary_pointer.unlink()
        except FileNotFoundError:
            pass

    return {
        "run_id": run_id,
        "generation_directory": str(generation_directory),
        "manifest_path": str(generation_directory / "manifest.json"),
        "pointer_path": str(pointer_path),
        "manifest": manifest,
    }


def resolve_current_generation(output_root: str | Path, verify_artifacts: bool = True) -> dict:
    root = Path(output_root).expanduser().resolve()
    pointer_path = root / "current_run.json"
    pointer = json.loads(pointer_path.read_text(encoding="utf-8"))
    run_id = validate_run_id(pointer.get("run_id", ""))
    expected_manifest_path = f"runs/{run_id}/manifest.json"
    if pointer.get("status") != "complete" or pointer.get("manifest") != expected_manifest_path:
        raise ValueError("Current output pointer is incomplete or points outside the generation directory.")

    generation_directory = root / "runs" / run_id
    manifest_path = generation_directory / "manifest.json"
    if _sha256(manifest_path) != pointer.get("manifest_sha256"):
        raise ValueError("Output generation manifest checksum mismatch.")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if manifest.get("status") != "complete" or manifest.get("run_id") != run_id:
        raise ValueError("Output generation manifest is incomplete or has the wrong run ID.")

    for artifact in manifest.get("artifacts", []):
        name = artifact.get("name", "")
        if Path(name).name != name or not name:
            raise ValueError("Output generation contains an invalid artifact name.")
        artifact_path = generation_directory / name
        if not artifact_path.is_file() or artifact_path.stat().st_size != artifact.get("size_bytes"):
            raise ValueError(f"Output artifact is missing or has the wrong size: {artifact_path}")
        if verify_artifacts and _sha256(artifact_path) != artifact.get("sha256"):
            raise ValueError(f"Output artifact checksum mismatch: {artifact_path}")

    return {
        "run_id": run_id,
        "generation_directory": generation_directory,
        "manifest_path": manifest_path,
        "manifest": manifest,
    }
