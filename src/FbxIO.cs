using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using Plankton;

namespace CreaseMachine
{
    /// <summary>
    /// Minimal binary-FBX (v7000+) reader. Pulls the first Geometry node's control points (the
    /// <c>Vertices</c> double-array) and <c>PolygonVertexIndex</c> (int-array, each polygon ended by a
    /// bitwise-complemented last index) and builds a <see cref="PlanktonMesh"/> WITHOUT welding — so the
    /// mesh's unwelded "unsmooth" seam topology (split vertices at brep-face boundaries) survives as
    /// separate connected components, one per face. zlib array payloads are inflated with the BCL
    /// <see cref="DeflateStream"/> (no external dependency). Binary FBX only (ASCII throws).
    ///
    /// This is the format that preserves Rhino's per-edge smooth/unsmooth property through export, which
    /// STL cannot. Validated against the project's Solid*.fbx set (each splits cleanly into 6 components).
    /// </summary>
    public static class FbxIO
    {
        public static PlanktonMesh LoadBinaryFbx(string path)
        {
            byte[] f = File.ReadAllBytes(path);
            if (f.Length < 27 || System.Text.Encoding.ASCII.GetString(f, 0, 18) != "Kaydara FBX Binary")
                throw new NotSupportedException("Not a binary FBX (ASCII FBX is unsupported): " + path);

            byte[] vb = ReadArray(f, FindNode(f, "Vertices"), 'd');
            int ncp = vb.Length / 24;                          // 3 doubles (24 bytes) per control point
            byte[] ib = ReadArray(f, FindNode(f, "PolygonVertexIndex"), 'i');
            int ni = ib.Length / 4;

            var m = new PlanktonMesh();
            for (int i = 0; i < ncp; i++)
                m.Vertices.Add(BitConverter.ToDouble(vb, 24 * i), BitConverter.ToDouble(vb, 24 * i + 8), BitConverter.ToDouble(vb, 24 * i + 16));

            var face = new List<int>();
            for (int k = 0; k < ni; k++)
            {
                int raw = BitConverter.ToInt32(ib, 4 * k);
                bool end = raw < 0;
                face.Add(end ? ~raw : raw);                    // last index of a polygon is stored as ~index
                if (!end) continue;
                if (face.Count == 3) m.Faces.AddFace(face[0], face[1], face[2]);
                else if (face.Count >= 4) for (int t = 1; t + 1 < face.Count; t++) m.Faces.AddFace(face[0], face[t], face[t + 1]);   // triangulate ngons (fan)
                face.Clear();
            }
            return m;
        }

        // Locate a node by name. In binary FBX a node record is [EndOffset][NumProps][PropListLen]
        // [NameLen:1][Name][Properties...], so the name is preceded by a single length byte and the first
        // property begins immediately after it. (Property strings carry a 4-byte length, so this won't
        // false-match them.) Returns the offset of the first property.
        static int FindNode(byte[] f, string name)
        {
            byte[] nb = System.Text.Encoding.ASCII.GetBytes(name);
            for (int i = 1; i < f.Length - nb.Length; i++)
            {
                if (f[i - 1] != nb.Length) continue;
                bool ok = true;
                for (int k = 0; k < nb.Length; k++) if (f[i + k] != nb[k]) { ok = false; break; }
                if (ok) return i + nb.Length;
            }
            throw new InvalidDataException("FBX node not found: " + name);
        }

        // Array property: [type:1]['d'/'i'/...][ArrayLength:4][Encoding:4][CompressedLength:4][payload].
        // Encoding 0 = raw; 1 = zlib-deflate (skip the 2-byte zlib header, inflate the rest).
        static byte[] ReadArray(byte[] f, int pos, char expectType)
        {
            char type = (char)f[pos]; pos++;
            if (type != expectType) throw new InvalidDataException($"FBX array type '{type}' != expected '{expectType}'");
            pos += 4;                                          // ArrayLength (element count; we use payload byte length)
            int enc = BitConverter.ToInt32(f, pos); pos += 4;
            int clen = BitConverter.ToInt32(f, pos); pos += 4;
            var comp = new byte[clen]; Array.Copy(f, pos, comp, 0, clen);
            if (enc == 0) return comp;
            using var ms = new MemoryStream(comp, 2, comp.Length - 2);   // skip 2-byte zlib header
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            using var o = new MemoryStream();
            ds.CopyTo(o);
            return o.ToArray();
        }
    }
}
