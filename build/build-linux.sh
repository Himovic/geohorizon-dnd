#!/usr/bin/env bash
# Linux/macOS developer build: native engine + all cross-platform libraries + tests.
# The WPF desktop project is compiled (EnableWindowsTargeting) but can only run on Windows.
set -euo pipefail
cd "$(dirname "$0")/.."
cmake -S native/GeoVisionPro.NativeEngine -B native/build -DCMAKE_BUILD_TYPE=Release
cmake --build native/build -j"$(nproc)"
ctest --test-dir native/build --output-on-failure
dotnet build GeoVisionPro.slnx -c Release
dotnet test tests/GeoVisionPro.Tests -c Release --no-build
