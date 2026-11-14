# Scientific algorithms

For each algorithm, this document gives the formulation, inputs and units, assumptions, validation (the automated test that
checks it against an analytical or known answer) and limitations. Unless stated otherwise:

- Times are two-way times in seconds (the UI shows milliseconds).
- Depths are metres, either measured depth (MD) or true vertical depth below sea level (TVDSS, positive down).
- Velocities are in m/s, densities in g/cc and moduli in GPa.
- Seismic polarity follows the **SEG normal convention**: an increase in acoustic impedance gives a positive reflection
  coefficient, which a zero-phase wavelet displays as a positive peak.

Test names refer to `tests/GeoVisionPro.Tests`.

## 1. Signal processing core (`Core.Numerics`)

| Algorithm | Formulation | Validation | Limitations |
|---|---|---|---|
| FFT | Math.NET Numerics with the MATLAB convention: forward unscaled, inverse scaled by 1/N. Arbitrary lengths are supported (Bluestein). | `Fft_RoundTrip_ReconstructsSignal` | — |
| Analytic signal and Hilbert transform | Zero-padded FFT; negative frequencies zeroed, positive frequencies doubled (Marple 1999). | `Hilbert_OfCosine_IsSine` | Edge effects within about one period of the trace ends. |
| Constant phase rotation | y = x cos φ − H[x] sin φ | `PhaseRotation_By180_InvertsPolarity` | — |
| Levinson–Durbin | Solves Toeplitz R f = g by recursion with the prediction-error filter update. | `Levinson_MatchesDenseSolve` | R must be positive definite (pre-whitening ensures this). |
| Zero-phase filters | Real response H(f) applied in the frequency domain after mirror padding. Ormsby trapezoid (cosine ramps), Butterworth (\|H\| = HP·LP of order n), notch (raised cosine). | `BandPass_RemovesOutOfBandSine` | Non-causal. Ramps narrower than the frequency resolution can ring. |
| Wavelets | Ricker: (1 − 2π²f²t²) e^(−π²f²t²). Ormsby: band-limited trapezoid. Windowed-sinc resampling; constant-phase estimation by maximising the rotated centre sample. | `Ormsby_HasUnitPeak…`, `Wavelet_EstimateConstantPhase…` | — |
| Interpolation | Linear; 8-point Hann-windowed sinc; 3-point parabolic peak refinement. | `SincInterpolation_IsExactOnBandLimitedSignal` | — |
| Statistics | Percentiles by linear interpolation; OLS regression; Pearson and Spearman correlation; polynomial fit (Math.NET). | `LinearRegression_RecoversLine`, `Statistics_Percentiles` | — |
| Conjugate gradient | Matrix-free CG for symmetric positive-definite operators. | `ConjugateGradient_SolvesSpdSystem` | — |
| Delaunay | Bowyer–Watson incremental triangulation. | `Delaunay_TriangulatesGrid` | O(n²) worst case; intended for ≤ 20k points. |
| Grid ↔ map transform | X = X0 + A11 Δil + A12 Δxl, Y = Y0 + A21 Δil + A22 Δxl; least-squares fit from ≥ 3 control points. | `GridTransform_RoundTrip_AndControlPointFit` | Affine only: a regular, possibly rotated grid. |

## 2. SEG-Y (`Seismic.Segy`)

- **IBM float**: value = (−1)^s · M · 16^(e−64) / 2^24, with a table-driven exponent. The reverse conversion normalises the
  hexadecimal mantissa and rounds to nearest. Tests: `IbmToIeee_KnownValues` (−118.625 = 0xC276A000, 100, 1, 0) and
  `IeeeToIbm_RoundTrip`.
- **Byte order**: the rev-2 constant 16909060 at bytes 3297–3300 is used when present; otherwise the order that gives a valid
  sample-format code. **Textual header**: EBCDIC (code page 037) is detected statistically.
- **Geometry reconstruction**: every trace header is read and inline/crossline steps are the GCD of consecutive unique values.
  Key bytes are auto-detected by scoring candidate pairs on grid fill, the distinct-bin ratio and offset uniqueness within bins
  (so trace sequence numbers and channel numbers are not mistaken for keys).
  Tests: `Scanner_DetectsNonStandardKeyBytes`, `Gathers_WriteRead_RoundTrip`, `WriteRead_RoundTrip_PreservesGeometryAndSamples`.
- **Coordinates**: the coordinate scalar convention is applied (positive multiplies, negative divides). With no coordinates, a
  nominal 25 m grid is used and a warning is issued.
