# Implementation status (version 0.8)

Legend: **Complete** = implemented, integrated in the UI, covered by automated tests, limitations documented.
**Partial** = implemented and working, with known gaps listed. **Pending** = not implemented. The application does not show
placeholder results for pending items.

> **Verification scope.** The libraries, the native engine and the persistence layer were built and tested on Linux:
> 137 xUnit tests and the native self-test pass. The WPF desktop project **compiles** (including XAML markup compilation), but
> it has **not been run**: WPF cannot execute on Linux and no Windows machine was available. The UI is therefore unverified
> at run time; see "Known issues". The first task of the next iteration is a Windows run-through of every view.

## Milestone 1 – Foundation

| Feature | Status | Notes |
|---|---|---|
| Solution (23 projects, .slnx), clean architecture, DI host | Complete | |
| Project create/open/save/save-as/close (SQLite + data folder), backups | Complete | Persistence round-trip tested |
| SEG-Y reader (rev 0/1/2 fixed-length, all common formats, both byte orders) | Complete | Variable-length traces and the −1 extended-header count: pending |
| SEG-Y header inspection (EBCDIC, binary, trace headers, trace data) | Complete | SEG-Y browser view |
| Automatic key-byte detection, geometry reconstruction, validation | Complete | |
| Link (memory-mapped) or copy into a Zarr chunked store | Complete | |
| LAS 2.0/3.0 reader and writer, CSV/ASCII logs, tops, deviation, checkshots | Complete | LAS 3.0: log and tops sections only |
| Data import manager, drag-and-drop (SEG-Y, LAS, .gvp) | Complete | |
| Project explorer, data manager, properties grid | Complete | Not run on Windows yet |
| Initial 2D seismic viewer | Complete | |

## Milestone 2 – Visualization

| Feature | Status | Notes |
|---|---|---|
| Inline, crossline, time slice, arbitrary line, gather viewers | Complete | Depth slices work for depth-domain datasets |
| Display modes, colour maps, clipping, gain, AGC, polarity, PNG export | Complete | |
| Linked crosshair, navigation, orthogonal line, measurement | Complete | |
| Horizon, fault, well and top overlays | Complete | |
| Base map with horizon and attribute maps; arbitrary-line digitising | Complete | |
| 3D scene (Helix DirectX): slices, horizons, faults, wells, frame, exaggeration, camera modes, screenshots | Complete | Not run on Windows yet |
| Volume rendering: slice stack with a transfer function (Helix) | Complete | Axis-aligned slices (no view-aligned resampling) |
| Volume rendering: GPU ray casting (VTK bridge) | Partial | Bridge compiles and links against VTK 9.1 on Linux; the Windows HWND hosting and rendering are untested; no interactive transfer-function editor yet |
| Well log display in 3D | Pending | Paths and tops only |
| Multiple volumes blended in one 3D scene | Partial | One seismic volume per scene, plus all interpretation objects |

## Milestone 3 – Interpretation

| Feature | Status | Notes |
|---|---|---|
| Manual picking with snapping; seeded 3D/2D auto-tracking | Complete | |
| Gridding (IDW, Laplace, minimum curvature), smoothing, flattening, comparison | Complete | Flattened-section *view* in the UI: pending (library function available) |
| Fault sticks, surfaces, intersection, throw | Complete | Fault-aware gridding: pending |
| Undo/redo of interpretation edits | Complete | |
| Seismic attributes (22) and horizon maps, curvature | Complete | Volume curvature: pending |

## Milestone 4 – Wells and well tie

| Feature | Status | Notes |
|---|---|---|
| Multi-track logs, correlation panels with flattening | Complete | Interactive curve editing (drawing on curves): pending; conditioning operations available in the library |
| Checkshots: velocities, sonic integration, drift calibration, QC | Complete | |
| Synthetic, wavelets (Ricker, statistical, least squares), well tie with shift, phase, polarity, stretch/squeeze | Complete | Manual knot editing by dragging: pending (automatic stretch/squeeze available) |
| Save tie and calibrated TDR | Complete | |

## Milestone 5 – Processing

