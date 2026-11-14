# Changelog

## 0.8.0 – initial development release

### Added
- Solution with 23 projects (clean architecture), central package management, .NET 10, C# 14.
- Core numerics (FFT, Hilbert, Levinson, zero-phase filters, wavelets, statistics, CG, Delaunay, transforms, units).
- SEG-Y rev 0/1/2 I/O with memory-mapped random access, header inspection, key detection, geometry reconstruction, writer
  and streaming writer. Zarr v2 chunked volume store with an LRU cache.
- Processing toolbox (24 operations), semblance velocity analysis, NMO stack, Kirchhoff and phase-shift migration, workflows,
  provenance.
- LAS 2.0/3.0 I/O, ASCII well imports, log conditioning, minimum-curvature trajectories.
- Checkshot calibration, synthetic seismograms, wavelet estimation, interactive well tie.
- Horizon picking and auto-tracking, gridding, flattening, fault modelling, horizon/fault I/O (XYZ, ZMAP+).
- 22 seismic attributes, including dip-steered coherence, structure-tensor dip and chaos; horizon maps and curvature.
- Post-stack inversion (recursive, band-limited, model-based, sparse-spike), low-frequency models, simultaneous pre-stack
  inversion, elastic impedance.
- AVO equations (Zoeppritz, Aki–Richards, Shuey, Fatti), intercept/gradient, classification, modelling.
- Rock physics, petrophysics, crossplot analytics with facies classification, geological modelling with kriging.
- PDF/XLSX/CSV reporting, HDF5 export.
- SQLite project persistence with backup, background tasks with cancellation, undo/redo, message log, settings, file
  logging, licence validation.
- WPF desktop: AvalonDock workspace, ribbon and menus, dark/light themes, project explorer, data manager, properties,
  interpretation controls, messages, tasks, 2D viewers, map, 3D view (Helix), VTK host, and documents for processing,
  workflows, velocity analysis, attributes, well logs, checkshots, well tie, inversion, AVO, crossplot, rock physics,
  petrophysics, geological model, SEG-Y browser, history and reports.
- Native C++ engine (OpenMP) and VTK 9 rendering bridge (CMake), with managed fallback.
- Synthetic validation dataset generator and committed sample data.
- 137 automated tests (xUnit) plus a native self-test; WiX v5 installer project; release script; CI workflow; documentation.

### Fixed during development (found by the validation tests)
- Half-sample delay of synthetic seismograms (reflection coefficients placed at t_i + Δt instead of t_i + Δt/2).
- Horizon curvature coefficients were a factor of 2 too small (Roberts fit).
- Plain semblance coherence failed on steep dips; replaced by dip-steered coherence.
- SEG-Y key detection could select trace-sequence or channel numbers for gathers.
- F-X deconvolution amplified noise at window edges (synthesis weighting).
- Point-sampled synthetic model quantised event times; reflections are now placed at sub-sample times.
