// GeoVision Pro native compute engine.
// Numerically identical (to float rounding) to the managed implementations in GeoVisionPro.Core / SeismicProcessing,
// which remain the reference; the managed side falls back automatically when this library is not present.
#include "gv_engine.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

namespace {
inline uint32_t load_u32(const uint8_t* p, bool big_endian) {
    return big_endian ? (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | uint32_t(p[3])
                      : (uint32_t(p[3]) << 24) | (uint32_t(p[2]) << 16) | (uint32_t(p[1]) << 8) | uint32_t(p[0]);
}

struct IbmTable {
    double scale[128];
    IbmTable() {
        for (int e = 0; e < 128; ++e) scale[e] = std::pow(16.0, e - 64) / 16777216.0;
    }
};
const IbmTable kIbm;

inline float linear_sample(const float* tr, int n, double idx) {
    if (idx < 0 || idx > n - 1) return std::nanf("");
    int i0 = static_cast<int>(idx);
    if (i0 >= n - 1) return tr[n - 1];
    double t = idx - i0;
    return static_cast<float>(tr[i0] * (1 - t) + tr[i0 + 1] * t);
}
}  // namespace

extern "C" {

int32_t gv_version(void) { return 800; }

int32_t gv_has_openmp(void) {
#if defined(GV_HAVE_OPENMP)
    return 1;
#else
    return 0;
#endif
}

void gv_ibm_to_ieee(const uint8_t* src, float* dst, int64_t n, int32_t big_endian) {
#if defined(GV_HAVE_OPENMP)
#pragma omp parallel for if (n > 65536)
#endif
    for (int64_t i = 0; i < n; ++i) {
        uint32_t w = load_u32(src + 4 * i, big_endian != 0);
        uint32_t mant = w & 0x00FFFFFFu;
        if (mant == 0) { dst[i] = 0.0f; continue; }
        int exp = static_cast<int>((w >> 24) & 0x7F);
        double v = mant * kIbm.scale[exp];
        dst[i] = static_cast<float>((w & 0x80000000u) ? -v : v);
    }
}

void gv_agc_rms(float* trace, int32_t n, int32_t window) {
    if (n <= 0) return;
    int h = window / 2;
    std::vector<double> cum(static_cast<size_t>(n) + 1, 0.0);
    for (int i = 0; i < n; ++i) cum[i + 1] = cum[i] + double(trace[i]) * trace[i];
    double global_rms = std::sqrt(cum[n] / std::max(1, n));
    double eps = global_rms * 1e-6 + 1e-30;
    for (int i = 0; i < n; ++i) {
        int a = std::max(0, i - h), b = std::min(n - 1, i + h);
        double rms = std::sqrt((cum[b + 1] - cum[a]) / (b - a + 1));
        trace[i] = static_cast<float>(trace[i] / (rms + eps));
    }
}

void gv_convolve_same(const float* x, int32_t nx, const float* w, int32_t nw, int32_t center, float* out) {
    for (int i = 0; i < nx; ++i) {
        // out[i] = full[i + center], full[k] = sum_j x[k - j] * w[j]
        int k = i + center;
        double s = 0;
        int j0 = std::max(0, k - (nx - 1)), j1 = std::min(nw - 1, k);
        for (int j = j0; j <= j1; ++j) s += double(x[k - j]) * w[j];
        out[i] = static_cast<float>(s);
    }
}

void gv_semblance(const float* gather, int32_t ntraces, int32_t ns, const double* offsets, const double* velocities,
                  int32_t nv, double dt, double t0, int32_t window, float* out) {
#if defined(GV_HAVE_OPENMP)
#pragma omp parallel for schedule(dynamic)
#endif
    for (int iv = 0; iv < nv; ++iv) {
        std::vector<double> num(ns), den(ns);
        std::vector<int> cnt(ns);
        double v = velocities[iv];
        for (int k = 0; k < ns; ++k) {
            double tz = t0 + k * dt, s = 0, s2 = 0;
            int live = 0;
            for (int r = 0; r < ntraces; ++r) {
                double tx = std::sqrt(tz * tz + offsets[r] * offsets[r] / (v * v));
                double idx = (tx - t0) / dt;
                if (idx > ns - 1) continue;
                float a = linear_sample(gather + static_cast<size_t>(r) * ns, ns, idx);
                if (std::isnan(a)) continue;
                s += a; s2 += double(a) * a; ++live;
            }
            num[k] = s * s; den[k] = s2; cnt[k] = live;
        }
        int h = window / 2;
        for (int k = 0; k < ns; ++k) {
            double sn = 0, sd = 0; int nn = 0;
            for (int w = std::max(0, k - h); w <= std::min(ns - 1, k + h); ++w) { sn += num[w]; sd += den[w]; nn = std::max(nn, cnt[w]); }
            out[static_cast<size_t>(iv) * ns + k] = (sd > 0 && nn > 0) ? static_cast<float>(sn / (nn * sd)) : 0.0f;
        }
    }
}

}  // extern "C"
