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
        readonly SimSettings _sim = new SimSettings();   // bindable sim params (right toolbar)
        string _meshPath;            // source mesh path, so Reset can reload the input from disk
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
            DataContext = _sim;   // right-panel sliders + the run-button caption bind to this

            _gl = new GLWpfControl();
            CenterHost.Children.Add(_gl);   // GL viewport lives in the center cell of the docked layout
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
                try { _session = new FlowSession(MeshIO.Load(def)); _meshPath = def; Title = "CreaseStudio — " + System.IO.Path.GetFileName(def); }
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

            // Top-bar actions (declared in XAML): run N flow steps, one subdivision, reset.
            IterButton.Click += (s, e) => RunIters(_sim.IterPerRun);
            SubdivideButton.Click += (s, e) => Subdivide();
            ResetButton.Click += (s, e) => ResetMesh();

            // Collapse chevron at each panel's inner-top corner toggles collapse/expand.
            LeftCollapseBtn.Click += (s, e) => ToggleCollapse(LeftCol, ref _leftRestore);
            RightCollapseBtn.Click += (s, e) => ToggleCollapse(RightCol, ref _rightRestore);
            // Dragging a splitter below the threshold collapses that panel (remembering the width it
            // had before the drag, to restore to). Above the threshold, drag just resizes normally.
            LeftSplitter.PreviewMouseLeftButtonDown += (s, e) => _preDragWidth = LeftCol.ActualWidth;
            LeftSplitter.PreviewMouseLeftButtonUp += (s, e) => AfterSplitterDrag(LeftCol, ref _leftRestore);
            RightSplitter.PreviewMouseLeftButtonDown += (s, e) => _preDragWidth = RightCol.ActualWidth;
            RightSplitter.PreviewMouseLeftButtonUp += (s, e) => AfterSplitterDrag(RightCol, ref _rightRestore);
        }

        // A panel is "collapsed" when its column is narrower than CollapseThreshold (32px). The
        // chevron button toggles between a thin CollapsedWidth bar and the last expanded width;
        // dragging the splitter below the threshold also collapses (restoring to the pre-drag width).
        const double CollapseThreshold = 32;
        const double CollapsedWidth = 26;
        double _leftRestore = 220, _rightRestore = 240;
        double _preDragWidth;

        void ToggleCollapse(System.Windows.Controls.ColumnDefinition col, ref double restore)
        {
            if (col.ActualWidth < CollapseThreshold)
                col.Width = new GridLength(restore >= CollapseThreshold ? restore : 200);    // expand
            else { restore = col.ActualWidth; col.Width = new GridLength(CollapsedWidth); }   // collapse
        }

        void AfterSplitterDrag(System.Windows.Controls.ColumnDefinition col, ref double restore)
        {
            double w = col.ActualWidth;
            if (w < CollapseThreshold)
            {
                if (_preDragWidth >= CollapseThreshold) restore = _preDragWidth;   // came from this width
                col.Width = new GridLength(CollapsedWidth);
            }
            else { restore = w; col.Width = new GridLength(w); }   // pin the dragged width; remember it
        }

        // Run n flow steps in-process (cheap; on the UI thread), then flag the mesh for re-upload.
        void RunIters(int n)
        {
            if (_session == null) return;
            var p = _sim.ToFlowParams();   // snapshot the right-panel settings for this run
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

        // One 1->4 subdivision of the live mesh (geometry-preserving), then re-upload. Momentum
        // resets inside FlowSession.Subdivide (vertex indices are renumbered). Camera unchanged.
        void Subdivide()
        {
            if (_session == null) return;
            _session.Subdivide();
            _meshDirty = true;
            Title = "CreaseStudio — subdivided  (" + _session.Mesh.Vertices.Count + " verts)";
            _gl?.InvalidateVisual();
        }

        // Reload the input mesh from disk and reset the flow (fresh velocity, iteration count 0).
        // Same mesh bounds as the initial load, so the camera framing stays valid.
        void ResetMesh()
        {
            if (_meshPath == null || !System.IO.File.Exists(_meshPath)) return;
            try { _session = new FlowSession(MeshIO.Load(_meshPath)); }
            catch (Exception ex) { Title = "CreaseStudio — reset failed: " + ex.Message; return; }
            _totalIters = 0;
            _meshDirty = true;
            Title = "CreaseStudio — " + System.IO.Path.GetFileName(_meshPath);
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
