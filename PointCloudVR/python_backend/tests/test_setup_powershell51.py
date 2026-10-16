from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from pathlib import Path

import pytest


PROJECT_ROOT = Path(__file__).resolve().parents[2]
SOURCE_BACKEND = PROJECT_ROOT / "python_backend"
SOURCE_SETUP = SOURCE_BACKEND / "Setup-Python.ps1"
UTF8_BOM = b"\xef\xbb\xbf"


def _powershell_51() -> Path | None:
    windir = os.environ.get("WINDIR")
    if not windir:
        return None
    executable = Path(windir) / "System32" / "WindowsPowerShell" / "v1.0" / "powershell.exe"
    return executable if executable.is_file() else None


def _assert_powershell_51_parses(script_path: Path, powershell: Path) -> None:
    command = r"""
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw "Expected Windows PowerShell 5.1, got $($PSVersionTable.PSVersion)" }
$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($env:PCWB_SETUP_SCRIPT,[ref]$tokens,[ref]$errors) | Out-Null
if ($errors.Count -gt 0) { $errors | Format-List *; exit 1 }
Write-Output "PCWB_PS51:$($PSVersionTable.PSVersion)"
"""
    env = os.environ.copy()
    env["PCWB_SETUP_SCRIPT"] = str(script_path)
    result = subprocess.run(
        [str(powershell), "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=30,
        env=env,
        check=False,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "PCWB_PS51:5.1" in result.stdout


def test_setup_script_is_utf8_bom_and_keeps_japanese_messages() -> None:
    raw = SOURCE_SETUP.read_bytes()
    assert raw.startswith(UTF8_BOM), "Setup-Python.ps1 must retain its UTF-8 BOM for Windows PowerShell 5.1."
    script = raw[len(UTF8_BOM) :].decode("utf-8", errors="strict")
    assert "既存のPython環境は正常です" in script
    assert "基礎Python" in script
    assert "PCWB_STAGE:" in script


@pytest.mark.skipif(os.name != "nt", reason="Windows PowerShell 5.1 is only available on Windows.")
def test_source_and_player_copy_parse_in_windows_powershell_51() -> None:
    powershell = _powershell_51()
    if powershell is None:
        pytest.skip("Windows PowerShell 5.1 was not found.")

    _assert_powershell_51_parses(SOURCE_SETUP, powershell)
    player_backend = os.environ.get("PCWB_PLAYER_BACKEND_PATH")
    if player_backend:
        deployed_setup = Path(player_backend) / "Setup-Python.ps1"
        assert deployed_setup.is_file(), f"Player setup script not found: {deployed_setup}"
        deployed_bytes = deployed_setup.read_bytes()
        assert deployed_bytes.startswith(UTF8_BOM), "The Player copy lost the UTF-8 BOM."
        _assert_powershell_51_parses(deployed_setup, powershell)


@pytest.mark.skipif(os.name != "nt", reason="Windows PowerShell 5.1 is only available on Windows.")
def test_setup_launch_with_unicode_path_logs_utf8_and_preserves_existing_environment() -> None:
    powershell = _powershell_51()
    if powershell is None:
        pytest.skip("Windows PowerShell 5.1 was not found.")

    environment_root = os.environ.get("PCWB_SETUP_TEST_ENVIRONMENT_ROOT")
    if not environment_root:
        pytest.skip("Set PCWB_SETUP_TEST_ENVIRONMENT_ROOT to a verified environment root for the no-install launch check.")
    environment_root_path = Path(environment_root)
    python_exe = Path(os.environ.get("PCWB_SETUP_TEST_PYTHON", str(environment_root_path / ".venv" / "Scripts" / "python.exe")))
    assert (environment_root_path / ".venv" / "Scripts" / "python.exe").is_file()
    assert python_exe.is_file()

    source_backends = [SOURCE_BACKEND]
    player_backend = os.environ.get("PCWB_PLAYER_BACKEND_PATH")
    if player_backend:
        deployed_backend = Path(player_backend)
        assert (deployed_backend / "Setup-Python.ps1").is_file()
        source_backends.append(deployed_backend)

    with tempfile.TemporaryDirectory(prefix="PCWB Python setup 日本語 ") as temporary:
        test_root = Path(temporary)
        process_env = os.environ.copy()
        process_env["TEMP"] = str(test_root)
        process_env["TMP"] = str(test_root)
        for index, source_backend in enumerate(source_backends):
            copied_backend = test_root / f"backend {index} with 日本語"
            copied_backend.mkdir()
            copied_setup = copied_backend / "Setup-Python.ps1"
            shutil.copyfile(source_backend / "Setup-Python.ps1", copied_setup)
            shutil.copyfile(source_backend / "requirements.txt", copied_backend / "requirements.txt")
            assert copied_setup.read_bytes().startswith(UTF8_BOM)

            result = subprocess.run(
                [
                    str(powershell),
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    str(copied_setup),
                    "-PythonExe",
                    str(python_exe),
                    "-EnvironmentRoot",
                    str(environment_root_path),
                ],
                cwd=copied_backend,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=120,
                env=process_env,
                check=False,
            )
            combined = result.stdout + result.stderr
            assert result.returncode == 0, combined
            assert "PCWB_STAGE:PYTHON_CHECK" in combined
            assert "既存のPython環境は正常です" in combined
            assert "\ufffd" not in combined


@pytest.mark.skipif(os.name != "nt", reason="Windows PowerShell 5.1 is only available on Windows.")
def test_bomless_script_experiment_detects_encoding_regression() -> None:
    powershell = _powershell_51()
    if powershell is None:
        pytest.skip("Windows PowerShell 5.1 was not found.")

    with tempfile.TemporaryDirectory(prefix="PCWB PS51 BOM regression ") as temporary:
        path = Path(temporary) / "Setup-Python without BOM.ps1"
        path.write_bytes(SOURCE_SETUP.read_bytes()[len(UTF8_BOM) :])
        env = os.environ.copy()
        env["PCWB_SETUP_SCRIPT"] = str(path)
        parse_command = r"""
$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($env:PCWB_SETUP_SCRIPT,[ref]$tokens,[ref]$errors) | Out-Null
Write-Output "PCWB_BOMLESS_PARSER_ERRORS:$($errors.Count)"
if ($errors.Count -gt 0) { $errors | Format-List Message,Extent; exit 1 }
"""
        parsed = subprocess.run(
            [str(powershell), "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", parse_command],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=30,
            env=env,
            check=False,
        )
        assert parsed.returncode == 0, parsed.stdout + parsed.stderr
        assert "PCWB_BOMLESS_PARSER_ERRORS:0" in parsed.stdout

        path = Path(temporary) / "encoding probe.ps1"
        script = "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\nWrite-Output '日本語ログ'\n"
        path.write_bytes(script.encode("utf-8"))
        result = subprocess.run(
            [str(powershell), "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(path)],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=30,
            check=False,
        )
        if result.returncode == 0:
            assert "日本語ログ" not in result.stdout, "The BOM-less control unexpectedly decoded Japanese correctly."
