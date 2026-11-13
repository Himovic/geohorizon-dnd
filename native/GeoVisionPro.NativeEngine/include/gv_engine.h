// GeoVision Pro native compute engine - C ABI (stable, P/Invoke friendly).
#pragma once
#include <stdint.h>

#if defined(_WIN32)
#  if defined(GV_BUILDING_DLL)
#    define GV_API __declspec(dllexport)
#  else
#    define GV_API __declspec(dllimport)
#  endif
#else
#  define GV_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/// Engine ABI version (major * 10000 + minor * 100 + patch).
GV_API int32_t gv_version(void);
/// 1 when compiled with OpenMP multithreading.
GV_API int32_t gv_has_openmp(void);
/// Decodes n IBM System/360 floats (raw bytes, big- or little-endian words) into IEEE floats.
GV_API void gv_ibm_to_ieee(const uint8_t* src, float* dst, int64_t n, int32_t big_endian);
/// In-place RMS AGC with a centred window of `window` samples.
GV_API void gv_agc_rms(float* trace, int32_t n, int32_t window);
/// "Same"-mode convolution of x (nx) with w (nw) whose time-zero sample is `center`.
GV_API void gv_convolve_same(const float* x, int32_t nx, const float* w, int32_t nw, int32_t center, float* out);
/// Semblance velocity spectrum of a CMP gather [ntraces x ns] (row-major) for nv trial velocities; out is [nv x ns].
GV_API void gv_semblance(const float* gather, int32_t ntraces, int32_t ns, const double* offsets, const double* velocities,
                         int32_t nv, double dt, double t0, int32_t window, float* out);

#ifdef __cplusplus
}
#endif
