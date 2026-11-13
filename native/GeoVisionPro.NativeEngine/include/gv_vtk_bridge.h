// GeoVision Pro VTK rendering bridge - C ABI used by the WPF host (HwndHost) on Windows.
#pragma once
#include "gv_engine.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct gvtk_scene gvtk_scene;

/// Creates a VTK render window embedded in the native parent window (HWND on Windows, may be null for off-screen).
GV_API gvtk_scene* gvtk_create(void* parent_window, int32_t width, int32_t height, int32_t offscreen);
GV_API void gvtk_destroy(gvtk_scene* s);
GV_API void gvtk_resize(gvtk_scene* s, int32_t width, int32_t height);
GV_API void gvtk_set_background(gvtk_scene* s, double r, double g, double b);
/// Sets a scalar volume (x fastest) with spacing and origin; replaces any previous volume. Returns 0 on success.
GV_API int32_t gvtk_set_volume(gvtk_scene* s, const float* data, int32_t nx, int32_t ny, int32_t nz,
                               double sx, double sy, double sz, double ox, double oy, double oz);
/// Transfer function: n control points of (value, r, g, b, opacity), components in [0,1] except value.
GV_API void gvtk_set_transfer_function(gvtk_scene* s, const double* points, int32_t n);
GV_API void gvtk_set_volume_visible(gvtk_scene* s, int32_t visible);
/// Adds a triangle mesh with per-vertex RGBA colours (may be null for a uniform colour). Returns an id.
GV_API int32_t gvtk_add_mesh(gvtk_scene* s, const float* xyz, int32_t nverts, const int32_t* triangles, int32_t ntris,
                             const uint8_t* rgba, double r, double g, double b, double opacity);
GV_API int32_t gvtk_add_polyline(gvtk_scene* s, const float* xyz, int32_t npts, double r, double g, double b, double width);
GV_API void gvtk_remove_actor(gvtk_scene* s, int32_t id);
GV_API void gvtk_clear(gvtk_scene* s);
GV_API void gvtk_set_parallel_projection(gvtk_scene* s, int32_t on);
GV_API void gvtk_reset_camera(gvtk_scene* s);
GV_API void gvtk_render(gvtk_scene* s);
/// Writes a PNG screenshot; returns 0 on success.
GV_API int32_t gvtk_screenshot(gvtk_scene* s, const char* path_utf8, int32_t magnification);

#ifdef __cplusplus
}
#endif
