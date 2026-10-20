using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace DynamicIsland;

public sealed class Gadget : Decorator
{
    public enum Model { Earbuds, Headphones, Speaker }

    const double CameraDistance = 12, FieldOfView = 30;
    const double SpinFrom = -140, DropFrom = 3, LandSeconds = 0.9;
    const double SwayDegrees = 11, SwaySeconds = 1.25, BobHeight = 0.09, BobSeconds = 1.05;

    static readonly Material Plastic = Shiny(Color.FromRgb(0xF5, 0xF5, 0xF7), Color.FromRgb(0xFF, 0xFF, 0xFF), 70);
    static readonly Material Grille = Shiny(Color.FromRgb(0x2A, 0x2A, 0x2E), Color.FromRgb(0x50, 0x50, 0x55), 25);
    static readonly Material Metal = Shiny(Color.FromRgb(0xB4, 0xB7, 0xBE), Color.FromRgb(0xFF, 0xFF, 0xFF), 40);
    static readonly Material Cushion = Shiny(Color.FromRgb(0x36, 0x36, 0x3A), Color.FromRgb(0x30, 0x30, 0x34), 10);
    static readonly Material Fabric = Shiny(Color.FromRgb(0x2C, 0x2C, 0x30), Color.FromRgb(0x40, 0x40, 0x46), 12);
    static readonly Dictionary<Model, Model3DGroup> Built = [];
    static readonly Dictionary<Model, double> RestAngles = new()
    {
        [Model.Earbuds] = 0,
        [Model.Headphones] = -28,
        [Model.Speaker] = -20,
    };

    readonly AxisAngleRotation3D _spin = new(new Vector3D(0, 1, 0), 0);
    readonly AxisAngleRotation3D _rock = new(new Vector3D(0, 0, 1), 0);
    readonly TranslateTransform3D _lift = new();
    readonly ModelVisual3D _holder = new();

