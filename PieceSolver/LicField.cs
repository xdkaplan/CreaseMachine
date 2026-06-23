using Plankton;
using CreaseMachine;

namespace PieceSolver
{
    // Builds the per-vertex direction field the MatCap-LIC shader convolves the solid noise along.
    //   mode 1 = ruling director  — the developable "grain" (zero-curvature generators).
    //   mode 2 = developability gradient — the flow field the Nesterov step descends.
    // Returned as float[nV*3] in mesh MODEL space, indexed by ORIGINAL vertex (MeshView remaps to its
    // compacted vertex order). The vector LENGTH carries the field strength (ruling: anisotropy in
    // [0,1]; gradient: |grad_tangent|), so the shader aligns the grain by direction and tints by
    // magnitude. fieldMax is the magnitude normaliser for the tint.
    static class LicField
    {
        public static float[] Compute(FlowSession session, FlowParams p, int mode, out float fieldMax)
        {
            if (mode == 2) return Gradient(session, p, out fieldMax);
            return RulingField.ComputeField(session.Mesh, out fieldMax);
        }

        // Developability-energy gradient, projected onto each vertex's tangent plane (the on-surface
        // flow direction). LIC is direction-agnostic (the convolution is symmetric in ±d), so the
        // gradient's sign doesn't matter here — only its line direction and magnitude.
        static float[] Gradient(FlowSession session, FlowParams p, out float fieldMax)
        {
            var M = session.Mesh;
            int nV = M.Vertices.Count;
            var field = new float[nV * 3];
            fieldMax = 1f;

            Vec3[] grad = session.EnergyGradient(p);
            if (grad == null || grad.Length != nV) return field;

            var pos = new Vec3[nV]; var nrm = new Vec3[nV];
            for (int v = 0; v < nV; v++) { if (M.Vertices[v].IsUnused) continue; var q = M.Vertices[v]; pos[v] = new Vec3(q.X, q.Y, q.Z); }
            for (int f = 0; f < M.Faces.Count; f++)
            {
                if (M.Faces[f].IsUnused) continue;
                int[] fv = M.Faces.GetFaceVertices(f); if (fv.Length < 3) continue;
                Vec3 cr = Vec3.Cross(pos[fv[1]] - pos[fv[0]], pos[fv[2]] - pos[fv[0]]);
                nrm[fv[0]] += cr; nrm[fv[1]] += cr; nrm[fv[2]] += cr;
            }
            for (int v = 0; v < nV; v++) { double L = nrm[v].Length; nrm[v] = L > 1e-20 ? nrm[v] * (1.0 / L) : new Vec3(0, 0, 1); }

            double mx = 0.0;
            for (int v = 0; v < nV; v++)
            {
                if (M.Vertices[v].IsUnused) continue;
                Vec3 g = grad[v]; if (!g.IsValid) continue;
                Vec3 t = g - nrm[v] * (g * nrm[v]);   // tangential component = the on-surface flow direction
                double len = t.Length;
                field[v * 3] = (float)t.X; field[v * 3 + 1] = (float)t.Y; field[v * 3 + 2] = (float)t.Z;
                if (len > mx) mx = len;
            }
            fieldMax = (float)(mx > 1e-20 ? mx : 1.0);
            return field;
        }
    }
}
