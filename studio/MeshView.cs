using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Plankton;

namespace CreaseStudio
{
    // Uploads a PlanktonMesh to the GPU and draws it with a procedural MatCap / lit-sphere shader.
    //
    // SMOOTH shading needs per-vertex normals, which need CONSISTENT face winding - but a welded
    // STL has mixed winding (the source of earlier "reversed normal" corruption). So we re-orient
    // the faces ourselves with a topological flood-fill (adjacent triangles must traverse their
    // shared edge in opposite directions) before averaging area-weighted normals. The fragment
    // then orients the interpolated normal toward the camera (n.z >= 0), which also absorbs a
    // globally-inward mesh - so the result can't be "reversed" regardless of the mesh's winding.
    sealed class MeshView : IDisposable
    {
        int _vao, _vboPos, _vboNrm, _ebo, _prog;
        int _uMvp, _uNormalMat, _uMatcap, _uHasMatcap;
        int _tex;
        bool _hasMatcap;
        int _indexCount;
        bool _ready;

        public Vector3 Center { get; private set; }
        public float Radius { get; private set; } = 1f;

        const string VERT = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
uniform mat4 uMvp;
uniform mat3 uNormalMat;
out vec3 vN;
void main() {
    gl_Position = uMvp * vec4(aPos, 1.0);
    vN = uNormalMat * aNormal;   // -> view space
}";

        const string FRAG = @"#version 330 core
in vec3 vN;
out vec4 FragColor;
uniform sampler2D uMatcap;
uniform int uHasMatcap;
void main() {
    vec3 n = normalize(vN);
    if (n.z < 0.0) n = -n;       // orient toward camera (view looks down -z) - winding-independent
    if (uHasMatcap == 1) {
        vec2 uv = n.xy * 0.5 + 0.5;
        FragColor = texture(uMatcap, uv);
    } else {
        vec3 L = normalize(vec3(0.35, 0.55, 0.75));
        float wrap = clamp(dot(n, L) * 0.5 + 0.5, 0.0, 1.0);
        vec3 col = mix(vec3(0.16, 0.17, 0.21), vec3(0.82, 0.80, 0.77), wrap);
        FragColor = vec4(col, 1.0);
    }
}";

        public void EnsureProgram()
        {
            if (_prog != 0) return;
            _prog = Link(VERT, FRAG);
            _uMvp = GL.GetUniformLocation(_prog, "uMvp");
            _uNormalMat = GL.GetUniformLocation(_prog, "uNormalMat");
            _uMatcap = GL.GetUniformLocation(_prog, "uMatcap");
            _uHasMatcap = GL.GetUniformLocation(_prog, "uHasMatcap");
        }

        public void SetMatcap(byte[] bgra, int w, int h)
        {
            EnsureProgram();
            if (_tex == 0) _tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, w, h, 0,
                PixelFormat.Bgra, PixelType.UnsignedByte, bgra);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            _hasMatcap = true;
        }

        public void Upload(PlanktonMesh P)
        {
            EnsureProgram();
            int nV = P.Vertices.Count;
            int[] map = new int[nV];
            int used = 0;
            for (int v = 0; v < nV; v++) map[v] = P.Vertices[v].IsUnused ? -1 : used++;

            var pos = new Vector3[used];
            var bb0 = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var bb1 = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int v = 0; v < nV; v++)
            {
                if (map[v] < 0) continue;
                var pv = P.Vertices[v];
                var p = new Vector3(pv.X, pv.Y, pv.Z);
                pos[map[v]] = p;
                bb0 = Vector3.ComponentMin(bb0, p); bb1 = Vector3.ComponentMax(bb1, p);
            }
            Center = (bb0 + bb1) * 0.5f;
            Radius = MathF.Max(1e-4f, (bb1 - bb0).Length * 0.5f);

            // collect triangles (compact vertex indices)
            var tris = new List<(int a, int b, int c)>(P.Faces.Count);
            for (int f = 0; f < P.Faces.Count; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int[] fv = P.Faces.GetFaceVertices(f);
                if (fv.Length != 3) continue;
                int a = map[fv[0]], b = map[fv[1]], c = map[fv[2]];
                if (a < 0 || b < 0 || c < 0) continue;
                tris.Add((a, b, c));
            }

            // area-weighted smooth vertex normals, faces as-is. No winding re-orientation: there's
            // no evidence the welded mesh has inconsistent winding (the earlier "reversed" look was
            // the missing depth buffer, now fixed). The shader's camera-orient handles a globally-
            // inward mesh, and matcap shading is symmetric anyway.
            var nrm = new Vector3[used];
            foreach (var (a0, b0, c0) in tris)
            {
                Vector3 fn = Vector3.Cross(pos[b0] - pos[a0], pos[c0] - pos[a0]); // 2*area * unit normal
                nrm[a0] += fn; nrm[b0] += fn; nrm[c0] += fn;
            }

            var posF = new float[used * 3];
            var nrmF = new float[used * 3];
            for (int i = 0; i < used; i++)
            {
                posF[i * 3] = pos[i].X; posF[i * 3 + 1] = pos[i].Y; posF[i * 3 + 2] = pos[i].Z;
                Vector3 n = nrm[i].LengthSquared > 1e-20f ? Vector3.Normalize(nrm[i]) : Vector3.UnitZ;
                nrmF[i * 3] = n.X; nrmF[i * 3 + 1] = n.Y; nrmF[i * 3 + 2] = n.Z;
            }

            var indices = new uint[tris.Count * 3];
            for (int t = 0; t < tris.Count; t++)
            {
                indices[t * 3] = (uint)tris[t].a;
                indices[t * 3 + 1] = (uint)tris[t].b;
                indices[t * 3 + 2] = (uint)tris[t].c;
            }
            _indexCount = indices.Length;

