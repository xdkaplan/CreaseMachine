using System;
using Plankton;

namespace CreaseMachine
{
    /// <summary>
    /// Minimal, Rhino-free mesh-cleanup helpers for the developability flow. Kept separate from
    /// the energy so they can be unit-tested against just Plankton.
    /// </summary>
    public static class MeshOps
    {
        /// <summary>
        /// Collapse every edge shorter than <paramref name="frac"/> x the mean edge length, to
        /// remove the slivers / near-degenerate triangles the flow can create (the source of the
        /// 1/area gradient spikes). Reuses Plankton's <c>CollapseEdge</c>, which self-guards
        /// against non-manifold collapses - this is NOT the adaptive remesher (no split/flip).
        /// The surviving vertex is moved to the edge midpoint. Boundary edges are left alone.
        ///
        /// Returns the number of collapses performed. If &gt; 0 the caller MUST call
        /// <c>P.Compact()</c> afterwards (this leaves unused elements behind) and rebuild any
        /// per-vertex state (e.g. momentum velocity), since Compact renumbers vertices.
        /// </summary>
        public static int CollapseShortEdges(PlanktonMesh P, double frac)
        {
            int nHE = P.Halfedges.Count;

            // mean edge length sets the scale, so the threshold tracks the mesh (incl. after Subdivide)
            double sum = 0.0; int cnt = 0;
            for (int h = 0; h < nHE; h += 2)
            {
                if (P.Halfedges[h].IsUnused) continue;
                sum += EdgeLength(P, h); cnt++;
            }
            if (cnt == 0) return 0;
            double thresh2 = (frac * sum / cnt); thresh2 *= thresh2;

            int collapses = 0;
            for (int h = 0; h < nHE; h += 2)
            {
                if (P.Halfedges[h].IsUnused) continue;
                int a = P.Halfedges[h].StartVertex;
                int b = P.Halfedges[h + 1].StartVertex;   // pair of an even halfedge is h+1
                if (P.Vertices[a].IsUnused || P.Vertices[b].IsUnused) continue;
                if (P.Vertices.IsBoundary(a) || P.Vertices.IsBoundary(b)) continue;   // leave boundaries alone
                if (EdgeLength2(P, a, b) >= thresh2) continue;

                double mx = 0.5 * (P.Vertices[a].X + P.Vertices[b].X);
                double my = 0.5 * (P.Vertices[a].Y + P.Vertices[b].Y);
                double mz = 0.5 * (P.Vertices[a].Z + P.Vertices[b].Z);

                int rtn = P.Halfedges.CollapseEdge(h);    // keeps a, kills b; -1 if it would be non-manifold
                if (rtn >= 0)
                {
                    P.Vertices.SetVertex(a, mx, my, mz);  // survivor to the midpoint
                    collapses++;
                }
            }
            return collapses;
        }

        /// <summary>
        /// Collapse the flagged fold vertices (coherence &lt; 0.05) - pinch points where the 1-ring
        /// folds back on itself, which the developability flow can't resolve. Each is merged into a
        /// neighbour via Plankton's manifold-safe <c>CollapseEdge</c> (skipped if it returns -1).
        /// <paramref name="isFold"/> comes free from <c>ComputeHingeEnergyAndGrad</c> - no face data
        /// is recomputed here. Returns the number collapsed; caller must <c>Compact()</c> + rebuild
        /// per-vertex state (e.g. momentum velocity) if &gt; 0.
        /// </summary>
        public static int CollapseFolds(PlanktonMesh P, bool[] isFold)
        {
            if (isFold == null) return 0;
            int collapsed = 0;
            int nV = P.Vertices.Count;
            for (int v = 0; v < nV; v++)
            {
                if (v >= isFold.Length || !isFold[v]) continue;
                if (P.Vertices[v].IsUnused || P.Vertices.IsBoundary(v)) continue;

                int[] hes = P.Vertices.GetHalfedges(v);   // outgoing from v; each pair points TO v
                foreach (int h in hes)
                {
                    if (P.Halfedges[h].IsUnused) continue;
                    // CollapseEdge(pair) keeps the neighbour (StartVertex of pair) and kills v,
                    // removing the pinch. Returns -1 if it would be non-manifold - then try another.
                    if (P.Halfedges.CollapseEdge(P.Halfedges.GetPairHalfedge(h)) >= 0) { collapsed++; break; }
                }
            }
            return collapsed;
        }

        private static double EdgeLength2(PlanktonMesh P, int a, int b)
        {
            double dx = P.Vertices[a].X - P.Vertices[b].X;
            double dy = P.Vertices[a].Y - P.Vertices[b].Y;
            double dz = P.Vertices[a].Z - P.Vertices[b].Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static double EdgeLength(PlanktonMesh P, int h)
        {
            return Math.Sqrt(EdgeLength2(P, P.Halfedges[h].StartVertex, P.Halfedges[h + 1].StartVertex));
        }
    }
}
