using System;
using System.Windows;
using OpenTK.Wpf;
using OpenTK.Graphics.OpenGL4;

namespace CreaseStudio
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // GL stack probe: spin up a GLWpfControl and clear it each frame. If the window opens
            // with a dark viewport (not a crash / white box), OpenTK + GLWpfControl + WPF on net8
            // is working and we can build the MatCap mesh viewport on top.
            var gl = new GLWpfControl();
            Root.Children.Add(gl);

            var settings = new GLWpfControlSettings
            {
                MajorVersion = 3,
                MinorVersion = 3,
                Profile = OpenTK.Windowing.Common.ContextProfile.Core,
            };
            gl.Start(settings);

            gl.Render += delta =>
            {
                GL.ClearColor(0.12f, 0.12f, 0.14f, 1.0f);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            };
        }
    }
}
