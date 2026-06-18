using System;
using System.Windows;
using System.Windows.Input;
using OpenTK.Wpf;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Plankton;
using CreaseMachine;

namespace CreaseStudio
{
    public partial class MainWindow : Window
    {
        GLWpfControl _gl;
        MeshView _view;
        PlanktonMesh _pendingMesh;   // loaded on CPU; uploaded on the GL thread at first render
        bool _glInit;

        // orbit camera
        float _azimuth = 0.6f, _elevation = 0.4f, _distance = 3f;
        Vector3 _target = Vector3.Zero;
        System.Windows.Point _lastMouse;
        bool _dragging;

        public MainWindow()
        {
            InitializeComponent();

            _gl = new GLWpfControl();
            Root.Children.Add(_gl);
            _gl.Start(new GLWpfControlSettings
            {
                MajorVersion = 3,
                MinorVersion = 3,
                Profile = OpenTK.Windowing.Common.ContextProfile.Core,
            });
            _gl.Render += OnRender;
            _gl.MouseDown += (s, e) => { _dragging = e.LeftButton == MouseButtonState.Pressed; _lastMouse = e.GetPosition(_gl); };
            _gl.MouseUp += (s, e) => _dragging = false;
            _gl.MouseMove += OnMouseMove;
            _gl.MouseWheel += (s, e) => { _distance *= MathF.Pow(0.999f, e.Delta); InvalidateView(); };

            // Load a default mesh on the CPU now; upload happens once the GL context is live.
            string def = @"C:\Temp\Bunny 5k.stl";
            if (System.IO.File.Exists(def))
            {
                try { _pendingMesh = MeshIO.Load(def); Title = "CreaseStudio — " + System.IO.Path.GetFileName(def) + "  (drag = orbit, wheel = zoom)"; }
                catch (Exception ex) { Title = "CreaseStudio — load failed: " + ex.Message; }
            }
            else Title = "CreaseStudio — (no mesh at " + def + ")";
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(_gl);
            float dx = (float)(p.X - _lastMouse.X), dy = (float)(p.Y - _lastMouse.Y);
            _lastMouse = p;
            _azimuth -= dx * 0.01f;
            _elevation = Math.Clamp(_elevation + dy * 0.01f, -1.5f, 1.5f);
            InvalidateView();
        }

        void InvalidateView() { _gl?.InvalidateVisual(); }

        void OnRender(TimeSpan delta)
        {
            if (!_glInit)
            {
                GL.Enable(EnableCap.DepthTest);
                GL.Disable(EnableCap.CullFace);   // welded STL winding can be mixed; show both sides
                _view = new MeshView();
                if (_pendingMesh != null)
                {
                    _view.Upload(_pendingMesh);
                    _target = _view.Center;
                    _distance = _view.Radius * 3f;
                }
                _glInit = true;
            }

            GL.ClearColor(0.12f, 0.12f, 0.14f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            double w = _gl.ActualWidth, h = Math.Max(1, _gl.ActualHeight);
            float aspect = (float)(w / h);

            Vector3 dir = new Vector3(
                MathF.Cos(_elevation) * MathF.Sin(_azimuth),
                MathF.Sin(_elevation),
                MathF.Cos(_elevation) * MathF.Cos(_azimuth));
            Vector3 eye = _target + dir * _distance;
            Matrix4 view = Matrix4.LookAt(eye, _target, Vector3.UnitY);
            float r = _view != null ? _view.Radius : 1f;
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(45f), aspect, MathF.Max(1e-3f, r * 0.01f), r * 100f);

            _view?.Draw(view, proj);
        }
    }
}
