# GeoVision Pro

GeoVision Pro is a Windows desktop application for seismic interpretation, seismic processing, well-to-seismic calibration,
seismic inversion, AVO and reservoir characterization. It is written in C# (.NET 10, WPF, MVVM) with a native C++ compute
engine and an optional VTK rendering bridge. Every module works from one geophysical data model, so a user can load a survey,
import wells, interpret, process, tie, invert and characterize a reservoir without leaving the application.

> **Status: version 0.8 (development).** All eight milestones have been started. The scientific libraries are
> implemented and validated by 137 automated tests (plus a native self-test) against synthetic models with known answers. The WPF desktop application
> compiles in full but has **not yet been run on Windows**: the development environment for this version was Linux, where WPF
> can be compiled but not executed. See [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) for what is complete, partial or pending.

## Capabilities

| Area | What is implemented |
|---|---|
| Data management | SEG-Y rev 0/1/2 reader (IBM/IEEE/integer formats, either byte order, EBCDIC/ASCII headers), header inspection, automatic inline/crossline byte detection, geometry reconstruction (2D, 3D, gathers), coordinate transform fitting, validation. Import can link the file through memory mapping or copy it into a compressed chunked Zarr v2 store. LAS 2.0 (including wrapped) and LAS 3.0 log/tops sections, CSV/ASCII logs, tops, deviation surveys and checkshots. Horizons as XYZ, IL/XL/Z or ZMAP+; fault sticks. SQLite project database; HDF5 export. |
| 2D visualization | Inline, crossline, time slice, arbitrary line and gather viewers: variable density, wiggle, wiggle variable area, colour maps, clipping, gain, display AGC, polarity. Horizon, fault, well and top overlays; linked crosshair across viewers; zoom/pan; measurement; PNG export. Base map with horizon and attribute maps. |
| 3D visualization | DirectX 11 scene (HelixToolkit): textured inline/crossline/time-slice planes, horizon and fault surfaces, well paths with tops, survey frame, slice-stack volume rendering with an opacity transfer function, vertical exaggeration, perspective or orthographic camera. GPU ray-cast volume rendering through the native VTK bridge (`gvvtk`) when it is deployed. |
| Processing | Scaling, polarity, DC removal, detrend, Ormsby band-pass, Butterworth low/high-pass, notch, RMS AGC, RMS/peak normalisation, smoothing, spiking and predictive (gapped) Wiener–Levinson deconvolution, spectral whitening, top/bottom mute, trace editing, F-K velocity filter, F-X deconvolution, lateral mix, Kirchhoff and phase-shift post-stack time migration, NMO with stretch mute, offset mute, parabolic Radon demultiple, semblance velocity analysis with auto-picking, NMO stack. Before/after preview with spectra, provenance records, reusable JSON workflows. |
| Interpretation | Manual picking snapped to peak, trough or zero crossing; seeded 3D or line-constrained auto-tracking; IDW, Laplace and minimum-curvature gridding; smoothing; flattening; horizon differences; fault sticks, fault surfaces, fault–horizon intersection and throw; undo/redo; import and export. |
| Attributes | Envelope, instantaneous phase/frequency, cosine of phase, phase rotation, windowed amplitudes (RMS, mean absolute, maximum, minimum, maximum absolute, energy), dominant frequency, STFT spectral magnitude and phase, attenuation slope, coherence (dip-steered semblance), variance, dip/azimuth (structure tensor), edge detection, chaos, sweetness, relative acoustic impedance; horizon extraction maps; horizon curvature and dip. |
| Wells | Multi-track log display and correlation panels with flattening on tops; despiking, smoothing, gap filling, unit conversion, depth shift and matching, normalisation; minimum-curvature trajectories. |
| Checkshots | Time-depth models (TVDSS/TWT), interval, average and RMS velocities, sonic integration, drift-curve calibration, drift-corrected sonic, replacement velocity, QC. |
| Well tie | Time-domain blocking of sonic and density logs, reflectivity, convolutional synthetic, trace extraction along the borehole, Ricker/Ormsby, statistical and least-squares deterministic wavelets, bulk shift and phase optimisation, polarity, automatic stretch and squeeze, correlation QC, saved tie and calibrated time-depth relation. |
| Inversion | Recursive, band-limited, model-based (regularised least squares by conjugate gradients, hard constraints) and sparse-spike (FISTA, L1) acoustic impedance inversion. Horizon-guided low-frequency model from wells. Scaler estimation, QC against wells. Pre-stack simultaneous inversion (Fatti, background trends) for Ip, Is and density with derived Vp/Vs, λρ, μρ and Poisson's ratio. Elastic impedance. |
| AVO/AVA | Exact Zoeppritz P-P, Aki–Richards, Shuey (2 and 3 terms), Fatti; intercept/gradient fitting; Rutherford–Williams/Castagna classes; fluid factor; offset-to-angle conversion; angle-gather modelling; intercept–gradient crossplots. |
| Rock physics | Voigt, Reuss and Hill averages, Hashin–Shtrikman bounds, Gassmann and inverse Gassmann, fluid substitution, Wood mixing, Batzle–Wang brine, Wyllie, Gardner, Castagna mudrock, Greenberg–Castagna, Hertz–Mindlin, soft sand, contact cement. |
| Petrophysics | Vshale (linear, Larionov, Clavier, Steiber), porosity (density, sonic, Raymer, neutron-density), Archie, Simandoux and Indonesia saturation, effective porosity, net reservoir and net pay with cut-offs, lithology indicator. |
| Crossplots | Well-log and seismic-volume crossplots, colour coding, polygon selection, regression, Pearson and Spearman statistics, k-means clustering, Gaussian (QDA) facies classification with posteriors, facies logs, CSV/PNG export. |
| Geological modelling | Structural framework from horizons with crossing resolution, proportional or follow-top/base stratigraphic grids, log upscaling, IDW or ordinary kriging property distribution, variograms, zone volumes, time-to-depth conversion of horizons. |
| Reporting | PDF technical report (dependency-free writer) with tables and figures, XLSX and CSV export, processing history export. |

