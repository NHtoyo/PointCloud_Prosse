# GPU Compatibility Tests

These tests distinguish graphics-device startup from visible rendering. A log showing a graphics API or shader initialization is not proof that points were visible or interactive.

## Visible API matrix

Use a newly built Windows x64 Player and a synthetic PLY in the configured `PointCloudData` folder. Run from an interactive PowerShell console so the script can wait for a person to inspect each Player window:

```powershell
.\tests\UnityIntegration\Test-GraphicsApiMatrix.ps1 `
  -PlayerExe 'C:\path\to\Player\PointCloudVR.exe' `
  -OutputDirectory 'C:\pcwb-test\run-unique-id' `
  -Cases D3D11,D3D12,Vulkan,D3D11_FL11,D3D11_FL10 `
  -Resolution 1280x720
```

Output folder must be new. After each Player closes, record the three prompts based on the actual screen. `D3D11_FL10` is a negative test and should display the compatibility error rather than silently proceeding. Do not count its expected unsupported result as a rendering pass. For each of 1024x768, 1600x900, and 1920x1080, use a separate output folder and a fresh run. High-DPI testing requires an independently controlled test PC; do not change the workstation's global display setting.

The test script records requested flags, Player exit code, selected graphics-related log lines, and manual confirmations. Keep those local logs out of Git if they contain machine-specific paths or environment details.

## Current evidence

- Automated `EvaluateGraphicsCompatibility` tests: SM5 threshold, required shader, 24-byte stride, and `supportsComputeShaders=false` informational behavior.
- Latest Windows Player headless run: D3D11 / Feature Level 11.1, shader initialized, 100,000 synthetic points loaded and Octree ready. No visible rendering was verified; the process did not close from `-quit` in batchmode and was stopped as an isolated test process.
- Direct3D 12, Vulkan, Feature Level 10_0, vendor-diverse hardware, and resolution/DPI checks remain `NOT_RUN` in this task.

## Required manual observations

For each API, record `graphicsDeviceType`, `graphicsDeviceVersion`, shader level, GPU model/driver, test PLY point count, actual point visibility, color/label display, camera movement, selection/edit, C2C visualization, errors, and whether the Player accepted normal close. Treat an API fallback as a result different from the requested API. Never mark visual checks from logs alone.
