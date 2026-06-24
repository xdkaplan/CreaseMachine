using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using Plankton;
using CreaseMachine;

namespace PieceSolver
{
    // The thin companion over ONE PlanktonMesh: the partition (PieceMap) + the derived crease set
    // (CreaseMap) + the ops that mutate them. NOT a mesh — it stores no geometry, only the per-face
    // piece labels index-coupled to the held mesh. (Plankton has no per-face attribute storage, so the
    // labels have nowhere to live ON the mesh — they live here.) See docs/archive/PIECER-REFACTOR.md.
    sealed class Pattern : ITxAble
    {
        readonly PlanktonMesh _mesh;

        // PRIMARY segmentation: per-face piece id (-1 = unused). Seeded by Propose (flood-fill), painted
        // by the Piecer. The hot-path array stays int[]; PieceId is the typed handle at the API boundary.
        public int[] PieceMap;
        // DERIVED crease set = edges between faces of different region; a Transient view of PieceMap. Feeds the
        // overlay + piece viz. PULL: lazily (re)derived from PieceMap (DeriveCreases) on read; marked stale by
        // RegenCrease after any PieceMap change. The Seed bootstrap PUSHes a provisional set via .Set (see
        // SeedCreaseEdges). Lossy — rebuilt wholesale, no per-crease identity. See AGENTS.md (Real/Transient).
        public readonly Transient<HashSet<long>> CreaseMap;

        public Pattern(PlanktonMesh mesh) { _mesh = mesh; CreaseMap = new Transient<HashSet<long>>(DeriveCreases); }

        // ===================== ops (mutate the authoritative partition) =====================

