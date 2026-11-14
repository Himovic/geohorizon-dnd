# GeoVision Pro user guide

## 1. Workspace

- **Menu and ribbon** (top): every function is reachable from the menus (File, Project, Data, Seismic, Processing,
  Interpretation, Wells, Attributes, Inversion, AVO/AVA, Crossplot, Rock Physics, Petrophysics, Geological Modeling, View, Tools,
  Window, Help). The ribbon tabs (Home, Seismic, Interpretation, Wells, Reservoir) hold the most frequent tools.
- **Left:** Project Explorer (hierarchical tree with filter; right-click for actions, double-click to open) and Data Manager
  (tables of surveys, wells, horizons and faults).
- **Centre:** documents (viewers and tools). Drag tabs to split, dock or float them.
- **Right:** Properties (editable fields of the selected object) and Interpretation (active horizon/fault and picking parameters).
- **Bottom:** Messages (info, warnings, errors) and Background Tasks (progress, cancellation).
- **Status bar:** cursor readout (inline, crossline, time, X/Y, amplitude), task progress, memory, native engine status.
- **Theme:** View > Toggle Dark/Light (Ctrl+T).

Keyboard shortcuts:

| Keys | Action |
|---|---|
| Ctrl+N, Ctrl+O, Ctrl+S | New, open, save project |
| Ctrl+I, Ctrl+L | Import SEG-Y, import LAS |
| F5, F6, F7 | Inline, crossline, time slice viewer |
| F8, F9 | Map, 3D view |
| Ctrl+Z, Ctrl+Y | Undo, redo |
| PgUp, PgDn | Previous, next line in a viewer |
| Home | Reset zoom in a viewer |
| Esc | Cancel a drawing |

## 2. Projects

**File > New Project** creates `<name>.gvp` (metadata database) and a `<name>.gvdata` folder (seismic stores, reports). Save
often (Ctrl+S); the previous version is kept as `.gvp.bak`. **File > Create Synthetic Sample Project** builds a complete
demonstration project (seismic stack, gathers, three wells, horizons, a fault) from a known earth model. Use it to learn the
workflows and to validate results.

Set the seismic reference datum and replacement velocity with **Project > Project Settings** (Properties pane).

## 3. Loading data

- **SEG-Y** (Data > Import SEG-Y, Ctrl+I, or drag and drop): the SEG-Y browser shows the textual header, the binary header,
  the trace headers of the first traces and the trace data. Check the detected inline/crossline/X/Y/scalar/offset bytes,
  optionally override the sample format or interval, and use **Scan geometry** to validate the survey. Then choose:
  - *LinkMemoryMapped*: no copy; the original file is read on demand.
  - *CopyToChunkedStore* (recommended for large volumes): compressed bricks in the project, fast slices in every direction.
  Pre-stack files are recognised automatically (several traces per bin with different offsets). Tick *angle gathers* if the
  offset field holds angles.
- **Wells:** import LAS files (several at once). Curves with the same well name or UWI are merged into one well. Set or check
  the surface X/Y and KB in Properties.
- **Tops:** Data > Import Well Tops, as a CSV with columns Well, Top, MD.
- **Deviation:** select a well, then Data > Import Deviation Survey (MD, INC, AZI).
- **Checkshots:** select a well, then Data > Import Checkshots (MD, TWT in ms; OWT is detected from the header).
- **Horizons:** XYZ (map X Y Z), IL XL Z, or ZMAP+ grids. Z is in milliseconds for time surveys. **Faults:** stick files.

## 4. Viewing seismic

Open a viewer from the explorer (double-click), the Seismic menu or the ribbon.

- Navigate with the slider, the arrow buttons or PgUp/PgDn.
- Zoom with the mouse wheel (Ctrl: traces only; Shift: time only). Pan by dragging (Navigate tool) or with the middle button.
- Adjust the colour map, display mode (variable density, wiggle, wiggle variable area, combined), clip percentile, gain,
  display AGC and polarity.
- Overlays: horizons (the active one is thicker), faults, well paths and tops.
- The crosshair is linked across all viewers and the map. **Orthogonal line** opens the crossing line at the cursor.
  Double-click a time slice to open the inline there.
- **Arbitrary lines:** in the Base Map, press *Draw arbitrary line*, click vertices and double-click to finish.
- **Spectrum** shows the average amplitude spectrum of the displayed section.

## 5. Interpretation

1. **Interpretation > New Horizon** (it becomes the active horizon in the Interpretation pane).
2. In a viewer, select the **Pick** tool:
   - Click successive points along the event; each segment is interpolated and snapped to the chosen event (peak, trough,
     zero crossing) within the search window.
   - **Shift+click:** seed 3D auto-tracking (flood fill controlled by minimum correlation and maximum jump).
   - **Ctrl+click:** track along the current line only.
   - **Right-click:** restart the pick chain.
3. **Erase** tool: drag across traces to delete picks.
4. Interpretation pane: fill gaps (Laplace, minimum curvature, IDW) and smooth.
5. **Faults:** Interpretation > New Fault, then the **Draw** tool. Click points along the fault and double-click to end the stick.
   Fault surfaces appear in the 3D view.
6. Undo or redo any edit with Ctrl+Z / Ctrl+Y. Save the project to persist interpretation.

Horizon maps: in the Base Map, choose a horizon and a map type (time, amplitude, RMS, dip, curvature, picking source).

## 6. Wells and time-depth