- **Limitations**: variable-length traces (rev 2) and the variable number of extended textual headers (−1) are not supported;
  the file is treated as fixed-length and a warning is issued. SEG-D is not implemented.

## 3. Processing (`SeismicProcessing`)

| Operation | Formulation | Validation | Assumptions and limitations |
|---|---|---|---|
| AGC | y(t) = x(t) / (RMS over a window centred on t + ε) | `Agc_NormalisesRms` | Destroys relative amplitudes. Not for AVO or inversion. |
| Spiking deconvolution | (R + ε r0 I) f = r(1..L); y(t) = x(t) − Σ f_j x(t−1−j); RMS restored | `SpikingDecon_CompressesMinimumPhaseWavelet` | Minimum-phase wavelet, white reflectivity, stationary design window. |
| Predictive deconvolution | Gapped prediction-error filter: Σ f_j r(i−j) = r(i+α) | `PredictiveDecon_AttenuatesPeriodicMultiples` | Periodic multiples with period > α. |
| Spectral whitening | X(f) / (smoothed \|X(f)\| + stabilisation) inside the Ormsby band; phase kept; RMS restored | `SpectralWhitening_FlattensSpectrumInBand` | Amplifies noise at the band edges. |
| F-K filter | 2D FFT; fan weight on the apparent velocity \|f/k\| with a cosine taper | `FkFilter_RemovesSlowLinearEvent` | Regular trace spacing; spatially aliased energy is not removed (the test uses unaliased data). |
| F-X deconvolution | Overlapping time windows (rectangular analysis, Hann blending); per-frequency complex forward and backward least-squares prediction across traces, averaged | `FxDecon_ImprovesSignalToNoise` | Events linear within a window; smears curved events. |
| NMO | t_x = √(t0² + x²/v_rms(t0)²), sinc interpolation, stretch mute (t_x − t0)/t0 > s; inverse NMO by fixed point | `Nmo_FlattensHyperbola_…` | Hyperbolic moveout (no anisotropy or higher-order terms). |
| Semblance | S = Σ_w (Σ_x a)² / (N Σ_w Σ_x a²) (Neidell & Taner 1971); auto-pick of the maximum per window | same test (peak at the true velocity ± 100 m/s) | Window length trades resolution against stability. |
| Dix | v_int,n² = (t_n v_n² − t_{n−1} v_{n−1}²)/(t_n − t_{n−1}) | `Dix_RoundTrip` | Unstable for noisy v_rms; non-physical values are clamped. |
| Parabolic Radon | Damped least squares per frequency: m = (LᴴL + μI)⁻¹ Lᴴ d with L = e^(−iωq(x/x_ref)²); the model for q > q_cut is subtracted | `Radon_RemovesParabolicMultiple_KeepsFlatPrimary` | Apply after NMO. Cost O(n_f·n_q³) per gather. |
| Kirchhoff time migration | √(−iω) prefilter; image(x0,τ) = Σ_x (τ/t)/√t · d(x, t), t = √(τ² + 4h²/v²), aperture and dip limits | `Migration_CollapsesDiffraction` | 2D inline by inline (no crossline migration); approximate amplitudes, normalised to input RMS; no anti-alias filter. |
| Phase-shift migration | Gazdag (1978): P ← P e^(iωΔτ√(1−(v k_x/2ω)²)), image at t = 0; v_int from Dix | same test | Exact for v(τ) only; evanescent energy dropped. |
| NMO stack | NMO followed by a mean over live samples | `NmoStack_OfSyntheticGathers_MatchesZeroOffsetModel` | — |
| Trace editing | Listed traces killed; RMS > k × median killed; NaN/Inf killed | — | — |

**Depth migration** is not implemented (phase-shift migration in time only). See IMPLEMENTATION_STATUS.md.

## 4. Wells (`WellLog`)

- **LAS**: 2.0 header parsing follows the CWLS layout (MNEM.UNIT VALUE : DESCRIPTION), supports wrapped data, null values,
  decreasing depth (reversed) and LAS 3.0 `~Log_Definition/~Log_Data` and `~Tops`. Tests: `Parse_Las20_…`, `Parse_WrappedLas`,
  `Parse_Las30_LogAndTops`, `Write_Read_RoundTrip`.
- **Minimum curvature**: ΔN = ΔMD/2 (sin I1 cos A1 + sin I2 cos A2) RF, ΔE likewise, ΔTVD = ΔMD/2 (cos I1 + cos I2) RF,
  RF = (2/β) tan(β/2). Tests: vertical, constant-inclination and quarter-circle build (`MinimumCurvature_*`).
