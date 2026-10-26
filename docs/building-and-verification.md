# Building, Packaging, and Verification

This guide covers building `Tedd.DotNetLab` from source, compiling the optional C++ CoreCLR profiler, creating and verifying reproducible Windows release packages, and running the qualification test suites.

---

## Prerequisites

- **Managed Solution & CLI:**
  - `.NET 10 SDK` or newer (`10.0.100+`, including `.NET 11` previews/RCs and `.NET 12+` SDKs via `rollForward: major` and `allowPrerelease: true` in [`global.json`](../global.json)).
  - Windows x64 or Linux x64 (including Ubuntu 24.04 on WSL2).
- **Optional Native C++ CoreCLR Profiler (`Tedd.DotNetLab.Profiler`):**
  - **Windows x64:** Visual Studio 2022/2026 C++ x64 tools + Clang/LLVM (`clang-cl`), CMake, Ninja, and Windows 10/11 SDK.
  - **Linux x64:** Clang, CMake, Ninja/Make, and OpenSSL headers (`libssl-dev`).
- **Distribution Packaging & Statistical Reference Suites (Optional):**
  - Python 3.12+ (with `numpy`, `scipy`, and `jsonschema` for statistical/schema qualification scripts).

---

## Build Layout and Locked Dependencies

All NuGet package versions are centrally pinned in [`Directory.Packages.props`](../Directory.Packages.props) with committed lock files (`packages.lock.json`) enforced via `RestorePackagesWithLockFile=true`.

By default, [`Directory.Build.props`](../Directory.Build.props) directs MSBuild `bin/` and `obj/` outputs to `$(DotNetLabBuildRoot)`:
- Defaults to `artifacts/` at the repository root (gitignored) on standard machines, or `D:/Workspaces/AI/dotnetlab-implementation` when `D:/Workspaces/AI` is present.
- Override at any time with `-p:DotNetLabBuildRoot=/custom/build/path`.

### Build Commands (Windows PowerShell)

```powershell
# 1. Optional: Build and unit-test the native C++ CoreCLR profiler first
#    so Tedd.DotNetLab.Host automatically bundles Tedd.DotNetLab.Profiler.dll:
pwsh -NoProfile -File native/build-native.ps1

# 2. Restore with locked dependencies and build the Release solution:
dotnet restore Tedd.DotNetLab.sln --locked-mode
dotnet build Tedd.DotNetLab.sln -c Release --no-restore
```

### Build Commands (Linux / WSL2 Bash)

```bash
# Optional native profiler build on Linux x64:
cmake -S native -B artifacts/native -DCMAKE_CXX_COMPILER=clang++ -DCMAKE_BUILD_TYPE=Release
cmake --build artifacts/native
ctest --test-dir artifacts/native --output-on-failure

# Managed restore and Release build:
dotnet restore Tedd.DotNetLab.sln --locked-mode
dotnet build Tedd.DotNetLab.sln -c Release --no-restore
```

---

## Building and Verifying a Windows Distribution Package

[`tools/build-windows-package.ps1`](../tools/build-windows-package.ps1) creates a deterministic, hash-manifested `Tedd.DotNetLab-1.0.0-windows-x64.zip` archive from a clean Git commit:

1. Verifies the repository working tree is clean and resolves the exact 40-char Git commit SHA.
2. Compiles and runs all 5 native C++ profiler contract tests (`native/build-native.ps1`).
3. Runs `dotnet restore --locked-mode` and publishes `Tedd.DotNetLab.Host` + `Tedd.DotNetLab.TraceWorker` (framework-dependent, untrimmed, ReadyToRun disabled) along with the bundled `skills/dotnetlab` skill, `schemas/`, `docs/`, and third-party NuGet/native license notices.
4. Verifies every locked NuGet archive's cryptographic content & archive SHA-512 via `Tedd.DotNetLab.PackageHashes` and records Authenticode status for every `.dll`/`.exe`.
5. Invokes [`tools/windows_package.py`](../tools/windows_package.py) to produce a reproducible ZIP and immediately runs offline structural & SHA-256 verification.

```powershell
pwsh -NoProfile -File tools/build-windows-package.ps1
```

