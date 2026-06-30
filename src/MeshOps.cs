using System;
using System.Collections.Generic;
using Plankton;

namespace CreaseMachine
{
    /// <summary>
    /// A tiny path-halving union-find over a dense int domain (face ids). The single reusable copy
    /// behind the partition passes that were each re-declaring <c>int[] uf; for(i) uf[i]=i; int
    /// Find(int)</c> + the same inline <c>p[Find(a)] = Find(b)</c> union: MeshOps.ComponentCount /
    /// SplitComponents, and Pattern's Seed / Delete / Carve / SplitDisconnected / LargestComponent /
    /// MergeGroups. <c>Union(a, b)</c> reproduces the inline form exactly (find both roots, then
    /// <c>p[rootA] = rootB</c>), so the resulting partition is identical.
    /// </summary>
    public struct UnionFind
    {
        private readonly int[] _p;
        public UnionFind(int n) { _p = new int[n]; for (int i = 0; i < n; i++) _p[i] = i; }
        public int Find(int x) { while (_p[x] != x) { _p[x] = _p[_p[x]]; x = _p[x]; } return x; }
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _p[a] = b; }
    }

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

        /// <summary>
        /// First non-degenerate edge length — the scale that makes the flow's <c>Step·L²</c>
        /// invariant to mesh scale and to subdivision. Walks even halfedges, skips unused, returns
        /// the first positive length (else 1.0). The single canonical copy: the GH component, the
        /// FlowSession step, and the energy's finite-difference probe all route here.
        /// </summary>
        public static double RepresentativeEdge(PlanktonMesh P)
        {
            for (int i = 0; i < P.Halfedges.Count; i += 2)
            {
                if (P.Halfedges[i].IsUnused) continue;
                PlanktonVertex a = P.Vertices[P.Halfedges[i].StartVertex];
                PlanktonVertex b = P.Vertices[P.Halfedges[i + 1].StartVertex];
                double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
                double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (len > 0) return len;
            }
            return 1.0;
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

        /// <summary>
        /// Invoke <paramref name="onEdge"/> once per interior edge (an edge with a real face on BOTH
        /// sides) with its two face ids and its two endpoint vertex ids: <c>(f1, f2, a, b)</c>. Walks
        /// each half-edge, visits each undirected edge once via the <c>pr &lt; h</c> tie-break, and
        /// skips boundary edges (either adjacent face &lt; 0) — the exact <c>for h … GetPairHalfedge …
        /// if(pr&lt;0||pr&lt;h) continue … AdjacentFace … StartVertex</c> walk the partition + crease
        /// passes all re-implemented. f1/a come from the smaller half-edge h; f2/b from its pair pr.
        /// </summary>
        public static void ForEachInteriorEdge(PlanktonMesh M, Action<int, int, int, int> onEdge)
        {
            int nH = M.Halfedges.Count;
            for (int h = 0; h < nH; h++)
            {
                if (M.Halfedges[h].IsUnused) continue;
                int pr = M.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = M.Halfedges[h].AdjacentFace, f2 = M.Halfedges[pr].AdjacentFace;
                if (f1 < 0 || f2 < 0) continue;
                int a = M.Halfedges[h].StartVertex, b = M.Halfedges[pr].StartVertex;
                onEdge(f1, f2, a, b);
            }
        }

        /// <summary>Number of connected components (faces joined through shared interior edges). An FBX
        /// solid loaded with its unwelded seams returns one component per face (e.g. 6 for a 6-sided solid).</summary>
        public static int ComponentCount(PlanktonMesh M)
        {
            int nF = M.Faces.Count;
            var uf = new UnionFind(nF);
            ForEachInteriorEdge(M, (f1, f2, a, b) => uf.Union(f1, f2));
            var roots = new HashSet<int>();
            for (int f = 0; f < nF; f++) if (!M.Faces[f].IsUnused) roots.Add(uf.Find(f));
            return roots.Count;
        }

        /// <summary>
        /// Per-interior-edge fold angle in radians (0 = flat, pi = folded back), measured
        /// winding-independently from the two adjacent triangles' opposite vertices. Boundary edges and
        /// edges whose adjacent triangle is degenerate (near-zero area / sliver) are skipped. Returns one
        /// entry per interior edge, with the endpoint vertex indices in edgeA/edgeB (parallel). The crease
        /// proposer flows toward developable then labels edges whose fold exceeds a threshold as candidate
        /// piece boundaries.
        /// </summary>
        public static double[] EdgeDihedrals(PlanktonMesh M, out int[] edgeA, out int[] edgeB)
        {
            int nH = M.Halfedges.Count;
            var fold = new List<double>(nH / 2);
            var ea = new List<int>(nH / 2);
            var eb = new List<int>(nH / 2);
            for (int h = 0; h < nH; h++)
            {
                if (M.Halfedges[h].IsUnused) continue;
                int pr = M.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = M.Halfedges[h].AdjacentFace, f2 = M.Halfedges[pr].AdjacentFace;
                if (f1 < 0 || f2 < 0) continue;                          // boundary edge -> not an interior crease
                int a = M.Halfedges[h].StartVertex, b = M.Halfedges[pr].StartVertex;
                int c = OppositeVertex(M, f1, a, b), d = OppositeVertex(M, f2, a, b);
                if (c < 0 || d < 0) continue;
                Vec3 pa = Pos(M, a), axis = Pos(M, b) - pa; double al = axis.Length;
                if (al < 1e-20) continue; axis = axis * (1.0 / al);
                Vec3 ca = Pos(M, c) - pa, da = Pos(M, d) - pa;
                Vec3 u = ca - axis * (ca * axis), w = da - axis * (da * axis);   // components perpendicular to the edge
                double ul = u.Length, wl = w.Length;
                if (ul < 1e-12 || wl < 1e-12) continue;                  // sliver-adjacent -> no reliable fold
                double interior = Vec3.SafeAcos((u * w) / (ul * wl));
                fold.Add(Math.PI - interior); ea.Add(a); eb.Add(b);
            }
            edgeA = ea.ToArray(); edgeB = eb.ToArray();
            return fold.ToArray();
        }

        private static int OppositeVertex(PlanktonMesh M, int face, int a, int b)
        {
            foreach (int v in M.Faces.GetFaceVertices(face)) if (v != a && v != b) return v;
            return -1;
        }
        private static Vec3 Pos(PlanktonMesh M, int v) { var p = M.Vertices[v]; return new Vec3(p.X, p.Y, p.Z); }

        /// <summary>Split a mesh into one sub-mesh per connected component (faces joined through shared
        /// interior edges). vertexMaps[c][localVertex] = the source mesh's vertex index, so a solved piece
        /// can be written back in place. Each sub-mesh keeps its own boundary loop(s).</summary>
        public static List<PlanktonMesh> SplitComponents(PlanktonMesh M, out List<int[]> vertexMaps)
            => SplitComponents(M, out vertexMaps, out _);

        /// <summary>As SplitComponents, plus faceMaps[c][localFace] = the source mesh's face index — lets a
        /// caller map a component back to per-face attributes (e.g. a piece id from a pieceMap).</summary>
        public static List<PlanktonMesh> SplitComponents(PlanktonMesh M, out List<int[]> vertexMaps, out List<int[]> faceMaps)
        {
            int nF = M.Faces.Count;
            var uf = new UnionFind(nF);
            ForEachInteriorEdge(M, (f1, f2, a, b) => uf.Union(f1, f2));
            var groups = new Dictionary<int, List<int>>();
            for (int f = 0; f < nF; f++)
            {
                if (M.Faces[f].IsUnused) continue;
                int r = uf.Find(f);
                if (!groups.TryGetValue(r, out var g)) { g = new List<int>(); groups[r] = g; }
                g.Add(f);
            }
            var pieces = new List<PlanktonMesh>(); vertexMaps = new List<int[]>(); faceMaps = new List<int[]>();
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
                pieces.Add(sub); vertexMaps.Add(vmap.ToArray()); faceMaps.Add(g.ToArray());
            }
            return pieces;
        }

        /// <summary>
        /// Unweld a mesh along its piece boundaries: rebuild it so each region in <paramref name="pieceMap"/>
        /// (per-face id) becomes its own connected component. Vertices are duplicated by (original vertex,
        /// piece): faces in the SAME piece sharing a vertex keep one shared copy (welded inside the piece);
        /// faces in DIFFERENT pieces sharing a vertex get coincident separate copies (unwelded along the
        /// crease). Geometry is identical (coincident seam verts); faces stay 1:1. <paramref name="vertexMap"/>
        /// maps each new vertex back to its source vertex (like SplitComponents). The Solve handoff: develop the
        /// painted pieces by feeding this to the per-component bake. See docs/archive/SOLVER-PHASE.md.
        /// </summary>
        public static PlanktonMesh UnweldByRegion(PlanktonMesh M, int[] pieceMap, out int[] vertexMap)
        {
            var outMesh = new PlanktonMesh();
            var localOf = new Dictionary<long, int>();   // (vertex, piece) packed -> new vertex index
            var vmap = new List<int>();
            int nF = M.Faces.Count;
            for (int f = 0; f < nF; f++)
            {
                if (M.Faces[f].IsUnused) continue;
                int piece = (pieceMap != null && f < pieceMap.Length) ? pieceMap[f] : 0;
                int[] fv = M.Faces.GetFaceVertices(f);
                var lf = new int[fv.Length];
                for (int k = 0; k < fv.Length; k++)
                {
                    int gv = fv[k];
                    long key = ((long)gv << 32) | (uint)piece;
                    if (!localOf.TryGetValue(key, out int lv))
                    {
                        var p = M.Vertices[gv];
                        lv = outMesh.Vertices.Add(p.X, p.Y, p.Z);
                        localOf[key] = lv; vmap.Add(gv);
                    }
                    lf[k] = lv;
                }
                outMesh.Faces.AddFace(lf);
            }
            vertexMap = vmap.ToArray();
            return outMesh;
        }

        /// <summary>
        /// Projected tangential relaxation - the vertex-slippage stabilizer. Pulls each interior
        /// vertex a fraction <paramref name="weight"/> of the way toward its 1-ring centroid
        /// (a uniform Laplacian), but FIRST removes the component of that pull lying along the
        /// developability gradient <paramref name="grad"/> at the vertex. The surviving
        /// displacement is confined to the first-order developable level set, so it shares no
        /// direction with the developability force - it can neither oppose nor assist it, and
        /// cannot move the converged target. It only redistributes vertices within the
        /// developable family, removing the zero-energy "slippage" of low-angle, multi-panel
        /// vertices (and the resulting symmetry-breaking drift).
        ///
        /// Projection per vertex:  r_perp = r - g * (g . r) / (|g|^2 + eps^2),  where r is the
        /// centroid pull and g = grad[v]. With
        ///   eps = <paramref name="epsFrac"/> * median(|g|)   over interior vertices,
        /// the eps floor fades the projection to a no-op exactly where |g| -> 0 (the slip zone,
        /// where g's direction is also numerically arbitrary): the relaxer then acts at full
        /// strength there and steps aside where the developability force is strong (seams).
        /// Tying eps to the per-vertex gradient median - the same statistic the kink filter keys
        /// on - keeps it scale-, resolution-, and subdivision-invariant with no per-mesh constant.
        ///
        /// Position-only: it mutates positions and does NOT touch momentum velocity. Call it AFTER
        /// the gradient step, reusing the <paramref name="grad"/> already computed for that step
        /// (no extra energy evaluation). Boundary and unused vertices are held. The Laplacian reads
        /// a position snapshot so it is order-independent (Jacobi, not Gauss-Seidel).
        /// </summary>
        public static void ProjectedTangentialRelax(PlanktonMesh P, Vec3[] grad, double weight, double epsFrac)
        {
            if (weight <= 0.0 || grad == null) return;
            int nV = P.Vertices.Count;
            if (grad.Length < nV) return;

            // eps^2 from the median gradient magnitude over interior vertices. If there is no
            // gradient anywhere, eps2 stays 0 and the projection becomes the identity - the raw
            // Laplacian relaxes freely, which is correct (no developability force to protect).
            double eps2 = 0.0;
            int m = 0;
            for (int v = 0; v < nV; v++)
                if (!P.Vertices[v].IsUnused && !P.Vertices.IsBoundary(v) && grad[v].Length > 0) m++;
            if (m > 0)
            {
                double[] mg = new double[m];
                int k = 0;
                for (int v = 0; v < nV; v++)
                    if (!P.Vertices[v].IsUnused && !P.Vertices.IsBoundary(v) && grad[v].Length > 0) mg[k++] = grad[v].Length;
                Array.Sort(mg);
                double eps = epsFrac * mg[m / 2];
                eps2 = eps * eps;
            }

            // Snapshot positions so the Laplacian is order-independent (Jacobi-style).
            double[] px = new double[nV], py = new double[nV], pz = new double[nV];
            for (int v = 0; v < nV; v++)
            {
                px[v] = P.Vertices[v].X; py[v] = P.Vertices[v].Y; pz[v] = P.Vertices[v].Z;
            }

            for (int v = 0; v < nV; v++)
            {
                if (P.Vertices[v].IsUnused || P.Vertices.IsBoundary(v)) continue;
                int[] nb = P.Vertices.GetVertexNeighbours(v);
                if (nb == null || nb.Length == 0) continue;

                double cx = 0, cy = 0, cz = 0;
                for (int n = 0; n < nb.Length; n++) { cx += px[nb[n]]; cy += py[nb[n]]; cz += pz[nb[n]]; }
                double inv = 1.0 / nb.Length;
                Vec3 r = new Vec3(cx * inv - px[v], cy * inv - py[v], cz * inv - pz[v]);

                Vec3 g = grad[v];
                double denom = (g * g) + eps2;        // |g|^2 + eps^2  (Vec3*Vec3 is dot)
                if (denom > 1e-300)
                    r = r - g * ((g * r) / denom);     // strip the developability-changing component

                if (!r.IsValid) continue;
                P.Vertices.SetVertex(v, px[v] + weight * r.X, py[v] + weight * r.Y, pz[v] + weight * r.Z);
            }
        }
    }
}