| Feature | Status | Notes |
|---|---|---|
| Filtering, AGC, normalisation, deconvolution, whitening, mutes, trace edit | Complete | |
| F-K filter, F-X deconvolution, Radon demultiple | Complete | |
| Velocity analysis (semblance, picking), NMO, stack | Complete | Single-CDP velocity function applied to all gathers (velocity field interpolation: pending) |
| Post-stack time migration (Kirchhoff, phase-shift) | Partial | 2D per inline; 3D migration pending |
| Depth migration | Pending | |
| Workflows (JSON), provenance history | Complete | Linear pipelines; graphical node editor pending |
| Coherent noise suppression beyond F-K/Radon (e.g. adaptive subtraction) | Pending | |

## Milestone 6 – Inversion and AVO

| Feature | Status | Notes |
|---|---|---|
| Recursive, band-limited, model-based, sparse-spike inversion | Complete | |
| Low-frequency model from wells and horizons | Complete | IDW only (kriging LFM pending) |
| Inversion QC at wells, scaler estimation | Complete | |
| Simultaneous pre-stack inversion | Partial | Single-gather inversion in the UI; volume-wide pre-stack inversion pending |
| Elastic impedance | Complete (library) | No dedicated UI |
| AVO analysis, classification, modelling, intercept–gradient crossplot | Complete | AVO attribute volumes (A, B, fluid factor) pending |

## Milestone 7 – Reservoir characterization

| Feature | Status | Notes |
|---|---|---|
| Crossplots (logs and volumes), selection, statistics, clustering, facies classification | Complete | |
| Petrophysics evaluation and net pay | Complete | Multi-mineral solver pending |
| Rock physics: Gassmann on logs, templates | Complete | |
| Geological modelling: framework, grids, upscaling, kriging | Partial | 2D layer display only; 3D grid display and faulted grids pending |
| Reporting: PDF, XLSX, CSV; LAS, SEG-Y, XYZ, ZMAP+, HDF5 export | Complete | |

## Milestone 8 – Production readiness

| Feature | Status | Notes |
|---|---|---|
| Automated tests (unit, numerical, integration, persistence, performance) | Partial | 137 xUnit tests + native self-test; UI automation tests pending |
| Error recovery (global handlers, error log, .bak project backup, cancellation) | Complete | |
| Installer (WiX v5 MSI, file association, shortcuts) | Partial | Project and script written; not built (requires Windows) |
| Icon and branding, dark and light themes | Complete | |
| Licensing architecture (RSA-signed licence files) | Partial | Validation implemented and tested; no vendor key or enforcement (all modules enabled) |
| CI workflow (Linux libraries/tests, Windows release + MSI) | Partial | Written, not yet run |
| Documentation | Complete | |
| Memory profiling, GPU/CUDA acceleration | Pending | Native engine uses OpenMP on CPU only |

## Known issues and risks

1. **UI not run on Windows.** Run-time XAML problems (resource lookups, AvalonDock layout behaviour, HelixToolkit device
   creation) cannot be excluded until the application is launched.
2. HelixToolkit.SharpDX.Core.Wpf 2.27 targets .NET Core 3.1 and is used on .NET 10. It restores and compiles; run-time
   compatibility is not verified.
3. Workspace persistence restores documents (viewers, map, 3D, logs, history), not the exact dock layout (AvalonDock 5 moved
   layout serialisation into separate packages).
4. The project file is rewritten completely on every save. This is fine for thousands of curves; very large projects
   may need incremental saves.
5. Arbitrary lines on surveys with irregular outlines use nearest-bin interpolation at the survey edges.
6. Opening a viewer on a *linked* SEG-Y dataset after reopening a project rescans its trace headers on the UI thread
   (seconds for multi-million-trace files). Datasets copied into the chunked store open instantly.

## Performance characteristics

- SEG-Y access is memory-mapped: only the traces that are read are paged in. A 60 MB, 40 000-trace file is scanned in about
  0.1 s and converted to Zarr in under 1 s (test `LargeSegy_ScanConvertSlice_WithBoundedMemory`).
- Zarr stores keep memory bounded: an LRU chunk cache (512 MB by default) plus a cap on dirty chunks while writing.
- Time slices from Zarr touch one chunk layer. From linked SEG-Y they read every trace, so time slicing is slow for large
  linked files; copying into the project store is recommended.
- The Radon transform costs O(n_f·n_q³) per gather. The Kirchhoff migration costs O(n_x·n_t·aperture) per inline.
- 2D rendering rasterises only the visible viewport at device resolution.
