// Native engine self test: validates IBM conversion, convolution and AGC against analytical values.
#include "gv_engine.h"
#include <cmath>
#include <cstdio>

static int failures = 0;
static void check(bool ok, const char* what) {
    std::printf("%s: %s\n", ok ? "PASS" : "FAIL", what);
    if (!ok) ++failures;
}

int main() {
    // IBM 0xC276A000 = -118.625 (classic reference value)
    const unsigned char ibm[4] = {0xC2, 0x76, 0xA0, 0x00};
    float v = 0;
    gv_ibm_to_ieee(ibm, &v, 1, 1);
    check(std::fabs(v + 118.625f) < 1e-5f, "IBM -> IEEE (-118.625)");

    const float x[5] = {0, 0, 1, 0, 0};
    const float w[3] = {1, 2, 3};
    float out[5];
    gv_convolve_same(x, 5, w, 3, 1, out);
    check(out[1] == 1 && out[2] == 2 && out[3] == 3, "convolution with centred wavelet");

    float t[100];
    for (int i = 0; i < 100; ++i) t[i] = 5.0f * std::sin(0.3f * i);
    gv_agc_rms(t, 100, 21);
    double s = 0;
    for (int i = 20; i < 80; ++i) s += t[i] * t[i];
    check(std::fabs(std::sqrt(s / 60) - 1.0) < 0.1, "AGC normalises RMS to ~1");
    std::printf("version %d, openmp %d\n", gv_version(), gv_has_openmp());
    return failures == 0 ? 0 : 1;
}
