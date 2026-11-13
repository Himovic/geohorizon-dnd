// GeoVision Pro VTK rendering bridge: scientific volume rendering (GPU ray casting via vtkSmartVolumeMapper),
// surfaces and polylines in a VTK render window embedded in the WPF application through its native window handle.
#include "gv_vtk_bridge.h"

#include <vtkActor.h>
#include <vtkCamera.h>
#include <vtkCellArray.h>
#include <vtkColorTransferFunction.h>
#include <vtkFloatArray.h>
#include <vtkImageData.h>
#include <vtkPNGWriter.h>
#include <vtkPiecewiseFunction.h>
#include <vtkPointData.h>
#include <vtkPoints.h>
#include <vtkPolyData.h>
#include <vtkPolyDataMapper.h>
#include <vtkPolyDataNormals.h>
#include <vtkPolyLine.h>
#include <vtkProperty.h>
#include <vtkRenderWindow.h>
#include <vtkRenderer.h>
#include <vtkSmartPointer.h>
#include <vtkSmartVolumeMapper.h>
#include <vtkTriangle.h>
#include <vtkUnsignedCharArray.h>
#include <vtkVolume.h>
#include <vtkVolumeProperty.h>
#include <vtkWindowToImageFilter.h>

#include <cstring>
#include <map>

struct gvtk_scene {
    vtkSmartPointer<vtkRenderWindow> window;
    vtkSmartPointer<vtkRenderer> renderer;
    vtkSmartPointer<vtkVolume> volume;
    vtkSmartPointer<vtkSmartVolumeMapper> volumeMapper;
    vtkSmartPointer<vtkColorTransferFunction> color;
    vtkSmartPointer<vtkPiecewiseFunction> opacity;
    std::map<int32_t, vtkSmartPointer<vtkActor>> actors;
    int32_t nextId = 1;
};

extern "C" {

gvtk_scene* gvtk_create(void* parent_window, int32_t width, int32_t height, int32_t offscreen) {
    auto* s = new gvtk_scene();
    s->renderer = vtkSmartPointer<vtkRenderer>::New();
    s->renderer->SetBackground(0.09, 0.106, 0.141);  // #171B24
    s->window = vtkSmartPointer<vtkRenderWindow>::New();
    if (offscreen) s->window->SetOffScreenRendering(1);
    if (parent_window) s->window->SetParentId(parent_window);
    s->window->SetSize(width > 0 ? width : 640, height > 0 ? height : 480);
    s->window->AddRenderer(s->renderer);
    s->color = vtkSmartPointer<vtkColorTransferFunction>::New();
    s->opacity = vtkSmartPointer<vtkPiecewiseFunction>::New();
    return s;
}

void gvtk_destroy(gvtk_scene* s) {
    if (!s) return;
    s->window->Finalize();
    delete s;
}

void gvtk_resize(gvtk_scene* s, int32_t width, int32_t height) {
    if (s && width > 0 && height > 0) s->window->SetSize(width, height);
}

void gvtk_set_background(gvtk_scene* s, double r, double g, double b) {
    if (s) s->renderer->SetBackground(r, g, b);
}

int32_t gvtk_set_volume(gvtk_scene* s, const float* data, int32_t nx, int32_t ny, int32_t nz,
                        double sx, double sy, double sz, double ox, double oy, double oz) {
    if (!s || !data || nx <= 0 || ny <= 0 || nz <= 0) return -1;
    auto image = vtkSmartPointer<vtkImageData>::New();
    image->SetDimensions(nx, ny, nz);
    image->SetSpacing(sx, sy, sz);
    image->SetOrigin(ox, oy, oz);
    auto scalars = vtkSmartPointer<vtkFloatArray>::New();
    scalars->SetNumberOfComponents(1);
    scalars->SetNumberOfTuples(static_cast<vtkIdType>(nx) * ny * nz);
    std::memcpy(scalars->GetPointer(0), data, sizeof(float) * static_cast<size_t>(nx) * ny * nz);
    image->GetPointData()->SetScalars(scalars);

    if (!s->volumeMapper) s->volumeMapper = vtkSmartPointer<vtkSmartVolumeMapper>::New();
    s->volumeMapper->SetInputData(image);
    s->volumeMapper->SetRequestedRenderModeToDefault();  // GPU ray casting when available
    if (!s->volume) {
        s->volume = vtkSmartPointer<vtkVolume>::New();
        auto prop = vtkSmartPointer<vtkVolumeProperty>::New();
        prop->SetColor(s->color);
        prop->SetScalarOpacity(s->opacity);
        prop->SetInterpolationTypeToLinear();
        prop->ShadeOff();
        s->volume->SetProperty(prop);
        s->renderer->AddVolume(s->volume);
    }
    s->volume->SetMapper(s->volumeMapper);
    return 0;
}

void gvtk_set_transfer_function(gvtk_scene* s, const double* p, int32_t n) {
    if (!s || !p) return;
    s->color->RemoveAllPoints();
    s->opacity->RemoveAllPoints();
    for (int32_t i = 0; i < n; ++i) {
        const double* q = p + 5 * i;
        s->color->AddRGBPoint(q[0], q[1], q[2], q[3]);
        s->opacity->AddPoint(q[0], q[4]);
    }
}

void gvtk_set_volume_visible(gvtk_scene* s, int32_t visible) {
    if (s && s->volume) s->volume->SetVisibility(visible);
}

int32_t gvtk_add_mesh(gvtk_scene* s, const float* xyz, int32_t nverts, const int32_t* tris, int32_t ntris,
                      const uint8_t* rgba, double r, double g, double b, double opacity) {
    if (!s || !xyz || !tris) return -1;
    auto points = vtkSmartPointer<vtkPoints>::New();
    points->SetNumberOfPoints(nverts);
    for (int32_t i = 0; i < nverts; ++i) points->SetPoint(i, xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]);
    auto cells = vtkSmartPointer<vtkCellArray>::New();
    for (int32_t t = 0; t < ntris; ++t) {
        vtkIdType ids[3] = {tris[3 * t], tris[3 * t + 1], tris[3 * t + 2]};
        cells->InsertNextCell(3, ids);
    }
    auto poly = vtkSmartPointer<vtkPolyData>::New();
    poly->SetPoints(points);
    poly->SetPolys(cells);
    if (rgba) {
        auto colors = vtkSmartPointer<vtkUnsignedCharArray>::New();
        colors->SetNumberOfComponents(4);
        colors->SetNumberOfTuples(nverts);
        std::memcpy(colors->GetPointer(0), rgba, 4 * static_cast<size_t>(nverts));
        colors->SetName("Colors");
        poly->GetPointData()->SetScalars(colors);
    }
    auto normals = vtkSmartPointer<vtkPolyDataNormals>::New();
    normals->SetInputData(poly);
    normals->SplittingOff();
    auto mapper = vtkSmartPointer<vtkPolyDataMapper>::New();
    mapper->SetInputConnection(normals->GetOutputPort());
    if (rgba) { mapper->SetColorModeToDirectScalars(); mapper->ScalarVisibilityOn(); } else mapper->ScalarVisibilityOff();
    auto actor = vtkSmartPointer<vtkActor>::New();
    actor->SetMapper(mapper);
    actor->GetProperty()->SetColor(r, g, b);
    actor->GetProperty()->SetOpacity(opacity);
    s->renderer->AddActor(actor);
    int32_t id = s->nextId++;
    s->actors[id] = actor;
    return id;
}

