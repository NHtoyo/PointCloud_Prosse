from __future__ import annotations

import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "python_backend" / "tests" / "process_runner" / "ProcessRunnerTests.csproj"
SETUP_SCRIPT = ROOT / "python_backend" / "Setup-Python.ps1"


def _powershell_51() -> Path | None:
    windir = os.environ.get("WINDIR")
    if not windir:
        return None
    path = Path(windir) / "System32" / "WindowsPowerShell" / "v1.0" / "powershell.exe"
    return path if path.is_file() else None


@pytest.fixture(scope="session")
def process_test_host(tmp_path_factory: pytest.TempPathFactory) -> Path:
    if os.name != "nt":
        pytest.skip("The process-tree and Setup-Python tests require Windows.")
    dotnet = shutil.which("dotnet")
    if not dotnet:
        pytest.skip("The standalone process test host requires the .NET SDK.")
    output = tmp_path_factory.mktemp("pcwb-process-host")
    artifacts = output / "artifacts"
    publish = output / "publish"
    result = subprocess.run(
        [dotnet, "publish", str(PROJECT), "--configuration", "Release",
         "--artifacts-path", str(artifacts), "--output", str(publish)],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=180,
        check=False,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    host = publish / "ProcessRunnerTests.exe"
    assert host.is_file()
    return host


def test_python_bridge_process_runner_stream_idle_exit_and_cancel(process_test_host: Path) -> None:
    result = subprocess.run(
        [str(process_test_host)],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=45,
        check=False,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "PROCESS_RUNNER_TESTS:PASS" in result.stdout


def test_old_job_object_gap_is_reproduced_and_child_is_cleaned(process_test_host: Path) -> None:
    result = subprocess.run(
        [str(process_test_host), "--reproduce-job-gap"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=30,
        check=False,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "REPRODUCED: Job Object killed the assigned parent but left its earlier child alive." in result.stdout


@pytest.mark.parametrize(
    ("mode", "expected_exit", "expected_stdout", "expected_stderr"),
    [
        ("output", 0, "FAKE_PIP_STDOUT_日本語", "FAKE_PIP_STDERR_日本語"),
        ("silent", 0, "", ""),
        ("output", 23, "FAKE_PIP_STDOUT_日本語", "FAKE_PIP_STDERR_日本語"),
    ],
)
def test_setup_script_pip_io_wait_and_exit(
    process_test_host: Path,
    tmp_path: Path,
    mode: str,
    expected_exit: int,
    expected_stdout: str,
    expected_stderr: str,
) -> None:
    powershell = _powershell_51()
    if powershell is None:
        pytest.skip("Windows PowerShell 5.1 was not found.")

    fake_dir = tmp_path / "fake python 日本語"
    fake_dir.mkdir()
    fake_python = fake_dir / "python.exe"
    for source in process_test_host.parent.iterdir():
        if source.is_file() and source.suffix.lower() != ".pdb":
            target_name = "python.exe" if source.suffix.lower() == ".exe" else source.name
            shutil.copyfile(source, fake_dir / target_name)

    env_root = tmp_path / "temporary environment 日本語"
    pip_log = tmp_path / "pip log 日本語" / "pip.log"
    env = os.environ.copy()
    env["PCWB_FAKE_PIP_MODE"] = mode
    env["PCWB_FAKE_PIP_EXIT"] = str(expected_exit)
    command = [
        str(powershell), "-NoLogo", "-NoProfile", "-NonInteractive",
        "-ExecutionPolicy", "Bypass", "-File", str(SETUP_SCRIPT),
        "-PythonExe", str(fake_python), "-EnvironmentRoot", str(env_root),
        "-PipLogPath", str(pip_log),
    ]
    started = time.monotonic()
    result = subprocess.run(
        command,
        cwd=ROOT / "python_backend",
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=60,
        env=env,
        check=False,
    )
    elapsed = time.monotonic() - started
    combined = result.stdout + result.stderr

    if expected_exit == 0:
        assert result.returncode == 0, combined
        assert "PCWB_PIP_EXIT:0" in combined
        assert "Python環境の確認が完了しました" in combined
    else:
        assert result.returncode != 0, combined
        assert "PCWB_PIP_EXIT:23" in combined
        assert "pip ExitCode: 23" in combined

    if expected_stdout:
        assert expected_stdout in result.stdout, combined
    if expected_stderr:
        assert expected_stderr in result.stderr, combined
    if mode == "silent":
        assert elapsed >= 0.7, f"Setup returned before silent pip completed ({elapsed:.3f}s)."
        assert "PCWB_PIP_EXIT:0" in combined
    assert pip_log.is_file(), "The pip command did not receive the requested log path."
