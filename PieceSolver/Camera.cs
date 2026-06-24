using System;
using OpenTK.Mathematics;

namespace PieceSolver
{
    // The orbit camera — Ephemeral view state owned by the View (see View.Camera). Z-up (Rhino convention).
    // Holds the orbit params and produces the view/projection matrices + pick rays; the stateless ray math
    // lives in Picker. MainWindow's input/render call into this rather than owning the camera fields.
    sealed class Camera
    {
        public float Azimuth = 0.6f, Elevation = 0.4f, Distance = 3f;
        public Vector3 Target = Vector3.Zero;

        // Unit vector from the orbit target toward the eye (eye = Target + Dir*Distance).
        public Vector3 Dir => Picker.CamDir(Azimuth, Elevation);
        public Vector3 Eye => Target + Dir * Distance;

        // Orbit (right-drag): yaw with dx, pitch with dy (clamped away from the poles).
        public void Orbit(float dx, float dy)
        {
            Azimuth += dx * 0.01f;
            Elevation = Math.Clamp(Elevation + dy * 0.01f, -1.5f, 1.5f);
        }

        // Pan (Shift+right-drag): translate the target in the camera's screen plane; speed scales with zoom.
        public void Pan(float dx, float dy)
        {
            Vector3 dir = Dir;
            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, dir));   // camera-right in world
            Vector3 up = Vector3.Normalize(Vector3.Cross(dir, right));              // camera-up in world
            float scale = Distance * 0.0015f;
            Target += (-dx * right + dy * up) * scale;
        }

        // Zoom (wheel): exponential in the notch count so the feel is even at any distance.
        public void Zoom(int delta) => Distance *= MathF.Pow(0.999f, delta);

        // Re-fit on a bounding sphere (center + radius) — the reframe after a load / reset / subdivide.
        public void Frame(Vector3 center, float radius) { Target = center; Distance = radius * 3f; }

        public Matrix4 ViewMatrix => Matrix4.LookAt(Eye, Target, Vector3.UnitZ);

        // Perspective projection; near/far derived from the scene radius so depth precision tracks scale.
        public Matrix4 ProjMatrix(float aspect, float sceneRadius)
            => Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(45f), aspect, MathF.Max(1e-3f, sceneRadius * 0.01f), sceneRadius * 100f);

        // Pick ray (eye + direction) from a screen point; delegates to the stateless Picker.
        public bool PickRay(System.Windows.Point screen, double w, double h, out Vector3 eye, out Vector3 rd)
            => Picker.PickRay(screen, w, h, Target, Azimuth, Elevation, Distance, out eye, out rd);
    }
}
