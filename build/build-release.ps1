<#
  GeoVision Pro release build (Windows).
  1. Builds the native engine (gvnative, optional gvvtk when VTK_DIR is set) with CMake/MSVC.
  2. Builds and tests the .NET solution.
  3. Publishes the desktop application (self-contained, win-x64) to artifacts/publish.
  4. Builds the MSI installer with WiX v5 (dotnet build installer/GeoVisionPro.Installer.wixproj).
  Usage: pwsh build/build-release.ps1 [-Version 0.8.0] [-VtkDir C:\vtk\lib\cmake\vtk-9.3] [-SkipTests]
#>
param(
    [string]$Version = "0.8.0",
    [string]$VtkDir = $env:VTK_DIR,
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "== Native engine" -ForegroundColor Cyan
$cmakeArgs = @("-S", "native/GeoVisionPro.NativeEngine", "-B", "native/build", "-A", "x64")
if ($VtkDir) { $cmakeArgs += "-DVTK_DIR=$VtkDir" } else { $cmakeArgs += "-DGV_WITH_VTK=OFF" }
cmake @cmakeArgs
cmake --build native/build --config Release
ctest --test-dir native/build -C Release --output-on-failure

Write-Host "== .NET build" -ForegroundColor Cyan
dotnet build GeoVisionPro.slnx -c Release
if (-not $SkipTests) { dotnet test tests/GeoVisionPro.Tests -c Release --no-build }

Write-Host "== Publish" -ForegroundColor Cyan
$publish = Join-Path $root "artifacts/publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src/GeoVisionPro.Desktop/GeoVisionPro.Desktop.csproj -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publish
Copy-Item native/build/Release/gvnative.dll $publish -ErrorAction SilentlyContinue
Copy-Item native/build/Release/gvvtk.dll $publish -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $publish "docs") | Out-Null
Copy-Item docs/*.md (Join-Path $publish "docs")
Copy-Item *.md (Join-Path $publish "docs")
New-Item -ItemType Directory -Force (Join-Path $publish "samples") | Out-Null
Copy-Item samples/synthetic (Join-Path $publish "samples") -Recurse

Write-Host "== Installer" -ForegroundColor Cyan
dotnet build installer/GeoVisionPro.Installer.wixproj -c Release -p:ProductVersion=$Version -p:PublishDir="$publish\"
Get-ChildItem installer/bin -Recurse -Filter *.msi | ForEach-Object { Write-Host "Installer: $($_.FullName)" -ForegroundColor Green }
