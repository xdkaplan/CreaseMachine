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
        FlowSession _session;        // live mesh + Nesterov velocity; the flow bakes it in place
        bool _glInit, _meshDirty;
        long _totalIters;
        byte[] _matcapPx; int _matcapW, _matcapH;   // matcap texture pixels (BGRA, GL row order)
        int _depthRbo, _depthW, _depthH;            // our depth attachment (GLWpfControl FBO is colour-only)

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
                try { _session = new FlowSession(MeshIO.Load(def)); Title = "CreaseStudio — " + System.IO.Path.GetFileName(def); }
                catch (Exception ex) { Title = "CreaseStudio — load failed: " + ex.Message; }
            }
            else Title = "CreaseStudio — (no mesh at " + def + ")";

            // Optional matcap texture (decoded on CPU now; GL upload at first render). Sampled by
            // the view-space normal - colours the surface by orientation (good for debugging normals).
            string mc = @"C:\Temp\matcap.png";
            if (System.IO.File.Exists(mc))
            {
                try
                {
                    var bi = new System.Windows.Media.Imaging.BitmapImage();
                    bi.BeginInit(); bi.UriSource = new Uri(mc);
                    bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.EndInit();
                    var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(
                        bi, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                    int w = conv.PixelWidth, h = conv.PixelHeight;
                    var px = new byte[w * h * 4];
                    conv.CopyPixels(px, w * 4, 0);
                    var flip = new byte[px.Length];                 // top-down rows -> bottom-up for GL
                    for (int y = 0; y < h; y++) Array.Copy(px, y * w * 4, flip, (h - 1 - y) * w * 4, w * 4);
                    _matcapPx = flip; _matcapW = w; _matcapH = h;
                }
                catch { }
            }

            // Minimal control overlay (top-left, over the viewport): a +10 iter button for now.
            var bar = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
            };
            var btn = new System.Windows.Controls.Button { Content = "+10 iter", Padding = new Thickness(12, 5, 12, 5), FontSize = 13 };
            btn.Click += (s, e) => RunIters(10);
            bar.Children.Add(btn);
            Root.Children.Add(bar);
        }

        // Run n flow steps in-process (cheap; on the UI thread), then flag the mesh for re-upload.
        void RunIters(int n)
        {
            if (_session == null) return;
            var p = new FlowParams { Step = 0.05, Momentum = 0.9, Sharpness = 4.0, CrazeBand = 0.1, MomFix = 4 };
            for (int i = 0; i < n; i++)
            {
                _session.CollapseShort();
                _session.CollapseSliver();
                _session.NesterovStep(p, out bool[] fold);
                _session.HealFolds(fold);
            }
            _totalIters += n;
            _meshDirty = true;
            Title = "CreaseStudio — iter " + _totalIters + "  (" + _session.Mesh.Vertices.Count + " verts)";
            _gl?.InvalidateVisual();
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
                _view = new MeshView();
                if (_matcapPx != null) _view.SetMatcap(_matcapPx, _matcapW, _matcapH);
                if (_session != null)
                {
                    _view.Upload(_session.Mesh);
                    _target = _view.Center;
                    _distance = _view.Radius * 3f;
                }
                _glInit = true;
            }

            // re-upload the (flow-mutated) mesh when a run has happened (GL thread = here)
            if (_meshDirty && _session != null) { _view.Upload(_session.Mesh); _meshDirty = false; }

            // Depth state EVERY frame: GLWpfControl rebinds its own framebuffer / resets GL state
            // each render, so a one-time enable doesn't stick. Without this there's no depth test and
            // the far side of the mesh paints over the near side (looks like "backfaces visible").
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.DepthMask(true);
            GL.Disable(EnableCap.CullFace);   // winding is mixed post-weld; rely on depth, not culling

            // GLWpfControl's framebuffer is colour-only (no depth), so attach our own depth
            // renderbuffer to the currently-bound FBO, sized to the viewport. Re-attach each frame
            // (the control may rebuild its FBO on resize); recreate the rbo only when the size changes.
            int[] vp = new int[4];
            GL.GetInteger(GetPName.Viewport, vp);
            int fbw = vp[2], fbh = vp[3];
            if (fbw > 0 && fbh > 0)
            {
                if (_depthRbo == 0 || fbw != _depthW || fbh != _depthH)
                {
                    if (_depthRbo == 0) _depthRbo = GL.GenRenderbuffer();
                    GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
                    GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, fbw, fbh);
                    _depthW = fbw; _depthH = fbh;
                }
                GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                    RenderbufferTarget.Renderbuffer, _depthRbo);
            }

            GL.ClearColor(0.12f, 0.12f, 0.14f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            double w = _gl.ActualWidth, h = Math.Max(1, _gl.ActualHeight);
            float aspect = (float)(w / h);

            // Z-up (Rhino convention): elevation raises +Z, up vector is +Z.
            Vector3 dir = new Vector3(
                MathF.Cos(_elevation) * MathF.Sin(_azimuth),
                MathF.Cos(_elevation) * MathF.Cos(_azimuth),
                MathF.Sin(_elevation));
            Vector3 eye = _target + dir * _distance;
            Matrix4 view = Matrix4.LookAt(eye, _target, Vector3.UnitZ);
            float r = _view != null ? _view.Radius : 1f;
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(45f), aspect, MathF.Max(1e-3f, r * 0.01f), r * 100f);

            _view?.Draw(view, proj);
        }
    }
}