- **Conditioning**: despiking (running median ± k·1.4826·MAD), NaN-aware smoothing, bounded gap filling, two-point percentile
  normalisation, depth matching by correlation search (returns the shift to apply). Tests: `Despike_…`, `Normalize_…`,
  `DepthShift_IsFoundByCorrelation`.
- **Acoustic impedance**: AI = ρ / DT, with slowness converted to s/m. Test: `AcousticImpedance_FromSonicAndDensity`.

## 5. Time-depth (`Checkshot`)

- The model holds monotonic (TVDSS, TWT) knots relative to the seismic reference datum (SRD elevation). Interpolation is linear.
  Extrapolation uses the last interval velocity, or the average velocity from the SRD above the first knot.
- Interval velocity v_int = 2ΔZ/ΔT; average velocity v_avg = 2 Z_srd / T; RMS velocity from the interval velocities.
- **Sonic integration**: TWT(z) = t0 + Σ (s_i + s_{i−1}) Δz_vertical, where t0 comes from the replacement velocity or an anchor
  point. Test: `SonicIntegration_MatchesAnalyticTime`.
- **Drift calibration**: drift(z) = t_cs − t_sonic at each checkshot, interpolated linearly. Calibrated time = t_sonic + drift.
  Corrected slowness = s + ½ d(drift)/dz. Test: `Calibration_HonoursCheckshots` (residual < 1 ms).
- QC flags interval velocities outside [1300, 7000] m/s.

## 6. Well tie (`WellTie`)

- Logs are blocked to the seismic sample interval. Slowness is averaged (time-consistent) and density averaged arithmetically.
- r_i = (Z_{i+1} − Z_i)/(Z_{i+1} + Z_i) is placed at sample i+1. The synthetic r * w is **advanced by half a sample** (sinc),
  because each coefficient belongs to the interface at t_i + Δt/2. Without this correction a systematic Δt/2 delay appears
  (about 20° of apparent phase at 30 Hz with Δt = 4 ms).
- The seismic trace is sampled along the borehole (deviated wells supported), optionally averaged over (2r+1)² traces.
- **Statistical wavelet**: amplitude spectrum = √(FFT of the average normalised autocorrelation), with zero or user phase and a
  Hann taper. **Deterministic wavelet**: min ‖s − r*w‖² + λ‖w‖², whose Toeplitz normal equations are solved with Levinson.
  Tests: `LeastSquaresWavelet_RecoversTrueWavelet` (r > 0.98), `StatisticalWavelet_HasCorrectBandwidth`.
- **Optimisation**: bulk shift by correlation search; constant phase by a 5° then 1° search; automatic stretch and squeeze by
  local cross-correlation at knots, with maximum shift and maximum strain (monotonic mapping). Tests: `BulkShift_IsRecovered`,
  `PhaseRotation_IsRecovered`, `Synthetic_AtTrueTdr_CorrelatesWithSeismic`.
- Limitations: the convolutional model assumes normal incidence, a 1D earth and a stationary wavelet. Phase and small time
  shifts trade off for band-limited data.

## 7. Interpretation (`Interpretation`)

- **Snapping**: the nearest peak or trough (parabolic refinement) or zero crossing (linear) within ± window samples.
- **Auto-tracker**: breadth-first flood fill from seeds. A neighbour is accepted if an event of the same type is found within
  the window, the time jump is ≤ the maximum jump, and the normalised cross-correlation over ± h samples is ≥ the threshold.
  Test: `AutoTracker_FollowsSyntheticHorizon` (> 95% within 8 ms on noisy data). Undo and redo restore exactly.
- **Gridding**: IDW (bucketed search); Laplace (∇²z = 0) and minimum curvature (biharmonic 13-point stencil) by Gauss–Seidel,
  initialised from IDW, with data cells fixed. Test: `Gridding_FillsHolesInSmoothSurface` (max error: minimum curvature
  < 0.005, Laplace < 0.01, IDW < 0.08 on a smooth surface).
- **Faults**: sticks resampled by arc length (time scaled into bins) and triangulated as ribbons between sticks ordered along
  the principal strike direction. Fault–horizon intersection is a sign change of (stick time − horizon time). Throw is the
  difference between horizon values sampled perpendicular to the strike. Test: `FaultSurface_And_Throw`.
- **Flattening**: traces are shifted so that the horizon sits at a reference time. Test: `Flatten_PutsHorizonAtReferenceTime`.
- **ZMAP+ and XYZ I/O**: round-trip tests.

## 8. Attributes (`Attributes`)