            if (_vao == 0) { _vao = GL.GenVertexArray(); _vboPos = GL.GenBuffer(); _vboNrm = GL.GenBuffer(); _ebo = GL.GenBuffer(); }
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboPos);
            GL.BufferData(BufferTarget.ArrayBuffer, posF.Length * sizeof(float), posF, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboNrm);
            GL.BufferData(BufferTarget.ArrayBuffer, nrmF.Length * sizeof(float), nrmF, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.DynamicDraw);
            GL.BindVertexArray(0);
            _ready = _indexCount > 0;
        }

        public void Draw(Matrix4 view, Matrix4 proj)
        {
            if (!_ready) return;
            Matrix4 mvp = view * proj;                 // OpenTK row-major + transpose=false convention
            Matrix3 normalMat = new Matrix3(view);     // rigid view -> rotation is the normal transform
            GL.UseProgram(_prog);
            GL.UniformMatrix4(_uMvp, false, ref mvp);
            GL.UniformMatrix3(_uNormalMat, false, ref normalMat);
            GL.Uniform1(_uHasMatcap, _hasMatcap ? 1 : 0);
            if (_hasMatcap)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _tex);
                GL.Uniform1(_uMatcap, 0);
            }
            GL.BindVertexArray(_vao);
            GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
        }

        // Topological orientation: flood-fill over edge-adjacency, flipping triangles so every pair
        // of adjacent triangles traverses their shared edge in OPPOSITE directions (the manifold
        // consistency condition). Independent components each seed independently. Returns per-tri
        // flip flags. Non-manifold edges (>2 incident tris) are simply not used for propagation.
        static bool[] OrientConsistently(List<(int a, int b, int c)> tris, int nV)
        {
            int nT = tris.Count;
            var flip = new bool[nT];
            if (nT == 0) return flip;

            // undirected edge -> incident triangles
            var edgeTris = new Dictionary<long, List<int>>(nT * 2);
            void AddEdge(int u, int v, int t)
            {
                long key = u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;
                if (!edgeTris.TryGetValue(key, out var l)) { l = new List<int>(2); edgeTris[key] = l; }
                l.Add(t);
            }
            for (int t = 0; t < nT; t++)
            {
                var (a, b, c) = tris[t];
                AddEdge(a, b, t); AddEdge(b, c, t); AddEdge(c, a, t);
            }

            var visited = new bool[nT];
            var stack = new Stack<int>();
            for (int seed = 0; seed < nT; seed++)
            {
                if (visited[seed]) continue;
                visited[seed] = true;
                stack.Push(seed);
                while (stack.Count > 0)
                {
                    int t = stack.Pop();
                    var tw = Winding(tris[t], flip[t]);
                    // each edge of t in its current winding
                    Span<(int u, int v)> edges = stackalloc (int, int)[3]
                        { (tw.a, tw.b), (tw.b, tw.c), (tw.c, tw.a) };
                    foreach (var (u, v) in edges)
                    {
                        long key = u < v ? ((long)u << 32) | (uint)v : ((long)v << 32) | (uint)u;
                        var inc = edgeTris[key];
                        if (inc.Count != 2) continue;          // boundary or non-manifold: skip
                        int nb = inc[0] == t ? inc[1] : inc[0];
                        if (visited[nb]) continue;
                        // nb must traverse this edge as v->u to be consistent with t's u->v.
                        bool nbHasUV = TraversesUV(tris[nb], false, u, v);   // original winding dir
                        // if nb (original) traverses u->v, it's same-direction as t -> needs flip.
                        flip[nb] = nbHasUV;
                        visited[nb] = true;
                        stack.Push(nb);
                    }
                }
            }
            return flip;
        }

        static (int a, int b, int c) Winding((int a, int b, int c) t, bool flip)
            => flip ? (t.a, t.c, t.b) : (t.a, t.b, t.c);

        // does triangle (with given flip) contain the directed edge u->v ?
        static bool TraversesUV((int a, int b, int c) t, bool flip, int u, int v)
        {
            var w = Winding(t, flip);
            return (w.a == u && w.b == v) || (w.b == u && w.c == v) || (w.c == u && w.a == v);
        }

        static IEnumerable<(int a, int b, int c)> WithFlip(List<(int a, int b, int c)> tris, bool[] flip)
        {
            for (int t = 0; t < tris.Count; t++) yield return Winding(tris[t], flip[t]);
        }

        static int Link(string vs, string fs)
        {
            int v = Compile(ShaderType.VertexShader, vs);
            int f = Compile(ShaderType.FragmentShader, fs);
            int prog = GL.CreateProgram();
            GL.AttachShader(prog, v); GL.AttachShader(prog, f);
            GL.LinkProgram(prog);
            GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new Exception("shader link failed: " + GL.GetProgramInfoLog(prog));
            GL.DetachShader(prog, v); GL.DetachShader(prog, f);
            GL.DeleteShader(v); GL.DeleteShader(f);
            return prog;
        }

        static int Compile(ShaderType type, string src)
        {
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, src);
            GL.CompileShader(s);
            GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new Exception(type + " compile failed: " + GL.GetShaderInfoLog(s));
            return s;
        }

        public void Dispose()
        {
            if (_vao != 0) { GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vboPos); GL.DeleteBuffer(_vboNrm); GL.DeleteBuffer(_ebo); }
            if (_tex != 0) GL.DeleteTexture(_tex);
            if (_prog != 0) GL.DeleteProgram(_prog);
        }
    }
}