int32_t gvtk_add_polyline(gvtk_scene* s, const float* xyz, int32_t npts, double r, double g, double b, double width) {
    if (!s || !xyz || npts < 2) return -1;
    auto points = vtkSmartPointer<vtkPoints>::New();
    auto line = vtkSmartPointer<vtkPolyLine>::New();
    line->GetPointIds()->SetNumberOfIds(npts);
    for (int32_t i = 0; i < npts; ++i) {
        points->InsertNextPoint(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]);
        line->GetPointIds()->SetId(i, i);
    }
    auto cells = vtkSmartPointer<vtkCellArray>::New();
    cells->InsertNextCell(line);
    auto poly = vtkSmartPointer<vtkPolyData>::New();
    poly->SetPoints(points);
    poly->SetLines(cells);
    auto mapper = vtkSmartPointer<vtkPolyDataMapper>::New();
    mapper->SetInputData(poly);
    auto actor = vtkSmartPointer<vtkActor>::New();
    actor->SetMapper(mapper);
    actor->GetProperty()->SetColor(r, g, b);
    actor->GetProperty()->SetLineWidth(static_cast<float>(width));
    s->renderer->AddActor(actor);
    int32_t id = s->nextId++;
    s->actors[id] = actor;
    return id;
}

void gvtk_remove_actor(gvtk_scene* s, int32_t id) {
    if (!s) return;
    auto it = s->actors.find(id);
    if (it == s->actors.end()) return;
    s->renderer->RemoveActor(it->second);
    s->actors.erase(it);
}

void gvtk_clear(gvtk_scene* s) {
    if (!s) return;
    for (auto& [id, a] : s->actors) s->renderer->RemoveActor(a);
    s->actors.clear();
    if (s->volume) { s->renderer->RemoveVolume(s->volume); s->volume = nullptr; }
}

void gvtk_set_parallel_projection(gvtk_scene* s, int32_t on) {
    if (s) s->renderer->GetActiveCamera()->SetParallelProjection(on);
}

void gvtk_reset_camera(gvtk_scene* s) {
    if (s) s->renderer->ResetCamera();
}

void gvtk_render(gvtk_scene* s) {
    if (s) s->window->Render();
}

int32_t gvtk_screenshot(gvtk_scene* s, const char* path_utf8, int32_t magnification) {
    if (!s || !path_utf8) return -1;
    s->window->Render();
    auto filter = vtkSmartPointer<vtkWindowToImageFilter>::New();
    filter->SetInput(s->window);
    filter->SetScale(magnification > 0 ? magnification : 1);
    filter->SetInputBufferTypeToRGBA();
    filter->ReadFrontBufferOff();
    filter->Update();
    auto writer = vtkSmartPointer<vtkPNGWriter>::New();
    writer->SetFileName(path_utf8);
    writer->SetInputConnection(filter->GetOutputPort());
    writer->Write();
    return 0;
}

}  // extern "C"
