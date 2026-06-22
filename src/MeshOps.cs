using System;
using System.Collections.Generic;
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
        /// One 1->4 midpoint subdivision: each triangle becomes 3 corner triangles + 1 central,
        /// with one shared midpoint vertex per edge. Geometry-preserving (midpoints are linear),
        /// winding-preserving, manifold-safe. Original vertex indices are preserved; midpoints are
        /// appended. Non-triangle / unused faces are skipped. Returns a fresh mesh (caller swaps it
        /// in and resets any per-vertex state, since the vertex list is renumbered/extended).
        ///
        /// This is the canonical copy. The CLI and the in-process studio both call it; the GH
        /// component still keeps its own (Rhino-side) copy in CreaseMachine.UniformSubdivide.
        /// </summary>
        public static PlanktonMesh UniformSubdivide(PlanktonMesh Pin)
        {
            var S = new PlanktonMesh();
            int nV = Pin.Vertices.Count, nE = Pin.Halfedges.Count / 2, nF = Pin.Faces.Count;

            // copy original vertices (indices preserved)
            for (int v = 0; v < nV; v++) { var pv = Pin.Vertices[v]; S.Vertices.Add(pv.X, pv.Y, pv.Z); }

            // one shared midpoint vertex per edge
            int[] mid = new int[nE];
            for (int e = 0; e < nE; e++)
            {
                if (Pin.Halfedges[2 * e].IsUnused) { mid[e] = -1; continue; }
                var pa = Pin.Vertices[Pin.Halfedges[2 * e].StartVertex];
                var pb = Pin.Vertices[Pin.Halfedges[2 * e + 1].StartVertex];
                mid[e] = S.Vertices.Add(0.5f * (pa.X + pb.X), 0.5f * (pa.Y + pb.Y), 0.5f * (pa.Z + pb.Z));
            }

            // each triangle -> 3 corner triangles + 1 central, preserving winding
            for (int f = 0; f < nF; f++)
            {
                if (Pin.Faces[f].IsUnused) continue;
                int[] hes = Pin.Faces.GetHalfedges(f);
                if (hes.Length != 3) continue; // only subdivide triangles
                int v0 = Pin.Halfedges[hes[0]].StartVertex, v1 = Pin.Halfedges[hes[1]].StartVertex, v2 = Pin.Halfedges[hes[2]].StartVertex;
                int m0 = mid[hes[0] / 2], m1 = mid[hes[1] / 2], m2 = mid[hes[2] / 2];
                if (m0 < 0 || m1 < 0 || m2 < 0) continue;
                S.Faces.AddFace(v0, m0, m2); S.Faces.AddFace(m0, v1, m1); S.Faces.AddFace(m2, m1, v2); S.Faces.AddFace(m0, m1, m2);
            }
            return S;
        }

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
        /// Collapse the SHORTEST edge of any face whose aspect ratio (2*area / maxLen^2) is below
        /// <paramref name="aspectThresh"/>. These "needle" triangles - shortest edge much shorter
        /// than the other two, but not necessarily short in ABSOLUTE terms - escape the absolute-
        /// length <c>CollapseShortEdges</c> (a 1:30 needle's short edge can sit comfortably above
        /// 0.2*mean and still pump a ~30x 1/dA spike that the face-level sliver guard misses,
        /// because its 1% aspect threshold only catches near-degenerate ones). Pre-step removal
        /// of these prevents the cap-saturated-motion cascade that turns a gradient spike at a
        /// needle's far vertex into a fold over a few frames. Reuses Plankton's manifold-safe
        /// <c>CollapseEdge</c>.
        ///
        /// Returns the number of collapses performed. If &gt; 0 the caller MUST call
        /// <c>P.Compact()</c> afterwards and rebuild any per-vertex state (e.g. momentum velocity).
        /// </summary>
        public static int CollapseSliverEdges(PlanktonMesh P, double aspectThresh)
        {
            int nF = P.Faces.Count;
            int collapses = 0;

            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int[] fhes = P.Faces.GetHalfedges(f);
                if (fhes.Length != 3) continue;

                int v0 = P.Halfedges[fhes[0]].StartVertex;
                int v1 = P.Halfedges[fhes[1]].StartVertex;
                int v2 = P.Halfedges[fhes[2]].StartVertex;

                double L01 = EdgeLength2(P, v0, v1);
                double L12 = EdgeLength2(P, v1, v2);
                double L20 = EdgeLength2(P, v2, v0);
                double maxL2 = Math.Max(L01, Math.Max(L12, L20));
                if (maxL2 < 1e-20) continue;

                double dA = TriDoubleArea(P, v0, v1, v2);
                if (dA / maxL2 >= aspectThresh) continue;   // not a needle, skip

                // collapse the shortest edge -> the needle's "short" side
                int hShort;
                if (L01 <= L12 && L01 <= L20) hShort = fhes[0];
                else if (L12 <= L20)          hShort = fhes[1];
                else                          hShort = fhes[2];

                int aKeep = P.Halfedges[hShort].StartVertex;
                int bKill = P.Halfedges[P.Halfedges.GetPairHalfedge(hShort)].StartVertex;
                if (P.Vertices[aKeep].IsUnused || P.Vertices[bKill].IsUnused) continue;
                if (P.Vertices.IsBoundary(aKeep) || P.Vertices.IsBoundary(bKill)) continue;

                double mx = 0.5 * (P.Vertices[aKeep].X + P.Vertices[bKill].X);
                double my = 0.5 * (P.Vertices[aKeep].Y + P.Vertices[bKill].Y);
                double mz = 0.5 * (P.Vertices[aKeep].Z + P.Vertices[bKill].Z);

                if (P.Halfedges.CollapseEdge(hShort) >= 0)   // keeps aKeep, kills bKill; -1 if non-manifold
                {
                    P.Vertices.SetVertex(aKeep, mx, my, mz);
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

        // 2 * triangle area = |(p1 - p0) x (p2 - p0)|. Same units the energy code uses for dA.
        private static double TriDoubleArea(PlanktonMesh P, int v0, int v1, int v2)
        {
            double e1x = P.Vertices[v1].X - P.Vertices[v0].X;
            double e1y = P.Vertices[v1].Y - P.Vertices[v0].Y;
            double e1z = P.Vertices[v1].Z - P.Vertices[v0].Z;
            double e2x = P.Vertices[v2].X - P.Vertices[v0].X;
            double e2y = P.Vertices[v2].Y - P.Vertices[v0].Y;
            double e2z = P.Vertices[v2].Z - P.Vertices[v0].Z;
            double cx = e1y * e2z - e1z * e2y;
            double cy = e1z * e2x - e1x * e2z;
            double cz = e1x * e2y - e1y * e2x;
            return Math.Sqrt(cx * cx + cy * cy + cz * cz);
        }

        /// <summary>
        /// Ordered boundary vertex loops. Each loop is the StartVertex sequence walked around a
        /// boundary (a halfedge whose AdjacentFace &lt; 0), following NextHalfedge until it closes.
        /// A closed solid returns none; an open disk patch returns one loop (its seam). Used to fit
        /// fixed B-spline seam curves and to pin boundary vertices during the isometric solve.
        /// </summary>
        public static System.Collections.Generic.List<int[]> BoundaryLoops(PlanktonMesh M)
        {
            int nH = M.Halfedges.Count;
            var visited = new bool[nH];
            var loops = new System.Collections.Generic.List<int[]>();
            for (int h0 = 0; h0 < nH; h0++)
            {
                if (M.Halfedges[h0].IsUnused || visited[h0] || M.Halfedges[h0].AdjacentFace >= 0) continue;
                var loop = new System.Collections.Generic.List<int>();
                int h = h0, guard = 0;
                while (h >= 0 && !visited[h] && guard++ <= nH)
                {
                    visited[h] = true;
                    loop.Add(M.Halfedges[h].StartVertex);
                    h = M.Halfedges[h].NextHalfedge;
                    if (h == h0) break;
                }
                if (loop.Count >= 2) loops.Add(loop.ToArray());
            }
            return loops;
        }

        /// <summary>Per-vertex mask, true where the vertex lies on any boundary loop.</summary>
        public static bool[] BoundaryVertexMask(PlanktonMesh M)
        {
            var mask = new bool[M.Vertices.Count];
            foreach (var loop in BoundaryLoops(M))
                foreach (int v in loop) if (v >= 0 && v < mask.Length) mask[v] = true;
            return mask;
        }

        private static int UfFind(int[] p, int x) { while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; } return x; }
        private static void UfUnion(int[] p, int a, int b) { a = UfFind(p, a); b = UfFind(p, b); if (a != b) p[a] = b; }

        /// <summary>Number of connected components (faces joined through shared interior edges). An FBX
        /// solid loaded with its unwelded seams returns one component per face (e.g. 6 for a 6-sided solid).</summary>
        public static int ComponentCount(PlanktonMesh M)
        {
            int nF = M.Faces.Count, nH = M.Halfedges.Count;
            var uf = new int[nF]; for (int i = 0; i < nF; i++) uf[i] = i;
            for (int h = 0; h < nH; h++)
            {
                if (M.Halfedges[h].IsUnused) continue;
                int pr = M.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = M.Halfedges[h].AdjacentFace, f2 = M.Halfedges[pr].AdjacentFace;
                if (f1 >= 0 && f2 >= 0) UfUnion(uf, f1, f2);
            }
            var roots = new HashSet<int>();
            for (int f = 0; f < nF; f++) if (!M.Faces[f].IsUnused) roots.Add(UfFind(uf, f));
            return roots.Count;
        }

        /// <summary>Split a mesh into one sub-mesh per connected component (faces joined through shared
        /// interior edges). vertexMaps[c][localVertex] = the source mesh's vertex index, so a solved piece
        /// can be written back in place. Each sub-mesh keeps its own boundary loop(s).</summary>
        public static List<PlanktonMesh> SplitComponents(PlanktonMesh M, out List<int[]> vertexMaps)
        {
            int nF = M.Faces.Count, nH = M.Halfedges.Count;
            var uf = new int[nF]; for (int i = 0; i < nF; i++) uf[i] = i;
            for (int h = 0; h < nH; h++)
            {
                if (M.Halfedges[h].IsUnused) continue;
                int pr = M.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = M.Halfedges[h].AdjacentFace, f2 = M.Halfedges[pr].AdjacentFace;
                if (f1 >= 0 && f2 >= 0) UfUnion(uf, f1, f2);
            }
            var groups = new Dictionary<int, List<int>>();
            for (int f = 0; f < nF; f++)
            {
                if (M.Faces[f].IsUnused) continue;
                int r = UfFind(uf, f);
                if (!groups.TryGetValue(r, out var g)) { g = new List<int>(); groups[r] = g; }
                g.Add(f);
            }
            var pieces = new List<PlanktonMesh>(); vertexMaps = new List<int[]>();
            foreach (var g in groups.Values)
            {
                var sub = new PlanktonMesh();
                var localOf = new Dictionary<int, int>(); var vmap = new List<int>();
                foreach (int f in g)
                {
                    int[] fv = M.Faces.GetFaceVertices(f); var lf = new int[fv.Length];
                    for (int k = 0; k < fv.Length; k++)
                    {
                        int gv = fv[k];
                        if (!localOf.TryGetValue(gv, out int lv)) { var p = M.Vertices[gv]; lv = sub.Vertices.Add(p.X, p.Y, p.Z); localOf[gv] = lv; vmap.Add(gv); }
                        lf[k] = lv;
                    }
                    sub.Faces.AddFace(lf);
                }
                pieces.Add(sub); vertexMaps.Add(vmap.ToArray());
            }
            return pieces;
        }
    }
}