### Offline Package Verification

Before extracting or executing a downloaded Windows distribution archive, you can verify its integrity without extracting files or running code:

```bash
python tools/windows_package.py verify --archive Tedd.DotNetLab-1.0.0-windows-x64.zip --sha256 <expected-64-hex-sha256>
```

### NuGet Packages and GitHub Actions Artifacts

Every CI run ([`.github/workflows/ci.yml`](../.github/workflows/ci.yml)) and tagged release ([`.github/workflows/release.yml`](../.github/workflows/release.yml)) builds both Windows and Linux native profilers, packs all `src/` NuGet packages (`.nupkg` and `.snupkg`, including the `Tedd.DotNetLab` `.NET` CLI tool and `Tedd.DotNetLab.Harness` library), uploads them as the **`nuget-packages`** workflow artifact, attaches them to GitHub Releases, and optionally pushes them to NuGet.org (`NUGET_API_KEY`) or GitHub Packages:

```bash
dotnet pack Tedd.DotNetLab.sln -c Release --no-restore -p:DotNetLabPackCrossPlatform=true
```

---

## Running Tests and Qualification Suites

### Standard xUnit Suite

```bash
dotnet test tests/Tedd.DotNetLab.Tests/Tedd.DotNetLab.Tests.csproj -c Release --no-build
```

> [!NOTE]
> Both managed controller/EventPipe/harness/installer/confirmation tests and live CoreCLR native profiler tests (`ProfilerTests`, `NativeStartupTests`, `NativeAttachTests`, `DualRuntimeTests`) run directly on any installed `.NET 10.0+` runtime (including `.NET 10.0.x` patch releases and `.NET 11` RCs/previews) without requiring pinned runtime downloads. When multiple runtimes (such as `.NET 10` and `.NET 11 RC`) are installed side-by-side, `DualRuntimeTests` automatically exercises cross-runtime startup, live profiler attach, ReJIT IL mutation, and verified restoration across them.

### Requirement-Mapped Test Evidence (`tests/run-test-evidence.ps1`)

Test methods carry `[Case(id, requirement, milestone, level)]` traits. The evidence runner executes the suite, exports assembly-hash-bound test declarations, and joins TRX outcomes into `requirement-evidence.json`:

```powershell
pwsh -NoProfile -File tests/run-test-evidence.ps1
pwsh -NoProfile -File tests/run-test-evidence.ps1 -Serial
pwsh -NoProfile -File tests/run-test-evidence.ps1 -Filter "Requirement=FR-074"
```

### Statistical Oracle Qualification (`tests/run-statistical-qualification.ps1`)

Cross-checks production C# estimators (paired log-ratio $t$-tests, TOST equivalence, Holm-Bonferroni, $2 \times 2$ factorial contrasts, cluster-bootstrapped quantiles, and native series) against independent Python `NumPy`/`SciPy` reference implementations and Monte Carlo coverage/false-positive envelopes:

```powershell
pwsh -NoProfile -File tests/run-statistical-qualification.ps1
```

See [Statistical Contract](statistical-contract.md) for mathematical definitions and simulation envelopes.

### Cross-Runtime & Linux WSL Qualification

- **Dual-Runtime Suite (`.NET 10` + `.NET 11`):**
  `tests/run-dual-runtime.ps1` builds and validates cross-version controller/target inspection, ReJIT mutation, restoration, and `.NET 11` fresh normal-build confirmation.
- **Linux WSL2 Transport & Native Suite:**
  `tests/run-linux-transport.ps1` validates Linux peer-credential authentication, ext4 SQLite WAL catalog behavior, cgroup v2 `TraceWorker` containment, EventPipe capture, and (with `-Distribution Ubuntu -NativeStartup`) Linux `ICorProfilerInfo12` startup inspection, probes, and ReJIT.
- **`.NET 12` CoreRun Qualification:**
  Set `DOTNETLAB_NET12_CORE_ROOT` to a Windows x64 `Core_Root` directory containing `corerun.exe`, `System.Private.CoreLib.dll`, `coreclr.dll`, and `clrjit.dll` to enable the live CoreRun integration test in `Net12ManagedExecutionTests`.
