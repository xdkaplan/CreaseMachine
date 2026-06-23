using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using Plankton;

namespace PieceSolver
{
    // The thin companion over ONE PlanktonMesh: the partition (PieceMap) + the derived crease set
    // (CreaseMap) + the ops that mutate them. NOT a mesh — it stores no geometry, only the per-face
    // piece labels index-coupled to the held mesh. (Plankton has no per-face attribute storage, so the
    // labels have nowhere to live ON the mesh — they live here.) See docs/PIECER-REFACTOR.md.
    sealed class Pattern
    {
        readonly PlanktonMesh _mesh;

        // PRIMARY segmentation: per-face piece id (-1 = unused). Seeded by Propose (flood-fill), painted
        // by the Piecer. The hot-path array stays int[]; PieceId is the typed handle at the API boundary.
        public int[] PieceMap;
        // DERIVED crease set = edges between faces of different region; a materialized view of PieceMap.
        // Feeds the overlay + piece viz. Rebuilt (lossily) by RegenCrease.
        public HashSet<long> CreaseMap;

        public Pattern(PlanktonMesh mesh) { _mesh = mesh; }

        // ===================== ops (mutate the authoritative partition) =====================

        // Flood-fill face regions across non-crease interior edges (a crease blocks the merge), producing the
        // per-face PieceMap (compacted ids 0..N-1; -1 for unused faces). This is the primary segmentation the
        // Piecer paints. A whole-partition reset (a Chapter reset), not a delta.
        public void Seed()
        {
            PieceMap = null;
            var P = _mesh;
            if (P == null) return;
            int nF = P.Faces.Count, nH = P.Halfedges.Count;
            var uf = new int[nF]; for (int i = 0; i < nF; i++) uf[i] = i;
            int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
            for (int h = 0; h < nH; h++)
            {
                if (P.Halfedges[h].IsUnused) continue;
                int pr = P.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = P.Halfedges[h].AdjacentFace, f2 = P.Halfedges[pr].AdjacentFace;
                if (f1 < 0 || f2 < 0) continue;
                int a = P.Halfedges[h].StartVertex, b = P.Halfedges[pr].StartVertex;
                if (CreaseMap != null && CreaseMap.Contains(EdgeKey(a, b))) continue;
                int ra = Find(f1), rb = Find(f2); if (ra != rb) uf[ra] = rb;
            }
            var region = new int[nF];
            var rootId = new Dictionary<int, int>();
            int count = 0;
            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused) { region[f] = -1; continue; }
                int r = Find(f); if (!rootId.TryGetValue(r, out int id)) { id = count++; rootId[r] = id; }
                region[f] = id;
            }
            PieceMap = region;
        }

        // One paint dab: paint REGION MEMBERSHIP. Every face whose centroid falls within the brush radius of
        // `center` is reassigned to the active region. Brushing across a boundary therefore GROWS the active
        // region into its neighbour — the crease (a region boundary) follows the brush. Re-derives the crease
        // set; the caller re-pieces. No geometry moves. Returns true if it changed any face.
        public bool Paint(Vector3 center, double radius, PieceId active)
        {
            if (PieceMap == null || active.Value < 0 || _mesh == null) return false;
            bool changed = false;
            foreach (int f in FacesUnderBrush(center, radius))
            {
                if (PieceMap[f] == active.Value) continue;
                PieceMap[f] = active.Value; changed = true;
            }
            if (changed) RegenCrease();
            return changed;
        }

        // Remove the wholly-marked pieces and HEAL the gap by merging their faces into the dominant surviving
        // neighbour (most shared border). Connected removed pieces heal together as one blob; a blob with no
        // surviving neighbour is left as-is. This is the "kill & donate" Ctrl gesture, used when NO piece is
        // selected. Returns a human-readable summary of what was removed (for the Console), or null if nothing.
        public string Remove(HashSet<int> touched)
        {
            var P = _mesh;
            if (P == null || PieceMap == null || touched == null) return null;
            int nF = P.Faces.Count, nH = P.Halfedges.Count;
            var remove = FullyMarked(touched);
            if (remove.Count == 0) return null;
            bool IsRemoved(int f) => f >= 0 && f < nF && !P.Faces[f].IsUnused && remove.Contains(PieceMap[f]);

            // Union connected removed faces into blobs (across any edge where BOTH faces are being removed).
            var uf = new int[nF]; for (int i = 0; i < nF; i++) uf[i] = i;
            int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
            for (int h = 0; h < nH; h++)
            {
                if (P.Halfedges[h].IsUnused) continue;
                int pr = P.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = P.Halfedges[h].AdjacentFace, f2 = P.Halfedges[pr].AdjacentFace;
                if (f1 < 0 || f2 < 0) continue;
                if (IsRemoved(f1) && IsRemoved(f2)) { int a = Find(f1), b = Find(f2); if (a != b) uf[a] = b; }
            }

            // Tally each blob's shared boundary (edge count) with each SURVIVING region.
            var tally = new Dictionary<int, Dictionary<int, int>>();
            for (int h = 0; h < nH; h++)
            {
                if (P.Halfedges[h].IsUnused) continue;
                int pr = P.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = P.Halfedges[h].AdjacentFace, f2 = P.Halfedges[pr].AdjacentFace;
                if (f1 < 0 || f2 < 0) continue;
                bool r1 = IsRemoved(f1), r2 = IsRemoved(f2);
                if (r1 == r2) continue;                          // both removed (internal) or both surviving
                int rf = r1 ? f1 : f2, sf = r1 ? f2 : f1;        // removed face / surviving face
                int blob = Find(rf), sreg = PieceMap[sf];
                if (!tally.TryGetValue(blob, out var d)) { d = new Dictionary<int, int>(); tally[blob] = d; }
                d[sreg] = DictGet(d, sreg) + 1;
            }

            // Heal target per blob = dominant surviving neighbour (most shared border).
            var target = new Dictionary<int, int>();
            foreach (var kv in tally)
            {
                int dom = -1, domC = -1;
                foreach (var nb in kv.Value) if (nb.Value > domC) { domC = nb.Value; dom = nb.Key; }
                if (dom >= 0) target[kv.Key] = dom;
            }

            int healed = 0, stuck = 0;
            for (int f = 0; f < nF; f++)
            {
                if (!IsRemoved(f)) continue;
                if (target.TryGetValue(Find(f), out int tgt)) PieceMap[f] = tgt; else stuck++;
            }
            foreach (var r in remove) healed++;
            RegenCrease();
            return stuck > 0 ? $"removed {healed} piece(s); {stuck} face(s) had no surviving neighbour (left as-is)"
                             : $"removed {healed} piece(s)";
        }

        // Carve faces OUT of the active piece (the Ctrl gesture when a piece IS selected). Only faces of the
        // active piece are carved. Each connected blob of carved faces is re-homed: donated to its dominant
        // FOREIGN neighbour (the active piece is EXCLUDED so it can't reclaim the carve — that is what makes
        // carving against a small neighbour actually work), or — if the blob is an island with no foreign
        // neighbour — split off as a brand-new piece. The active piece is shrunk, never deleted: carving away
        // ALL of it is refused (deselect + Remove to delete a whole piece). Returns a summary, or null if nothing.
        public string Carve(HashSet<int> touched, PieceId active)
        {
            var P = _mesh;
            if (P == null || PieceMap == null || touched == null || active.Value < 0) return null;
            int nF = P.Faces.Count, nH = P.Halfedges.Count, act = active.Value;
            bool IsCarved(int f) => f >= 0 && f < nF && !P.Faces[f].IsUnused && touched.Contains(f) && PieceMap[f] == act;

            // Honour "the active piece is never removed": refuse to carve away ALL of it.
            int activeTotal = 0, carvedCount = 0;
            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused || PieceMap[f] != act) continue;
                activeTotal++; if (touched.Contains(f)) carvedCount++;
            }
            if (carvedCount == 0) return null;
            if (carvedCount >= activeTotal) return "can't carve the whole active piece — deselect (click empty space) to remove it";

            // Blobs of connected carved faces.
            var uf = new int[nF]; for (int i = 0; i < nF; i++) uf[i] = i;
            int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
            for (int h = 0; h < nH; h++)
            {
                if (P.Halfedges[h].IsUnused) continue;
                int pr = P.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = P.Halfedges[h].AdjacentFace, f2 = P.Halfedges[pr].AdjacentFace;
                if (f1 < 0 || f2 < 0) continue;
                if (IsCarved(f1) && IsCarved(f2)) { int a = Find(f1), b = Find(f2); if (a != b) uf[a] = b; }
            }
            var blobRoots = new HashSet<int>();
            for (int f = 0; f < nF; f++) if (IsCarved(f)) blobRoots.Add(Find(f));

            // Tally each blob's shared border with each FOREIGN region (anything that isn't the active piece —
            // unmarked active faces are NOT candidates, so the carve cannot heal back into the piece it left).
            var tally = new Dictionary<int, Dictionary<int, int>>();
            for (int h = 0; h < nH; h++)
            {
                if (P.Halfedges[h].IsUnused) continue;
                int pr = P.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = P.Halfedges[h].AdjacentFace, f2 = P.Halfedges[pr].AdjacentFace;
                if (f1 < 0 || f2 < 0) continue;
                bool c1 = IsCarved(f1), c2 = IsCarved(f2);
                if (c1 == c2) continue;
                int cf = c1 ? f1 : f2, sf = c1 ? f2 : f1, sreg = PieceMap[sf];
                if (sreg == act) continue;                       // the active piece itself -> excluded
                int blob = Find(cf);
                if (!tally.TryGetValue(blob, out var d)) { d = new Dictionary<int, int>(); tally[blob] = d; }
                d[sreg] = DictGet(d, sreg) + 1;
            }

            // Target per blob: dominant foreign neighbour, else a fresh piece id (island).
            int nextId = NewRegionId().Value, islands = 0;
            var target = new Dictionary<int, int>();
            foreach (int blob in blobRoots)
            {
                if (tally.TryGetValue(blob, out var d) && d.Count > 0)
                {
                    int dom = -1, domC = -1;
                    foreach (var nb in d) if (nb.Value > domC) { domC = nb.Value; dom = nb.Key; }
                    target[blob] = dom;
                }
                else { target[blob] = nextId++; islands++; }
            }

            int carved = 0;
            for (int f = 0; f < nF; f++)
            {
                if (!IsCarved(f)) continue;
                PieceMap[f] = target[Find(f)]; carved++;
            }
            RegenCrease();
            return islands > 0 ? $"carved {carved} face(s) ({islands} new island piece(s))"
                               : $"carved {carved} face(s)";
        }

        // A brush stroke can carve one region into several edge-disconnected islands that still share its id
        // (e.g. painting B straight through A leaves two A-halves). Re-split every such region: the LARGEST
        // island keeps the original id, each other island gets a fresh id. (Creases are unaffected — the
        // islands are separated by OTHER regions, so no A|A' edge exists.) Returns true if it renumbered.
        public bool SplitDisconnected()
        {
            if (PieceMap == null || _mesh == null) return false;
            var P = _mesh;
            int nF = P.Faces.Count, nH = P.Halfedges.Count;
            // within-region connectivity: union faces sharing an edge that have the SAME region id.
            var uf = new int[nF]; for (int i = 0; i < nF; i++) uf[i] = i;
            int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
            for (int h = 0; h < nH; h++)
            {
                if (P.Halfedges[h].IsUnused) continue;
                int pr = P.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = P.Halfedges[h].AdjacentFace, f2 = P.Halfedges[pr].AdjacentFace;
                if (f1 < 0 || f2 < 0) continue;
                if (PieceMap[f1] == PieceMap[f2]) { int a = Find(f1), b = Find(f2); if (a != b) uf[a] = b; }
            }
            var blobSize = new Dictionary<int, int>();
            var regionBlobs = new Dictionary<int, List<int>>();
            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int r = PieceMap[f]; if (r < 0) continue;
                int root = Find(f);
                blobSize[root] = DictGet(blobSize, root) + 1;
                if (!regionBlobs.TryGetValue(r, out var lst)) { lst = new List<int>(); regionBlobs[r] = lst; }
                if (!lst.Contains(root)) lst.Add(root);
            }
            int nextId = NewRegionId().Value;
            bool changed = false;
            foreach (var kv in regionBlobs)
            {
                var blobs = kv.Value;
                if (blobs.Count <= 1) continue;                  // region is still connected
                int keep = blobs[0], keepSize = DictGet(blobSize, blobs[0]);
                foreach (int b in blobs) { int s = DictGet(blobSize, b); if (s > keepSize) { keepSize = s; keep = b; } }
                var newIdFor = new Dictionary<int, int>();
                foreach (int b in blobs) if (b != keep) newIdFor[b] = nextId++;   // largest keeps the id; others get fresh ids
                for (int f = 0; f < nF; f++)
                {
                    if (P.Faces[f].IsUnused || PieceMap[f] != kv.Key) continue;
                    if (newIdFor.TryGetValue(Find(f), out int nid)) { PieceMap[f] = nid; changed = true; }
                }
            }
            return changed;
        }

        // ===================== regen (re-derive the cached crease view) =====================

        // Derive the crease set from the region map: an interior edge is a crease iff its two faces are in
        // different regions. Called after every paint (and after Seed) so the overlay + piece grooves always
        // trace the current region boundaries. Lossy by design (rebuilds the whole set; no per-crease identity).
        public void RegenCrease()
        {
            CreaseMap = new HashSet<long>();
            if (_mesh == null || PieceMap == null) return;
            var P = _mesh;
            int nH = P.Halfedges.Count;
            for (int h = 0; h < nH; h++)
            {
                if (P.Halfedges[h].IsUnused) continue;
                int pr = P.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = P.Halfedges[h].AdjacentFace, f2 = P.Halfedges[pr].AdjacentFace;
                if (f1 < 0 || f2 < 0 || PieceMap[f1] == PieceMap[f2]) continue;
                int a = P.Halfedges[h].StartVertex, b = P.Halfedges[pr].StartVertex;
                CreaseMap.Add(EdgeKey(a, b));
            }
        }

        // ===================== queries (read-only) =====================

        // A fresh, unused region id (one past the current max), so Shift+paint introduces a NEW region rather
        // than growing an existing one. Ids only need to be unique; gaps from fully-overwritten regions are fine.
        public PieceId NewRegionId()
        {
            int mx = -1;
            if (PieceMap != null) for (int i = 0; i < PieceMap.Length; i++) if (PieceMap[i] > mx) mx = PieceMap[i];
            return new PieceId(mx + 1);
        }

        // The set of regions whose faces are ALL marked (so they read dark red and will be removed). O(F).
        // Used only by the no-selection "kill & donate" remove path (carving never removes whole pieces).
        public HashSet<int> FullyMarked(HashSet<int> touched)
        {
            var result = new HashSet<int>();
            if (touched == null || touched.Count == 0 || PieceMap == null || _mesh == null) return result;
            var P = _mesh; int nF = P.Faces.Count;
            var total = new Dictionary<int, int>();
            var hit = new Dictionary<int, int>();
            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int r = PieceMap[f]; if (r < 0) continue;
                total[r] = DictGet(total, r) + 1;
                if (touched.Contains(f)) hit[r] = DictGet(hit, r) + 1;
            }
            foreach (var kv in total) if (DictGet(hit, kv.Key) == kv.Value) result.Add(kv.Key);
            return result;
        }

        // The faces whose centroid is within `radius` of `center`. The shared footprint loop behind Paint
        // (which reassigns them) and the remove gesture's Mark (which accumulates them) — invoked per dab.
        public IEnumerable<int> FacesUnderBrush(Vector3 center, double radius)
        {
            if (_mesh == null) yield break;
            var P = _mesh;
            float R2 = (float)(radius * radius);
            int nF = P.Faces.Count;
            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int[] fv = P.Faces.GetFaceVertices(f); if (fv.Length != 3) continue;
                Vector3 c = (BV(P, fv[0]) + BV(P, fv[1]) + BV(P, fv[2])) * (1f / 3f);
                if ((c - center).LengthSquared <= R2) yield return f;
            }
        }

        // ===================== helpers =====================

        public static long EdgeKey(int a, int b) { int lo = Math.Min(a, b), hi = Math.Max(a, b); return ((long)lo << 32) | (uint)hi; }
        static int DictGet(Dictionary<int, int> d, int k) => d.TryGetValue(k, out var v) ? v : 0;
        static Vector3 BV(PlanktonMesh P, int i) { var v = P.Vertices[i]; return new Vector3((float)v.X, (float)v.Y, (float)v.Z); }
    }
}
