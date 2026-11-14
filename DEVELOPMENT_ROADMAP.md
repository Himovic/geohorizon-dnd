# Development roadmap

The product is built in eight milestones. Each one ends with a compiling solution, passing tests and updated documentation.
All eight milestones have been started in version 0.8. The exact state of each feature is in IMPLEMENTATION_STATUS.md.

| # | Milestone | State | Acceptance criterion | Evidence |
|---|---|---|---|---|
| 1 | Application foundation | Implemented; UI untested at run time | Create a project, import a real SEG-Y, inspect headers, view an inline, save | Persistence and SEG-Y tests; `EndToEnd_SampleProject…` |
| 2 | Visualization engine | Implemented (VTK path partial) | Load a 3D volume and explore it in 2D and 3D | Viewers, map and 3D view compile; rendering core tested |
| 3 | Interpretation | Implemented | Interpret and save horizons and faults | Tracking, gridding, fault and I/O tests; persistence round trip |
| 4 | Well analysis and tie | Implemented | Tie a well with logs and checkshots | Tie tests (shift, phase, wavelets, correlation > 0.85) |
| 5 | Seismic processing | Implemented (no depth or 3D migration) | Processing outputs can be viewed and compared | Processing tests; before/after UI; provenance tests |
| 6 | Inversion and AVO | Implemented (pre-stack volume inversion pending) | Generate and validate impedance from seismic and calibrated wells | Inversion tests and end-to-end inversion (r > 0.85 against the true model) |
| 7 | Reservoir characterization | Implemented (3D model display pending) | Integrated characterization workflow | Petrophysics, rock physics, crossplot, modelling and report tests |
| 8 | Production readiness | In progress | Installer, packaging, licensing, profiling | WiX project, licence validation tests, CI workflow, performance test |

## Next iteration (0.9)

1. **Windows validation.** Run every view, fix run-time XAML and rendering issues, and add UI smoke tests (FlaUI).
2. Run the CI workflow. Build the MSI and gvnative/gvvtk on Windows, then test VTK hosting.
3. Velocity field interpolation (multiple CDP picks) for NMO, stacking and migration; 3D Kirchhoff time migration.
4. Volume-wide pre-stack simultaneous inversion and AVO attribute volumes.
5. Interactive well-tie knot editing and log curve editing in the log viewer.
6. Persist and restore the AvalonDock layout (AvalonDock serialisation package).
7. Interactive transfer-function editor for VTK and Helix volume rendering.

## Later (1.x)

- Depth migration (phase-shift plus split-step, then Kirchhoff PSDM) with a velocity model builder.
- Kriged low-frequency models, multi-volume 3D blending, 3D display of geological grids, fault-aware gridding.
- GPU acceleration (CUDA or DirectCompute) of attributes, migration and inversion; memory profiling under multi-GB datasets.
- SEG-D import, RESQML/OpenVDS exchange, cross-platform viewer (Avalonia) reusing the UI-independent renderers.
- Licence enforcement per module, signed installers, auto-update.