| Attribute | Formulation | Validation |
|---|---|---|
| Envelope, phase, cosine of phase | \|x + iH[x]\|, atan2(H, x), x/\|a\| | `Envelope_OfModulatedSine_RecoversEnvelope` |
| Instantaneous frequency | (x h′ − h x′)/(2π(x² + h²)) with envelope-weighted smoothing of numerator and denominator | `InstantaneousFrequency_OfSine` |
| Windowed amplitudes | RMS, mean absolute, maximum, minimum, maximum absolute, energy, mean in a centred window | catalog test |
| Spectral decomposition | Gaussian-windowed STFT coefficient at f: 2\|Σ x g e^(−i2πft)\| / Σg; phase | `SpectralDecomposition_PicksMatchingFrequency` |
| Dominant frequency | Peak of the Hann-windowed local spectrum | catalog test |
| Attenuation | Least-squares slope of ln\|A(f)\| over [f1, f2] (relative indicator; not Q) | catalog test |
| Coherence | Semblance over 3×3 traces in a vertical window. **Dip-steered** by structure-tensor dips refined iteratively on aligned neighbours (Marfurt et al. 1998 style). | `Coherence_IsLowAtFault` (> 0.85 on a flank dipping 1.7 samples/trace, lower at the fault) |
| Variance | Σ(a − ā)²/Σa² (not dip-steered) | catalog test |
| Dip and azimuth | p = −⟨∂x a ∂t a⟩/⟨(∂t a)²⟩ (ms/m); azimuth from the inline axis | `Dip_OfPlaneWave` |
| Chaos | 2λ2/(λ1 + λ3) − 1 from the eigenvalues of the 3D structure tensor (trigonometric solver) | `ChaosEigenvalues_OfDiagonalMatrix` |
| Edge | Sobel lateral gradient / local RMS | catalog test |
| Sweetness | envelope / √max(f_inst, 1 Hz) | catalog test |
| Relative impedance | Cumulative sum + Butterworth low-cut | catalog test |
| Horizon curvature | Quadratic fit (Roberts 2001) with a = ½∂²z/∂x², b = ½∂²z/∂y², c, d, e from central differences; mean, Gaussian, maximum, minimum, most-positive, most-negative curvature; dip and azimuth | `Curvature_OfParaboloid_MatchesAnalytic` (exact k and k²) |

Neighbourhood attributes use the 3 nearest inlines and crosslines. Volume curvature (from dip fields) is not implemented.

## 9. Inversion (`Inversion`)

Forward model: s = W r, with r_i ≈ ½ Δln Z_i (small-contrast approximation). The seismic is divided by a scaler (estimated
from wells by least squares between the trace and the well synthetic).

| Method | Formulation | Validation |
|---|---|---|
| Recursive | ln Z_{i+1} = ln Z_i + ln((1+r)/(1−r)) (exact recursion), with the trace taken as r | `Recursive_OnPureReflectivity_RecoversImpedanceExactly` |
| Band-limited | High-passed integrated trace plus the low-frequency model below the merge frequency | `Inversion_RecoversBlockyImpedance(BandLimited)` r > 0.9 |
| Model-based | min ‖W D L − s‖² + μ‖L − L0‖², solved by CG on the normal equations; optional hard bounds ±p% around the model | r > 0.9 and RMS error below the model's |
| Sparse-spike | min ½‖W r − s‖² + λ‖r‖₁ by FISTA (Beck & Teboulle 2009), least-squares de-biasing, then merge with the model | r > 0.85 and RMS error below the model's |
| Low-frequency model | Well AI blocked in time; IDW between wells; proportional (stratigraphic) time mapping between horizons; Butterworth low-pass in the log domain | `LowFrequencyModel_InterpolatesWellsAndHonoursCutoff` |
| Simultaneous pre-stack | Unknowns L_p = ln Ip, ΔL_s, ΔL_d around the trends ln Is = k L_p + k_c and ln ρ = m L_p + m_c; Fatti coefficients ½(1+tan²θ), −4K sin²θ, −(½tan²θ − 2K sin²θ); regularised CG (Hampson, Russell & Bankhead 2005) | `SimultaneousInversion_RecoversIpAndIs` (Ip r > 0.85, Is r > 0.7) |
| Elastic impedance | EI = Vp^(1+tan²θ) Vs^(−8K sin²θ) ρ^(1−4K sin²θ) (Connolly 1999); normalised EI (Whitcombe 2002) | `ElasticImpedance_AtZeroAngle_EqualsAcousticImpedance` |

End-to-end validation: `EndToEnd_SampleProject_ImportProcessTieInvertReport` inverts the synthetic survey and recovers the true
impedance trace with r > 0.85 at the crest well.

