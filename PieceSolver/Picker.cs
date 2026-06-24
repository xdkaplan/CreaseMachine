using System;
using System.Windows;
using OpenTK.Mathematics;
using Plankton;

namespace PieceSolver
{
    // Stateless ray-pick geometry, lifted out of MainWindow: camera params + mesh in, hit out. No GL,
    // no app state. MainWindow's IEditorHost.PickFace / PickSurface and ScreenRadiusPx delegate here so
    // the picking math lives in exactly one place and stays testable in isolation.
    static class Picker
    {
        // Unit vector from the orbit target toward the eye (eye = target + dir*distance). Z-up.
        public static Vector3 CamDir(float azimuth, float elevation) => new Vector3(
            MathF.Cos(elevation) * MathF.Sin(azimuth),
            MathF.Cos(elevation) * MathF.Cos(azimuth),
            MathF.Sin(elevation));

        // Build a pick ray (eye + direction) from the camera params, convention-independent. Z-up, 45deg FOV.
        // `width`/`height` are the viewport's pixel dimensions; `screen` is in those pixels (top-left origin).
        public static bool PickRay(Point screen, double width, double height,
            Vector3 target, float azimuth, float elevation, float distance, out Vector3 eye, out Vector3 rd)
        {
            double w = Math.Max(1, width), h = Math.Max(1, height);
            Vector3 dir = CamDir(azimuth, elevation);
            eye = target + dir * distance;
            Vector3 forward = -dir;
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
            Vector3 up = Vector3.Cross(right, forward);
            float tanH = MathF.Tan(MathHelper.DegreesToRadians(45f) * 0.5f);   // matches the 45 deg proj FOV
            float aspect = (float)(w / h);
            float ndcX = (float)(2.0 * screen.X / w - 1.0);
            float ndcY = (float)(1.0 - 2.0 * screen.Y / h);
            rd = Vector3.Normalize(forward + right * (ndcX * tanH * aspect) + up * (ndcY * tanH));
            return true;
        }

        // Nearest ray-triangle hit over the mesh (linear scan; double-sided since winding is mixed).
        // Returns the hit point AND the hit face index together — one routine for both PickSurface
        // (point only) and PickFace (point + face). false (face = -1) when the ray misses every triangle.
        public static bool RayMeshHit(Vector3 ro, Vector3 rd, PlanktonMesh mesh, out Vector3 hit, out int face)
        {
            hit = default; face = -1;
            if (mesh == null) return false;
            double best = double.MaxValue;
            int nf = mesh.Faces.Count;
            for (int f = 0; f < nf; f++)
            {
                if (mesh.Faces[f].IsUnused) continue;
                int[] fv = mesh.Faces.GetFaceVertices(f);
                if (fv.Length != 3) continue;
                if (RayTri(ro, rd, BV(mesh, fv[0]), BV(mesh, fv[1]), BV(mesh, fv[2]), out double t) && t < best)
                { best = t; hit = ro + rd * (float)t; face = f; }
            }
            return face >= 0;
        }

        static bool RayTri(Vector3 ro, Vector3 rd, Vector3 a, Vector3 b, Vector3 c, out double t)
        {
            t = 0;
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 pv = Vector3.Cross(rd, e2);
            float det = Vector3.Dot(e1, pv);
            if (MathF.Abs(det) < 1e-12f) return false;
            float inv = 1f / det;
            Vector3 tv = ro - a;
            float u = Vector3.Dot(tv, pv) * inv;
            if (u < 0f || u > 1f) return false;
            Vector3 qv = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(rd, qv) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, qv) * inv;
            return t > 1e-6;
        }

        static Vector3 BV(PlanktonMesh P, int i) { var v = P.Vertices[i]; return new Vector3((float)v.X, (float)v.Y, (float)v.Z); }
    }
}
