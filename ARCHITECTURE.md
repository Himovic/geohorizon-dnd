# GeoVision Pro architecture

## Principles

- **Clean architecture.** Scientific libraries have no UI dependencies and depend only on `Core` and `Domain`. The application layer
  orchestrates use cases. Infrastructure and data access implement the application's abstractions. The WPF desktop is the
  composition root.
- **One data model.** `GeoVisionPro.Domain` defines projects, seismic datasets, survey geometry, wells, curves, tops, time-depth
  surveys, horizons, faults, wavelets, ties, provenance records, workflows, reports, models and crossplots. All modules exchange these types.
- **Testable algorithms.** Each algorithm is a pure function or a small class over arrays, so tests can validate it on
  synthetic models with known answers (137 xUnit tests).
- **No full-volume loading.** Seismic data is read on demand, either from memory-mapped SEG-Y or from chunked Zarr bricks
  through a bounded LRU cache. Processing streams inline by inline.
- **Provenance and reproducibility.** Every derived dataset references a `ProcessingRecord` that stores the operation, the
  algorithm version, JSON parameters, inputs, outputs, timestamps, status and messages.

## Projects and dependencies

```
Core ──────────────┐  numerics (FFT via Math.NET, DSP, filters, wavelets, statistics, solvers), spatial (transforms,
                   │  polygons, Delaunay), units, primitives (Array2D, Grid2D, ranges, progress, validation), parameters
Domain ────────────┤  entities and SurveyGeometry
Seismic ───────────┤  SEG-Y encoding/headers/reader/scanner/writer, ISeismicVolume/IGatherDataset, InMemory/Segy/Zarr volumes
SeismicProcessing ─┤  operation framework + catalog, trace/ensemble operations, NMO/semblance, migration, engine
WellLog ───────────┤  LAS reader/writer, curve families, conditioning, ASCII import, minimum-curvature trajectory
Checkshot ─────────┤  TimeDepthModel, sonic integration, drift calibration (→ WellLog)
WellTie ───────────┤  time-domain logs, reflectivity, synthetic, wavelet estimation, WellTieSession (→ Checkshot, Seismic)
Interpretation ────┤  snapping, auto-tracking, manual picking, gridding, flattening, faults, horizon/fault I/O
Attributes ────────┤  trace and neighbourhood attributes, engine, horizon extraction, curvature
Inversion ─────────┤  low-frequency model, post-stack inversion, simultaneous pre-stack inversion, EI (→ WellTie)
AVO ───────────────┤  reflectivity equations, AVO analysis, angle-gather modelling
RockPhysics ───────┤  mixing laws, bounds, Gassmann, granular-medium models, fluids
Petrophysics ──────┤  Vsh, porosity, saturation, evaluation, net pay
Crossplot ─────────┤  crossplot data, selection, statistics, k-means, Gaussian facies classifier
Visualization2D ───┤  colour maps, section/map rasteriser (BGRA buffers, UI-independent)
Visualization3D ───┤  scene meshes (horizons, faults, slices), transfer functions, slice-stack volume rendering
GeologicalModeling ┤  framework, stratigraphic grid, upscaling, IDW/kriging, depth conversion
NativeEngine ──────┤  P/Invoke bindings to gvnative (compute) and gvvtk (VTK renderer) with managed fallback
Reporting ─────────┘  PDF writer, report builder, XLSX/CSV export
Application         ProjectSession, services (import, interpretation, processing, wells, workflows, export, report, samples),
                    background tasks, undo/redo, message log, synthetic survey generator
DataAccess          EF Core SQLite DbContext and repository (IProjectRepository), HDF5 exchange (PureHDF)
Infrastructure      settings, rolling file logger, license validation, DI registration
Desktop (WPF)       MVVM shell, AvalonDock workspace, ribbon and menus, custom controls, HelixToolkit 3D, ScottPlot charts
Tests               xUnit (unit, numerical validation, integration, persistence, performance)
```

## Project storage

A project consists of two items side by side:

| Item | Content |
|---|---|
| `<name>.gvp` | SQLite database (EF Core). Metadata of every object. Log samples, horizon grids and wavelets are stored as little-endian binary blobs. Deviation surveys, checkshot points and fault sticks are stored as JSON columns. The schema version is kept in `PRAGMA user_version`. The previous file is kept as `.gvp.bak` on every save. |
| `<name>.gvdata/` | Bulk data. `seismic/<id>/` holds Zarr v2 arrays (`.zarray`, `.zattrs` with the survey geometry, chunk files `i.j.k`, zlib-compressed little-endian float32, plus `live.bin` with live-trace flags), readable by zarr-python. It also holds generated gather SEG-Y files, reports, sample files and `workspace.json`. |