    public Gadget()
    {
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x58, 0x58, 0x5E)));
        lights.Children.Add(new DirectionalLight(Colors.White, new Vector3D(0.5, -0.8, -1)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x6A, 0x6A, 0x70), new Vector3D(-0.9, 0.2, -0.35)));

        var motion = new Transform3DGroup();
        motion.Children.Add(new RotateTransform3D(_rock));
        motion.Children.Add(new RotateTransform3D(_spin));
        motion.Children.Add(_lift);
        _holder.Transform = motion;

        var view = new Viewport3D
        {
            Camera = new PerspectiveCamera(new Point3D(0, 0, CameraDistance), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), FieldOfView),
            IsHitTestVisible = false,
        };
        view.Children.Add(new ModelVisual3D { Content = lights });
        view.Children.Add(_holder);
        Child = view;
        IsHitTestVisible = false;
    }

    public void Play(Model model, bool shaky, TimeSpan wait, TimeSpan stay)
    {
        if (!Built.TryGetValue(model, out Model3DGroup? shape)) Built[model] = shape = Build(model);
        _holder.Content = shape;

        double rest = RestAngles[model];
        var land = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        TimeSpan landed = wait + TimeSpan.FromSeconds(LandSeconds);
        _spin.BeginAnimation(AxisAngleRotation3D.AngleProperty,
            Swaying(rest + SpinFrom, rest, wait, landed, land, SwayDegrees, SwaySeconds, stay));
        _lift.BeginAnimation(TranslateTransform3D.OffsetYProperty,
            Swaying(DropFrom, 0, wait, landed - TimeSpan.FromSeconds(0.2), land, BobHeight, BobSeconds, stay));
        _rock.BeginAnimation(AxisAngleRotation3D.AngleProperty,
            shaky ? Motion.Sway(landed, TimeSpan.FromMilliseconds(700), -13, 11, -8, 5, -2, 0) : null);
    }

    static DoubleAnimationUsingKeyFrames Swaying(double from, double to, TimeSpan wait, TimeSpan landed, IEasingFunction land,
        double swing, double swingSeconds, TimeSpan stay)
    {
        DoubleAnimationUsingKeyFrames animation = Motion.Delayed(from, to, wait, landed - wait, land);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        TimeSpan at = landed;
        for (int side = 1; at < stay; side = -side)
        {
            at += TimeSpan.FromSeconds(swingSeconds);
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(to + swing * side, KeyTime.FromTimeSpan(at), ease));
        }
        animation.Duration = at;
        return animation;
    }

    static Model3DGroup Build(Model model)
    {
        var group = new Model3DGroup();
        switch (model)
        {
            case Model.Earbuds:
                group.Children.Add(Earbud(-1));
                group.Children.Add(Earbud(1));
                break;
            case Model.Headphones:
                AddHeadphones(group);
                break;
            case Model.Speaker:
                AddSpeaker(group);
                break;
        }
        group.Freeze();
        return group;
    }

    static Model3DGroup Earbud(double side)
    {
        var bud = new Model3DGroup();
        bud.Children.Add(Part(Meshes.Tube([new(0.32 * side, 1.0, 0), new(0.5 * side, -1.85, 0.02)], [0.36, 0.31]), Plastic));
        bud.Children.Add(Part(Meshes.Ellipsoid(new(0.5 * side, -1.85, 0.02), new(0.31, 0.16, 0.31)), Metal));
        bud.Children.Add(Part(Meshes.Ellipsoid(new(0, 1.35, 0), new(1, 0.95, 0.9)), Plastic));
        bud.Children.Add(Part(Meshes.Ellipsoid(new(-0.55 * side, 1.36, 0.55), new(0.3, 0.42, 0.3)), Grille));
        bud.Children.Add(Part(Meshes.Ellipsoid(new(0.45 * side, 1.78, 0.6), new(0.1, 0.08, 0.08)), Grille));

        var place = new Transform3DGroup();
        place.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), -25 * side)));
        place.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), 7 * side)));
        place.Children.Add(new TranslateTransform3D(1.4 * side, -0.05, 0));
        bud.Transform = place;
        return bud;
    }

    static void AddHeadphones(Model3DGroup group)
    {
        Point3D[] band = Meshes.Arc(new Point3D(0, 0, 0), 2.05, 12, 168, 40);
        group.Children.Add(Part(Meshes.Tube(band, band.Select(_ => 0.2).ToArray()), Metal));
        foreach (double side in new[] { -1.0, 1.0 })
        {
            Point3D end = band[side > 0 ? 0 : band.Length - 1];
            group.Children.Add(Part(Meshes.Tube([end, new(2.08 * side, -0.55, 0)], [0.08, 0.08]), Metal));
            group.Children.Add(Part(Meshes.Ellipsoid(new(2.12 * side, -1.4, 0), new(0.5, 0.95, 0.82)), Plastic));
            group.Children.Add(Part(Meshes.Ellipsoid(new(1.74 * side, -1.4, 0), new(0.3, 0.86, 0.74)), Cushion));
        }
        group.Transform = new TranslateTransform3D(0, -0.15, 0);
    }

    static void AddSpeaker(Model3DGroup group)
    {
        group.Children.Add(Part(Meshes.Tube([new(0, -1.5, 0), new(0, 1.5, 0)], [1, 1]), Fabric));
        group.Children.Add(Part(Meshes.Ellipsoid(new(0, 1.5, 0), new(1, 0.22, 1)), Metal));
        group.Children.Add(Part(Meshes.Ellipsoid(new(0, -1.5, 0), new(1, 0.22, 1)), Fabric));
    }

    static GeometryModel3D Part(MeshGeometry3D mesh, Material material) => new(mesh, material) { BackMaterial = material };

    static Material Shiny(Color body, Color shine, double sharpness)
    {
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(body)));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(shine), sharpness));
        material.Freeze();
        return material;
    }
}
