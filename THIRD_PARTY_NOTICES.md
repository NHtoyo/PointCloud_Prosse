# Third-Party Notices

This project includes or can be installed with third-party software. Each component remains under its own license; the repository's MIT License does not replace those terms. This inventory is an environment snapshot taken on 2026-10-09, not a lockfile or a claim that every optional package is shipped in every build.

## Python

`PointCloudVR/python_backend/requirements.txt` declares these direct dependencies without version pins: Open3D, NumPy, SciPy, FastAPI, Uvicorn, Pydantic, and Matplotlib. The inspected virtual environment used Python 3.12.3 (conda-forge), not the Python 3.11 version mentioned in earlier setup guidance. Its resolved package versions and licenses are listed in [DEPENDENCY_INVENTORY.csv](docs/legal/DEPENDENCY_INVENTORY.csv).

Package license files copied from installed distribution metadata are under [`licenses/third_party/python/`](licenses/third_party/python/). Matplotlib's bundled DejaVu and STIX font license notices are included beside its package notices. This snapshot includes indirect packages installed in the inspected environment; a clean installation may resolve different versions because requirements are unpinned.

The Pillow 12.3.0 Windows wheel's copied `LICENSE` contains third-party notices for its bundled codecs and text/font libraries (including Brotli, FreeType, HarfBuzz, LittleCMS, libavif, libjpeg-turbo, libpng, libwebp, OpenJPEG, TIFF, XZ, and zlib-ng). Their exact license texts are preserved in that file; per-component versions found there are recorded in the CSV. The wheel can statically include native code even where no separate DLL is present.

`README.md` says that Python 3.11 is automatically installed when absent, while the inspected `setup_python_env.bat` prefers 3.11 but can select Python 3.12, 3.10, 3.9, or a default interpreter; its error text states 3.8-3.12. The inspected project environment is 3.12.3. No Python version or dependency lock was changed as part of this license-only task.

### Native components found in the inspected Windows environment

| Component | Evidence / license | Notice and status |
|---|---|---|
| oneTBB | `open3d/tbb12.dll`; Open3D uses oneTBB. Apache-2.0; DLL version/build not identified. | [`oneTBB-LICENSE.txt`](licenses/third_party/native/oneTBB-LICENSE.txt), [`Apache-2.0.txt`](licenses/third_party/native/Apache-2.0.txt). Confirm exact wheel provenance/version before binary redistribution. |
| OpenBLAS | NumPy and SciPy `*.libs` DLLs; BSD-3-Clause family. Exact bundled build/version not identified. | [`OpenBLAS-LICENSE.txt`](licenses/third_party/native/OpenBLAS-LICENSE.txt). Confirm the exact wheel's native build metadata before binary redistribution. |
| Microsoft Visual C++ runtime | `numpy.libs/msvcp140-*.dll` present. Exact redistributable package and applicable Microsoft terms were not established. | **Distribution blocker until verified** against the originating wheel and Microsoft redistribution terms. |
| Open3D native extension build dependencies | Open3D's license manifest covers its included web assets, but the inspected wheel does not expose a complete native static-link SBOM (software bill of materials). | **Distribution blocker until the exact 0.19.0 Windows wheel build's native dependency notices are verified.** |
| Matplotlib native extension build dependencies | No separate DLLs were found in the environment, but the wheel may statically link native components such as FreeType/libpng/Qhull. | **Verify the exact wheel build's bundled native notices before binary redistribution.** |

## Unity

The project uses Unity Editor/Runtime `6000.4.7f1`. Unity Engine and built-in modules are not MIT-licensed by this repository; use and redistribution require compliance with the applicable Unity Engine License and current Unity terms. Unity's package documentation recommends including package license and third-party notices when distributing packages ([Unity legal-package guidance](https://docs.unity.com/en-us/engine/6000.0/manual/packages-list/cus-pkg-development/cus-legal)).

| Package | Resolved version | License / notices |
|---|---:|---|
| Input System | 1.19.0 in `packages-lock.json`; `manifest.json` says 1.7.0 | Unity Companion License; local package notice copied under `licenses/third_party/unity/`. Version discrepancy must be resolved before a reproducible release. |
| XR Interaction Toolkit | 3.0.4 | Unity Companion License; package Third Party Notices includes Google ARCore gesture code under Apache-2.0. |
| XR Plug-in Management | 4.5.2 | Unity Companion License. |
| OpenXR Plugin | 1.10.0 | Unity Companion License / Unity Package Distribution License as stated by package; Third Party Notices includes Khronos OpenXR-SDK-Source and other components. |
| XR Core Utils | 2.6.0 | Unity Companion License. |
| Mathematics | 1.3.3 | Unity Companion License; package also includes Ashima Arts / Stefan Gustavson noise code under MIT-style terms. |
| XR Legacy Input Helpers | 3.0.1 | Unity Companion License; package Third Party Notices includes Google Arm Model code under Apache-2.0. |
| uGUI | 2.0.0 | Unity Companion License. |
| Multiplayer Center | 1.0.1 | Unity Package Distribution License; package notice says it contains no third-party software. |
| Built-in Unity modules | 1.0.0 in lock file | Part of the Unity installation/runtime; not separately relicensed by this repository. |

Unity package notice snapshots and the official license pages captured on 2026-10-09 are under [`licenses/third_party/unity/`](licenses/third_party/unity/). Package-specific Apache notices are retained verbatim in each package's `Third Party Notices.md`.

The OpenXR package notice lists the Oculus OpenXR Mobile SDK under the Oculus SDK License Agreement. The notice says it applies to Android builds when Oculus Quest Support is enabled. Meta's current page titles the agreement the Meta Platform Technologies SDK License Agreement (formerly Oculus SDK License Agreement); the official-page snapshot is under [`licenses/third_party/unity/`](licenses/third_party/unity/). Verify the target/feature configuration and current [vendor terms](https://developers.meta.com/vr/licenses/oculussdk/) before distributing an applicable build.

## Data and provenance

`PointCloudVR/Assets/StreamingAssets/sample.ply` is tracked, but its author, source, and permission to redistribute have not been confirmed. It is not covered by the project's MIT grant and must not be included in a release until permission is documented or it is excluded. CloudCompare code was not found in the inspected implementation; see [the provenance audit](docs/legal/CC_CODE_PROVENANCE_AUDIT.md) for scope and limitations.

The full snapshot inventory, including indirect Python packages, Unity packages, built-in modules, and native DLLs, is [DEPENDENCY_INVENTORY.csv](docs/legal/DEPENDENCY_INVENTORY.csv). See [DISTRIBUTION_GUIDE.md](docs/legal/DISTRIBUTION_GUIDE.md) before redistributing any source or binary build.