- **Well Logs:** tick wells for a correlation panel and flatten on a top. Wheel scrolls, Ctrl+wheel zooms, drag pans. The cursor
  reads out all curve values.
- **Checkshots:** time-depth, interval/average/RMS velocity and drift plots. **Calibrate sonic and store** adds a calibrated TDR
  and a drift-corrected sonic. **Set as active TDR** chooses the relation used everywhere (viewers, tie, inversion).

## 7. Well-to-seismic tie

Wells > Well-to-Seismic Tie:

1. Choose the well, the seismic and a wavelet: Ricker, a project wavelet, statistical (extracted at the well) or deterministic
   (least squares from well reflectivity and seismic).
2. Tracks show velocity, density, impedance, reflectivity, synthetic, seismic and residual. The wavelet and the
   cross-correlation are plotted on the right; the correlation and quality rating appear at the top.
3. Adjust the bulk shift and phase (sliders), the polarity, the trace averaging radius and the correlation window. Or use
   **Optimise shift**, **Optimise phase** and **Stretch/squeeze** (automatic, bounded strain).
4. **Save tie** stores the tie record, the wavelet and the tied TDR (optionally made active). **Save wavelet** adds the rotated
   wavelet to the project for inversion.

## 8. Processing

Processing > Processing Sequence:

1. Choose the input (post-stack or gathers) and add operations to the sequence. Edit parameters (units and ranges are
   validated); enable or disable steps; reorder them.
2. The preview shows the selected inline (or a gather) before and after, with spectra.
3. **Run on dataset** creates a new dataset with a provenance record (Project > Processing History).
   **Save as workflow** stores the sequence.

**Velocity analysis:** select a CDP, compute the semblance, then click picks or use auto-pick. The NMO-corrected gather updates
live. **NMO + stack all gathers** creates a stack.

**Workflow Manager:** build workflows that mix processing and attribute steps; save them to the project, export or import JSON,
and execute them on any dataset.

## 9. Attributes

Attributes > Seismic Attributes: choose an attribute and its parameters, check the inline preview, then **Compute attribute
volume**. For maps, choose a horizon, a window mode (at the horizon, a window around it, or between horizons) and a statistic,
then **Extract map** (shown in the Base Map).

## 10. Inversion

Inversion > Inversion Workflow:

1. Seismic and wavelet (preferably the well-tie wavelet).
2. Calibrated wells (with an active TDR) and horizons guiding the low-frequency model.
3. Low-frequency high-cut (typically 8–12 Hz). **Build LF model volume** lets you inspect it.
4. Method and parameters. **Estimate** computes the seismic scaler from the wells.
5. **Invert trace at QC well** compares inverted, well and model impedance with statistics. **Run inversion on volume** writes
   the impedance volume.
6. Pre-stack: choose gathers, a location and an RMS velocity (for NMO and angles). Wells need DTS. **Invert gather** estimates
   Ip, Is, density and Vp/Vs.

Inverted properties are estimates. Always compare them with wells that were left out of the low-frequency model.

## 11. AVO/AVA

AVO/AVA > AVO Analysis and Modelling: choose gathers and a location, an RMS velocity, the pick time and peak/trough. The plot
shows amplitude versus sin²θ with the intercept–gradient fit, the class and the fluid factor. **Intercept-gradient crossplot**
fits every gather. The two-layer model compares Zoeppritz with the Aki–Richards and Shuey approximations, with optional
Gassmann brine-to-gas substitution of the lower layer.

## 12. Crossplots, rock physics, petrophysics, modelling

- **Crossplot:** well logs (wells, X/Y/colour curves, depth range) or seismic volumes. Draw a polygon (toggle, click points, then
  Close) to select points, assign facies and **Classify** (Gaussian, with posteriors). Or use k-means. **Write FACIES logs**
  stores the classification in the wells.
- **Petrophysics:** choose the Vsh, porosity and saturation models and their parameters, evaluate, and review the net pay
  summary. VSH, PHIT, PHIE, SW, SHC, NETFLAG and LITHO curves are added to the well.
- **Rock physics:** Gassmann substitution on the logs (brine to gas/oil/brine at a target Sw), Batzle–Wang brine, and template
  curves (Wyllie, soft sand, contact cement, Hashin–Shtrikman) with the well data overlaid.
- **Geological model:** select horizons, set layers per zone and decimation, choose a well property and an IDW or kriging
  variogram, then build. Layer maps and zone volumes are reported.

## 13. 3D and volume rendering

The 3D View shows textured inline/crossline/time slices (sliders), horizons, faults, wells with tops and the survey frame,
with vertical exaggeration. Rotate with right-drag, pan with middle-drag (or Shift+right-drag) and zoom with the wheel.
**Render volume** builds a semi-transparent slice-stack rendering. *Volume Rendering (VTK)* uses GPU ray casting when the
VTK bridge is installed.

## 14. Reports and export

Tools > Report Generator writes a PDF (project, surveys with a section figure, wells and tie QC, horizons, faults, provenance,
conclusions) and an XLSX workbook of the tables. Export selected objects as SEG-Y, LAS, XYZ, ZMAP+ or HDF5 from the Data menu.
Viewers and charts export PNG images.

## 15. Troubleshooting

- Messages and errors are listed in the Messages pane and logged under `%APPDATA%\GeoVisionPro\logs` (Tools > Open Log Folder).
- Unexpected errors are reported and logged without closing the application. Save your work and check `errors.log`.
- If a linked SEG-Y file was moved, the project reports the missing path on opening. Update the dataset's StoragePath in
  Properties, or re-import the file.