Limitations: the wavelet is stationary; the low-frequency model uses IDW (no kriging) along proportional layers; density from
simultaneous inversion is poorly constrained below about 35° maximum angle. The application labels all outputs as estimates.

## 10. AVO (`AVO`)

- **Zoeppritz P-P**: the exact Aki & Richards (1980) expression, complex beyond the critical angle.
  Test: `Zoeppritz_NormalIncidence_EqualsImpedanceContrast`.
- **Aki–Richards, Shuey (2 and 3 terms), Fatti**. Test: `Approximations_AgreeWithZoeppritzAtModerateAngles`. Errors grow with
  angle and contrast: for a gas sand with about 50% Vs contrast, < 0.005 up to 10°, < 0.015 at 20° and < 0.03 at 30°.
- **Intercept/gradient**: least squares on sin²θ (optionally a third term). Classes I, II, IIp, III, IV with a ±0.02 threshold
  on A. Fluid factor uses the mudrock relation with Gardner density. Offset to angle uses a straight ray through v_rms (Walden 1991).
  Tests: `InterceptGradientFit_AndClassification`, `OffsetToAngle_StraightRay`, `ModelledAngleGather_AmplitudesFollowZoeppritz`.

## 11. Rock physics (`RockPhysics`)

Voigt, Reuss and Hill averages; Hashin–Shtrikman bounds; Gassmann K_sat = K_dry + (1 − K_dry/K_min)² / (φ/K_fl + (1−φ)/K_min −
K_dry/K_min²), with μ unchanged, and its inverse; fluid substitution with a density update; Wood fluid mixing; Batzle & Wang
(1992) brine; Wyllie; Gardner ρ = 0.31 Vp^0.25; Castagna mudrock; Greenberg–Castagna; Hertz–Mindlin; soft sand (modified lower HS
bound); contact cement (Dvorkin & Nur 1996, contact-cement scheme). Tests check the Gassmann round trip, the effect of
substitution, bound ordering, derived moduli, empirical relations, physical brine values and the pressure/porosity trends.

Assumptions: Gassmann requires isotropic, homogeneous mineral, connected pores and low frequency. Granular models assume
identical spheres.

## 12. Petrophysics (`Petrophysics`)

I_GR = (GR − GR_clean)/(GR_shale − GR_clean). Vsh: linear, Larionov Tertiary 0.083(2^(3.7I) − 1) and older 0.33(2^(2I) − 1),
Clavier, Steiber. Porosity: density, Wyllie sonic, Raymer, neutron-density RMS. φ_e = φ_t − Vsh·φ_sh. Saturation: Archie,
Simandoux and Indonesia. Net reservoir and net pay use cut-offs on Vsh, φ_e and Sw. Tests: `Archie_KnownValue` (also checks
that Simandoux and Indonesia reduce to Archie for clean rock), `Vshale_Methods…`, `Evaluation_FlagsGasSandAsPay`. The
application warns when Archie is used in shaly intervals.

## 13. Crossplot and classification (`Crossplot`)

Polygon selection (even-odd rule), regression and correlation statistics, k-means with k-means++ initialisation on
standardised features, and Gaussian quadratic discriminant analysis with class priors (posteriors are reported as an
uncertainty measure). Tests: `KMeans_SeparatesClusters`, `GaussianClassifier_ClassifiesTrainingClasses`, `Selection_And_Statistics`.

## 14. Geological modelling (`GeologicalModeling`)

Surfaces are sorted by depth, gaps filled by Laplace interpolation, and crossings truncated to the surface above. Stratigraphic
grids use proportional or follow-top/base layering. Well logs are upscaled into cells by arithmetic averaging. Properties are
distributed by IDW or ordinary kriging (spherical, exponential or Gaussian variogram; pseudo-inverse of the kriging system)
layer by layer. Tests: `Framework_And_StratigraphicGrid`, `Kriging_HonoursDataAndIsBounded`, `Variogram_Models`.
Depth conversion uses a layer cake with IDW average velocities from the wells.

## 15. Synthetic validation dataset

`SyntheticSurveyGenerator` builds a faulted anticline with dip, a gas cap in the reservoir sand above a gas-water contact, and a
fault plane that dips consistently in both the model and the fault sticks. Reflection coefficients are computed on an 8× finer
grid and placed at sub-sample interface times. Wells (GR, RHOB, NPHI, DT, DTS, RT), checkshots, tops and pre-stack gathers
(hyperbolic moveout, Zoeppritz amplitudes) are derived from the same model, so every workflow has a known answer.