Linked SEG-Y datasets keep an absolute path and the byte-location settings (`ImportSettingsJson`), and are reopened through memory mapping.

## Seismic access

- `SegyFile` maps the whole file read-only (`MemoryMappedFile`, a raw pointer, thread-safe reads). It decodes only the traces that are requested.
- `SegyScanner` reads every trace header once to build a trace index `[inline][crossline][fold]`. It handles missing bins, duplicates and gathers, and fits the grid-to-map affine transform by least squares.
- `ZarrVolume` stores chunks of 32×32×128 by default. A bounded LRU cache (512 MB by default, configurable) and dirty-chunk buffering keep memory bounded while writing.
- `ISeismicVolume` exposes `TryReadTrace`, `ReadInline`, `ReadCrossline` and `ReadTimeSlice`. Extension methods provide arbitrary lines with bilinear interpolation, statistics and copying.

## Execution model

- Long operations run through `BackgroundTaskManager` (Task.Run, `IProgress<ProgressInfo>` throttled to 10 updates per second, `CancellationToken`). Each task appears in the *Background Tasks* pane with progress and a Cancel button.
- Engines parallelise across inlines (`Parallel.For` with a configurable degree). Writes to stores are serialised.
- The UI thread only renders. Views read data through `Task.Run` and cancel stale requests (for example, while the inline slider moves).

## Rendering

| Path | Use |
|---|---|
| `SectionRenderer` (C#) → `WriteableBitmap` | 2D sections, slices and maps. Only the visible viewport is rasterised, at device resolution. Wiggle and variable-area drawing is rasterised as well. |
| `SeismicSectionControl`, `MapControl`, `WellLogControl`, `TimeTracksControl` | Custom WPF `FrameworkElement`s (`OnRender`) for overlays, axes and interaction. |
| HelixToolkit SharpDX (DirectX 11) | 3D scene built from the UI-independent `SceneMesh` model of `Visualization3D`. |
| `gvvtk` (VTK 9, C++) hosted in an `HwndHost` | GPU ray-cast volume rendering (`vtkSmartVolumeMapper`). Optional: when the DLL is absent, the UI explains how to build it. |
| ScottPlot 5 | Charts (spectra, crossplots, time-depth, AVO, QC), wrapped by `ChartHelper`. |

## Native engine

`native/GeoVisionPro.NativeEngine` builds with CMake:

- `gvnative` (C ABI): IBM to IEEE conversion, RMS AGC, convolution and semblance, parallelised with OpenMP.
- `gvvtk`: the VTK scene bridge (create/resize, set volume, transfer function, meshes, polylines, camera, screenshots).

`NativeCompute` probes the library at run time and falls back to managed implementations. Results are identical within
floating-point tolerance (test `NativeAndManaged_GiveIdenticalResults`). Set `GEOVISION_DISABLE_NATIVE=1` to force managed code.

## Extensibility

- Processing operations implement `IProcessingOperation` (trace, section or gather scope) and declare `ParameterDescriptor`s. The UI generates their parameter forms, and `ParameterSet` validates and serialises them. Add an operation to `ProcessingCatalog` to expose it in the processing editor, workflows and provenance.
- Attributes implement `ISeismicAttribute` (trace or 3×3 neighbourhood scope) and register in `AttributeCatalog`.
- Import formats go in `Seismic`, `WellLog` (`AsciiWellImport`) or `Interpretation` (`InterpretationIO`); export formats go in `ExportService`, `Reporting` or `Hdf5Exchange`.
- Persistence lives behind `IProjectRepository`, which can be replaced (for example, by a server database).

## UI composition

`App` builds a `Microsoft.Extensions.Hosting` host. It registers infrastructure, application services, UI services (dialogs,
theme, cursor bus), `MainViewModel` and `MainWindow`. Documents are view-models rendered through implicit `DataTemplate`s
(`Views/DataTemplates.xaml`) inside AvalonDock's `DocumentsSource`. Tool panes (Explorer, Data Manager, Properties,
Interpretation, Messages, Tasks) are anchorables. Themes are colour dictionaries swapped at run time (`ThemeService`). The
workspace (open documents) is saved per project.
