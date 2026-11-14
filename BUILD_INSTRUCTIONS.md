# Build instructions

## Prerequisites

| Component | Version | Needed for |
|---|---|---|
| .NET SDK | 10.0.100 or later (see `global.json`) | everything |
| Visual Studio 2026 (".NET desktop development" and "Desktop development with C++" workloads) | 18.x | Windows development, native build |
| Windows 10 (19041+) or Windows 11, DirectX 11 GPU | | running the desktop application |
| CMake | 3.20 or later | native engine |
| VTK | 9.x (optional) | GPU volume rendering bridge `gvvtk` |
| WiX Toolset | 5.x (restored automatically as an MSBuild SDK) | MSI installer (Windows only) |

NuGet packages are versioned centrally in `Directory.Packages.props`: Math.NET Numerics, CommunityToolkit.Mvvm,
Microsoft.Extensions.Hosting, EF Core SQLite, Dirkster.AvalonDock (+ VS2013 themes), HelixToolkit.SharpDX.Core.Wpf,
ScottPlot.WPF, PureHDF and xUnit.

No local paths are hard-coded. Settings live in `%APPDATA%\GeoVisionPro\settings.json` and logs in `%APPDATA%\GeoVisionPro\logs`.

## Windows (Visual Studio)

1. Open `GeoVisionPro.slnx`.
2. Optional: build the native engine first (see below) so that `gvnative.dll`/`gvvtk.dll` are copied next to the executable.
3. Set **GeoVisionPro.Desktop** as the startup project and run (F5).
4. Run the tests with Test Explorer, or:

```bash
dotnet test tests/GeoVisionPro.Tests
```

## Command line (any OS)

```bash
dotnet build GeoVisionPro.slnx
```

```bash
dotnet test tests/GeoVisionPro.Tests
```

On Linux and macOS, the WPF project compiles thanks to `EnableWindowsTargeting` (set in `Directory.Build.props`), but it
runs only on Windows. Everything else, including the tests, runs on all platforms.

Skip the slow scalability test with:

```bash
dotnet test tests/GeoVisionPro.Tests --filter "Category!=Performance"
```

## Native engine

Windows, compute engine only:

```bash
cmake -S native/GeoVisionPro.NativeEngine -B native/build -A x64 -DGV_WITH_VTK=OFF
```

```bash
cmake --build native/build --config Release
```

```bash
ctest --test-dir native/build -C Release --output-on-failure
```

Add the VTK renderer by pointing CMake at a VTK 9 build (built with RenderingVolumeOpenGL2):

```bash
cmake -S native/GeoVisionPro.NativeEngine -B native/build -A x64 -DVTK_DIR=C:/vtk/build/lib/cmake/vtk-9.3
```

Linux:

```bash
sudo apt-get install cmake g++ libvtk9-dev
```

```bash
cmake -S native/GeoVisionPro.NativeEngine -B native/build -DCMAKE_BUILD_TYPE=Release && cmake --build native/build -j
```

The test project copies `native/build/libgvnative.so` (Linux) or `native/build/Release/gvnative.dll` (Windows) when present,
so the native/managed parity test runs against the C++ code. Without the library, the managed fallback is used and tested.

## Sample data

Regenerate the synthetic validation dataset (deterministic):

```bash
dotnet run --project tools/GeoVisionPro.SampleData -- samples/synthetic --inlines 40 --crosslines 50
```

The desktop application can also build a complete project from the generator: **File > Create Synthetic Sample Project**.

## Release build and installer (Windows)

```powershell
pwsh build/build-release.ps1 -Version 0.8.0
```

The script:

1. Builds and tests the native engine.
2. Builds and tests the solution.
3. Publishes a self-contained win-x64 application to `artifacts/publish` (with the native DLLs, documentation and sample data).
4. Builds `installer/GeoVisionPro.Installer.wixproj` into an MSI under `installer/bin`.

The MSI installs to Program Files, adds a Start menu shortcut and associates `.gvp` files with the application.

## Continuous integration

`.github/workflows/ci.yml` defines two jobs:

- **Linux:** native engine, solution build and tests.
- **Windows:** release script and MSI artifact.