## Repository layout

```
GeoVisionPro.slnx               solution (Visual Studio 2026 / dotnet CLI)
src/GeoVisionPro.*              23 projects (see ARCHITECTURE.md)
native/GeoVisionPro.NativeEngine C++ compute engine (gvnative) and VTK bridge (gvvtk), CMake
tests/GeoVisionPro.Tests        xUnit unit, numerical-validation, integration and performance tests
tools/GeoVisionPro.SampleData   synthetic dataset generator (command line)
samples/synthetic               generated sample data: SEG-Y stack and gathers, LAS wells, checkshots, tops, horizons, faults
installer/                      WiX v5 MSI project
build/                          release (Windows) and developer (Linux) build scripts
docs/USER_GUIDE.md              user documentation
```

## Quick start

```bash
dotnet build GeoVisionPro.slnx
```

```bash
dotnet test tests/GeoVisionPro.Tests
```

On Windows, run `src/GeoVisionPro.Desktop` (startup project in Visual Studio), then use **File > Create Synthetic Sample Project**
or import `samples/synthetic/synthetic_stack.sgy` and the LAS files. The full procedure, including the native engine, VTK and the
installer, is in [BUILD_INSTRUCTIONS.md](BUILD_INSTRUCTIONS.md).

## Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md): layers, modules, data model, storage, threading, rendering
- [SCIENTIFIC_ALGORITHMS.md](SCIENTIFIC_ALGORITHMS.md): formulation, inputs, units, assumptions, validation and limitations of every algorithm
- [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md): completed, partial and pending features, known issues, performance limits
- [DEVELOPMENT_ROADMAP.md](DEVELOPMENT_ROADMAP.md): milestones 1–8
- [BUILD_INSTRUCTIONS.md](BUILD_INSTRUCTIONS.md): prerequisites, builds, native engine, installer
- [CHANGELOG.md](CHANGELOG.md)
- [docs/USER_GUIDE.md](docs/USER_GUIDE.md)

## Scientific integrity

Inverted impedances, elastic properties, AVO attributes, rock-physics predictions and petrophysical results are **estimates**
that depend on the wavelet, the low-frequency model, the calibration and the model assumptions documented in
SCIENTIFIC_ALGORITHMS.md. The application labels them as such in reports and dataset descriptions. Features that are not yet
implemented are listed as pending; the application does not produce placeholder results for them.