        // Flood-fill face regions across non-crease interior edges (a crease blocks the merge), producing the
        // per-face PieceMap (compacted ids 0..N-1; -1 for unused faces). This is the primary segmentation the
        // Piecer paints. A whole-partition reset (a Chapter reset), not a delta.
        public void Seed()
        {
            PieceMap = null;
            var P = _mesh;
            if (P == null) return;
            int nF = P.Faces.Count;
            var uf = new UnionFind(nF);
            CreaseMap.Peek(out var seedCreases);   // peek the cached crease set (provisional or last-derived) — no circular regen
            MeshOps.ForEachInteriorEdge(P, (f1, f2, a, b) =>
            {
                if (seedCreases != null && seedCreases.Contains(EdgeKey(a, b))) return;
                uf.Union(f1, f2);
            });
            var region = new int[nF];
            var rootId = new Dictionary<int, int>();
            int count = 0;
            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused) { region[f] = -1; continue; }
                int r = uf.Find(f); if (!rootId.TryGetValue(r, out int id)) { id = count++; rootId[r] = id; }
                region[f] = id;
            }
            PieceMap = region;
        }

        // Remove the wholly-marked pieces and HEAL the gap by merging their faces into the dominant surviving
        // neighbour (most shared border). Connected removed pieces heal together as one blob; a blob with no
        // surviving neighbour is left as-is. This is the "kill & donate" Ctrl gesture, used when NO piece is
        // selected. Returns a human-readable summary of what was removed (for the Console), or null if nothing.
        public string Delete(HashSet<int> touched)
        {
            var P = _mesh;
            if (P == null || PieceMap == null || touched == null) return null;
            int nF = P.Faces.Count;
            var remove = FullyMarked(touched);
            if (remove.Count == 0) return null;
            bool IsRemoved(int f) => f >= 0 && f < nF && !P.Faces[f].IsUnused && remove.Contains(PieceMap[f]);

            // Union connected removed faces into blobs (across any edge where BOTH faces are being removed).
            var uf = new UnionFind(nF);
            MeshOps.ForEachInteriorEdge(P, (f1, f2, a, b) =>
            {
                if (IsRemoved(f1) && IsRemoved(f2)) uf.Union(f1, f2);
            });

            // Tally each blob's shared boundary (edge count) with each SURVIVING region.
            var tally = new Dictionary<int, Dictionary<int, int>>();
            MeshOps.ForEachInteriorEdge(P, (f1, f2, a, b) =>
            {
                bool r1 = IsRemoved(f1), r2 = IsRemoved(f2);
                if (r1 == r2) return;                            // both removed (internal) or both surviving
                int rf = r1 ? f1 : f2, sf = r1 ? f2 : f1;        // removed face / surviving face
                int blob = uf.Find(rf), sreg = PieceMap[sf];
                if (!tally.TryGetValue(blob, out var d)) { d = new Dictionary<int, int>(); tally[blob] = d; }
                d[sreg] = DictGet(d, sreg) + 1;
            });

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
                if (target.TryGetValue(uf.Find(f), out int tgt)) PieceMap[f] = tgt; else stuck++;
            }
            foreach (var r in remove) healed++;
            RegenCrease();
            return stuck > 0 ? $"removed {healed} piece(s); {stuck} face(s) had no surviving neighbour (left as-is)"
                             : $"removed {healed} piece(s)";
        }

        // Carve faces OUT of the SELECTED pieces (the Ctrl gesture when one or more pieces are selected). Only
        // faces belonging to a selected piece are carved. Each connected blob of carved faces is re-homed:
        // donated to its dominant neighbour OUTSIDE the selection (selected pieces are EXCLUDED so a carve can't
        // heal back into the selection it left — that is what makes carving against a small neighbour work), or
        // — if the blob has no such neighbour — split off as a brand-new island piece. No selected piece is ever
        // fully consumed: a piece the brush would empty is PROTECTED (its faces are left as a no-op); if every
        // carved face is protected, the carve is refused. Returns a summary, or null if nothing.
        public string Carve(HashSet<int> touched, HashSet<int> selection)
        {
            var P = _mesh;
            if (P == null || PieceMap == null || touched == null || selection == null || selection.Count == 0) return null;
            int nF = P.Faces.Count;

            // Per selected piece: total faces vs how many the brush marked. A piece the brush would EMPTY is
            // PROTECTED — carving it all away would delete it (deselect + Remove for that), so it stays put.
            var total = new Dictionary<int, int>();
            var marked = new Dictionary<int, int>();
            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int r = PieceMap[f]; if (!selection.Contains(r)) continue;
                total[r] = DictGet(total, r) + 1;
                if (touched.Contains(f)) marked[r] = DictGet(marked, r) + 1;
            }
            var prot = new HashSet<int>();
            int carvable = 0;
            foreach (var kv in marked) { if (kv.Value >= DictGet(total, kv.Key)) prot.Add(kv.Key); else carvable += kv.Value; }
            if (carvable == 0) return "can't carve a whole piece — deselect (click empty space) to remove it";

            bool IsCarved(int f) => f >= 0 && f < nF && !P.Faces[f].IsUnused
                                    && touched.Contains(f) && selection.Contains(PieceMap[f]) && !prot.Contains(PieceMap[f]);

            // Blobs of connected carved faces.
            var uf = new UnionFind(nF);
            MeshOps.ForEachInteriorEdge(P, (f1, f2, a, b) =>
            {
                if (IsCarved(f1) && IsCarved(f2)) uf.Union(f1, f2);
            });
            var blobRoots = new HashSet<int>();
            for (int f = 0; f < nF; f++) if (IsCarved(f)) blobRoots.Add(uf.Find(f));

            // Tally each blob's shared border with each region OUTSIDE the selection (selected pieces excluded,
            // so the carve cannot heal back into the selection it left).
            var tally = new Dictionary<int, Dictionary<int, int>>();
            MeshOps.ForEachInteriorEdge(P, (f1, f2, a, b) =>
            {
                bool c1 = IsCarved(f1), c2 = IsCarved(f2);
                if (c1 == c2) return;
                int cf = c1 ? f1 : f2, sf = c1 ? f2 : f1, sreg = PieceMap[sf];
                if (selection.Contains(sreg)) return;            // inside the selection -> excluded
                int blob = uf.Find(cf);
                if (!tally.TryGetValue(blob, out var d)) { d = new Dictionary<int, int>(); tally[blob] = d; }
                d[sreg] = DictGet(d, sreg) + 1;
            });

            // Target per blob: dominant neighbour outside the selection, else a fresh piece id (island).
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
                PieceMap[f] = target[uf.Find(f)]; carved++;
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
            int nF = P.Faces.Count;
            // within-region connectivity: union faces sharing an edge that have the SAME region id.
            var uf = new UnionFind(nF);
            MeshOps.ForEachInteriorEdge(P, (f1, f2, a, b) =>
            {
                if (PieceMap[f1] == PieceMap[f2]) uf.Union(f1, f2);
            });
            var blobSize = new Dictionary<int, int>();
            var regionBlobs = new Dictionary<int, List<int>>();
            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int r = PieceMap[f]; if (r < 0) continue;
                int root = uf.Find(f);
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
                    if (newIdFor.TryGetValue(uf.Find(f), out int nid)) { PieceMap[f] = nid; changed = true; }
                }
            }
            return changed;
        }

        // Multi-source grow assignment. Of the candidate `touched` faces, return a face -> piece map: each touched
        // face REACHABLE from the selection (a flood seeded from every selected face, stepping only through
        // selected faces or other touched candidates) is assigned to the SELECTED piece whose front reached it
        // first (BFS — nearest wins, ties by face index). Touched faces no front reaches are omitted (a
        // disconnected affordance — never applied). The provisional Shift+grow previews the keys Green 5 and the
        // remaining touched faces Green 2. Single-select is the special case (one source). Read-only; mutates nothing.
        public Dictionary<int, int> GrowAssign(HashSet<int> touched, HashSet<int> selection)
        {
            var result = new Dictionary<int, int>();
            if (PieceMap == null || _mesh == null || touched == null || touched.Count == 0 || selection == null || selection.Count == 0) return result;
            var P = _mesh; int nF = P.Faces.Count;
            var adj = new List<int>[nF];
            MeshOps.ForEachInteriorEdge(P, (f1, f2, a, b) =>
            {
                (adj[f1] ??= new List<int>()).Add(f2);
                (adj[f2] ??= new List<int>()).Add(f1);
            });
            var src = new int[nF]; for (int i = 0; i < nF; i++) src[i] = -1;
            var q = new Queue<int>();
            for (int f = 0; f < nF; f++)
                if (!P.Faces[f].IsUnused && selection.Contains(PieceMap[f])) { src[f] = PieceMap[f]; q.Enqueue(f); }   // seed: every selected face, labelled with its own piece
            while (q.Count > 0)
            {
                int f = q.Dequeue(); var nbrs = adj[f]; if (nbrs == null) continue;
                foreach (int nf in nbrs)
                {
                    if (src[nf] != -1) continue;                  // already claimed (selected seed or earlier front)
                    bool inSel = selection.Contains(PieceMap[nf]);
                    if (!inSel && !touched.Contains(nf)) continue;
                    src[nf] = src[f]; q.Enqueue(nf);
                    if (!inSel) result[nf] = src[f];              // a grown (touched, non-selected) face joins the source piece
                }
            }
            return result;
        }

        // Of the candidate faces, return the LARGEST edge-connected component (connectivity stepping only through
        // other candidates). Used by the provisional Shift+mint (no selection): the main blob previews Green 5 and
        // becomes the new region; disconnected strays preview Green 2 and are dropped on release — so a mint never
        // spawns stray single-triangle pieces. Same union-find pass as the other grow queries; read-only.
        public HashSet<int> LargestComponent(HashSet<int> touched)
        {
            var result = new HashSet<int>();
            if (PieceMap == null || _mesh == null || touched == null || touched.Count == 0) return result;
            var P = _mesh; int nF = P.Faces.Count;
            var uf = new UnionFind(nF);
            MeshOps.ForEachInteriorEdge(P, (f1, f2, a, b) =>
            {
                if (touched.Contains(f1) && touched.Contains(f2)) uf.Union(f1, f2);
            });
            var size = new Dictionary<int, int>();
            int best = -1, bestSize = 0;
            foreach (int f in touched)
            {
                if (f < 0 || f >= nF) continue;
                int r = uf.Find(f), s = DictGet(size, r) + 1; size[r] = s;
                if (s > bestSize) { bestSize = s; best = r; }
            }
            if (best < 0) return result;
            foreach (int f in touched) if (f >= 0 && f < nF && uf.Find(f) == best) result.Add(f);
            return result;
        }

        // Commit a single-piece grow / mint: assign the given faces to one region. Re-derives creases.
        public void ApplyGrow(HashSet<int> faces, PieceId active)
        {
            if (PieceMap == null || faces == null || active.Value < 0) return;
            bool changed = false;
            foreach (int f in faces)
                if (f >= 0 && f < PieceMap.Length && PieceMap[f] != active.Value) { PieceMap[f] = active.Value; changed = true; }
            if (changed) RegenCrease();
        }

        // Commit a multi-source grow: assign each face to the piece GrowAssign mapped it to. Re-derives creases.
        public void ApplyGrowMap(Dictionary<int, int> faceToPiece)
        {
            if (PieceMap == null || faceToPiece == null) return;
            bool changed = false;
            foreach (var kv in faceToPiece)
                if (kv.Key >= 0 && kv.Key < PieceMap.Length && PieceMap[kv.Key] != kv.Value) { PieceMap[kv.Key] = kv.Value; changed = true; }
            if (changed) RegenCrease();
        }

        // ===================== regen (re-derive the cached crease view) =====================

        // Creases are DERIVED from PieceMap (a crease = an interior edge between differing pieces). RegenCrease
        // marks the CreaseMap Transient stale; it rebuilds lazily (DeriveCreases) on the next read. Called after
        // every op + Seed. Lossy — rebuilt wholesale, no per-crease identity.
        public void RegenCrease() => CreaseMap.Rot();

        // The pull regen behind the CreaseMap Transient: build the crease set from the current PieceMap.
        HashSet<long> DeriveCreases()
        {
            var set = new HashSet<long>();
            if (_mesh == null || PieceMap == null) return set;
            var P = _mesh;
            int nH = P.Halfedges.Count;
            for (int h = 0; h < nH; h++)
            {
                if (P.Halfedges[h].IsUnused) continue;
                int pr = P.Halfedges.GetPairHalfedge(h); if (pr < 0 || pr < h) continue;
                int f1 = P.Halfedges[h].AdjacentFace, f2 = P.Halfedges[pr].AdjacentFace;
                if (f1 < 0 || f2 < 0 || PieceMap[f1] == PieceMap[f2]) continue;
                int a = P.Halfedges[h].StartVertex, b = P.Halfedges[pr].StartVertex;
                set.Add(EdgeKey(a, b));
            }
            return set;
        }

        // ===================== transactions (ITxAble) =====================

        // Apply / Invert a PieceDelta to the Real state (PieceMap), then regen the Transient view (CreaseMap).
        // The single persistent writer of PieceMap (driven only by Doc.Run / Redo / Undo). See DOC-TX-REFACTOR.md.
        public void Apply(IDelta d)
        {
            if (PieceMap == null || !(d is PieceDelta pd)) return;
            foreach (var o in pd.Ops) if (o.Face >= 0 && o.Face < PieceMap.Length) PieceMap[o.Face] = o.To;
            RegenCrease();
        }
        public void Invert(IDelta d)
        {
            if (PieceMap == null || !(d is PieceDelta pd)) return;
            foreach (var o in pd.Ops) if (o.Face >= 0 && o.Face < PieceMap.Length) PieceMap[o.Face] = o.From;
            RegenCrease();
        }

        // Run an in-place mutator, capture the net PieceMap change as a delta, then ROLL BACK — leaving Real and
        // Transient unchanged. The returned delta is what Doc.Run applies for real. Lets the intricate in-place
        // ops (Delete / Carve / Grow / Mint + SplitDisconnected) be reused verbatim as delta-producing Commands.
        public PieceDelta ComputeDelta(Action mutate)
        {
            if (PieceMap == null) { mutate(); return new PieceDelta(new List<Op>()); }
            var before = (int[])PieceMap.Clone();
            mutate();                                       // existing logic mutates PieceMap (+ RegenCrease)
            var ops = new List<Op>();
            int n = Math.Min(before.Length, PieceMap.Length);
            for (int f = 0; f < n; f++) if (PieceMap[f] != before[f]) ops.Add(new Op(f, before[f], PieceMap[f]));
            PieceMap = before; RegenCrease();               // roll Real + Transient back; Doc.Run re-applies the delta
            return new PieceDelta(ops);
        }

        // ===================== queries (read-only) =====================

        // Merge mapping for a selection: each selected piece -> the survivor of its CONNECTED COMPONENT among the
        // selection (the min id reachable through adjacent selected pieces). A piece adjacent to no other selected
        // piece maps to itself. So Merge fuses every adjacent cluster independently and leaves isolated pieces
        // alone: select {A,B,C} with A|B sharing a border but C off on its own -> A,B -> min(A,B), C -> C. A piece
        // that maps to a different id is one that will actually move. Read-only.
        public Dictionary<int, int> MergeGroups(HashSet<int> selection)
        {
            var map = new Dictionary<int, int>();
            if (PieceMap == null || _mesh == null || selection == null || selection.Count == 0) return map;
            var P = _mesh; int nF = P.Faces.Count;
            var uf = new UnionFind(nF);
            // union faces of ADJACENT selected pieces -> components of the selection
            MeshOps.ForEachInteriorEdge(P, (f1, f2, a, b) =>
            {
                if (selection.Contains(PieceMap[f1]) && selection.Contains(PieceMap[f2])) uf.Union(f1, f2);
            });
            var survivor = new Dictionary<int, int>();   // component root -> min selected piece id in it
            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int pid = PieceMap[f]; if (!selection.Contains(pid)) continue;
                int r = uf.Find(f);
                if (!survivor.TryGetValue(r, out int mn) || pid < mn) survivor[r] = pid;
            }
            for (int f = 0; f < nF; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int pid = PieceMap[f]; if (!selection.Contains(pid) || map.ContainsKey(pid)) continue;
                map[pid] = survivor[uf.Find(f)];
            }
            return map;
        }

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
