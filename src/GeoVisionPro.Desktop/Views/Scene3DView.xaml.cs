using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;
using GeoVisionPro.Visualization3D;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.Wpf.SharpDX;
using SharpDX;
using MeshGeometry3D = HelixToolkit.SharpDX.Core.MeshGeometry3D;
using PerspectiveCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using OrthographicCamera = HelixToolkit.Wpf.SharpDX.OrthographicCamera;
using Material = HelixToolkit.Wpf.SharpDX.Material;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

namespace GeoVisionPro.Desktop.Views;

/// <summary>Builds HelixToolkit (DirectX 11) models from the renderer-independent scene model of the view-model.</summary>
public partial class Scene3DView : UserControl
{
    private Scene3DViewModel? _vm;
    private static readonly IEffectsManager Effects = new DefaultEffectsManager();

    public Scene3DView()
    {
        InitializeComponent();
        Viewport.EffectsManager = Effects;
        Viewport.Camera = new PerspectiveCamera { Position = new Point3D(0, 4000, 6000), LookDirection = new Vector3D(0, -4000, -6000), UpDirection = new Vector3D(0, 1, 0), FarPlaneDistance = 1e7, NearPlaneDistance = 1 };
        DataContextChanged += (_, _) => Attach(DataContext as Scene3DViewModel);
        Unloaded += (_, _) => { };
    }

    private void Attach(Scene3DViewModel? vm)
    {
        if (_vm is not null) { _vm.SceneChanged -= Rebuild; _vm.ZoomExtentsRequested -= Zoom; _vm.CameraModeChanged -= CameraMode; }
        _vm = vm;
        if (vm is null) return;
        vm.SceneChanged += Rebuild; vm.ZoomExtentsRequested += Zoom; vm.CameraModeChanged += CameraMode;
        if (vm.Geometry is { } g)
        {
            IlSlider.Minimum = Math.Min(g.Inlines.First, g.Inlines.Last); IlSlider.Maximum = Math.Max(g.Inlines.First, g.Inlines.Last); IlSlider.TickFrequency = g.Inlines.Step;
            XlSlider.Minimum = Math.Min(g.Crosslines.First, g.Crosslines.Last); XlSlider.Maximum = Math.Max(g.Crosslines.First, g.Crosslines.Last); XlSlider.TickFrequency = g.Crosslines.Step;
            TsSlider.Minimum = 0; TsSlider.Maximum = g.SampleCount - 1;
        }
        Rebuild(vm, EventArgs.Empty);
    }

    // world (X east, Y north, Z up) -> Helix (X, Y up, Z towards viewer)
    private static Vector3 V(float x, float y, float z) => new(x, z, -y);

    private void Rebuild(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        SceneRoot.Children.Clear();
        foreach (var item in _vm.Items.Where(i => i.Visible))
        {
            foreach (var m in item.Meshes) SceneRoot.Children.Add(ToModel(m, item.Transparent));
            foreach (var l in item.Lines) SceneRoot.Children.Add(ToLine(l));
        }
    }

    private static Element3D ToModel(SceneMesh m, bool transparent)
    {
        var pos = new Vector3Collection(m.VertexCount);
        for (int i = 0; i < m.VertexCount; i++) pos.Add(V(m.Positions[3 * i], m.Positions[3 * i + 1], m.Positions[3 * i + 2]));
        var geo = new MeshGeometry3D { Positions = pos, Indices = new IntCollection(m.Indices) };
        if (m.Normals is { } n)
        {
            var nn = new Vector3Collection(m.VertexCount);
            for (int i = 0; i < m.VertexCount; i++) nn.Add(V(n[3 * i], n[3 * i + 1], n[3 * i + 2]));
            geo.Normals = nn;
        }
        Material material;
        if (m.Texture is { } tex && m.TexCoords is { } uv)
        {
            var tc = new Vector2Collection(m.VertexCount);
            for (int i = 0; i < m.VertexCount; i++) tc.Add(new Vector2(uv[2 * i], uv[2 * i + 1]));
            geo.TextureCoordinates = tc;
            var stream = BitmapUtil.ToPngStream(tex, m.TextureWidth, m.TextureHeight);
            material = new DiffuseMaterial { DiffuseMap = TextureModel.Create(stream), EnableUnLit = true };
        }
        else if (m.VertexColors is { } vc)
        {
            var cols = new Color4Collection(m.VertexCount);
            foreach (var c in vc) cols.Add(new Color4(((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f));
            geo.Colors = cols;
            material = new VertColorMaterial();
        }
        else material = PhongMaterials.Gray;
        return new MeshGeometryModel3D { Geometry = geo, Material = material, CullMode = SharpDX.Direct3D11.CullMode.None, IsTransparent = transparent || m.Texture is not null && transparent };
    }

    private static Element3D ToLine(ScenePolyline l)
    {
        var pos = new Vector3Collection(l.Points.Count);
        foreach (var p in l.Points) pos.Add(V((float)p.X, (float)p.Y, (float)p.Z));
        var idx = new IntCollection();
        for (int i = 1; i < l.Points.Count; i++) { idx.Add(i - 1); idx.Add(i); }
        uint c = l.Color;
        return new LineGeometryModel3D
        {
            Geometry = new LineGeometry3D { Positions = pos, Indices = idx },
            Color = System.Windows.Media.Color.FromArgb(255, (byte)(c >> 16), (byte)(c >> 8), (byte)c),
            Thickness = l.Thickness
        };
    }

    private void Zoom(object? s, EventArgs e) => Viewport.ZoomExtents(500);

    private void CameraMode(object? s, EventArgs e)
    {
        if (_vm is null) return;
        var cam = Viewport.Camera;
        if (_vm.Orthographic)
            Viewport.Camera = new OrthographicCamera { Position = cam.Position, LookDirection = cam.LookDirection, UpDirection = cam.UpDirection, Width = 20000, FarPlaneDistance = 1e7, NearPlaneDistance = -1e6 };
        else
            Viewport.Camera = new PerspectiveCamera { Position = cam.Position, LookDirection = cam.LookDirection, UpDirection = cam.UpDirection, FarPlaneDistance = 1e7, NearPlaneDistance = 1 };
        Viewport.ZoomExtents();
    }

    private void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        var p = _vm?.Main.Dialogs.SaveFile("3D screenshot", "PNG image (*.png)|*.png", "scene3d.png");
        if (p is not null) BitmapUtil.SavePng(Viewport, p, 2);
    }
}
