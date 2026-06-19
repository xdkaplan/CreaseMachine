using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Plankton;

namespace CreaseStudio
{
    // Uploads a PlanktonMesh to the GPU and draws it with a MatCap / lit-sphere shader (a texture
    // sampled by the view-space surface normal). Two upload modes: WELDED/smooth (shared vertices
    // with area-weighted averaged normals) and UNWELDED/flat (each triangle its own 3 vertices +
    // face normal, which exposes the planar panels). The fragment shader orients each normal toward
    // the camera (n.z >= 0), so a globally-inward mesh or mixed winding can't read as "reversed".
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

        // flat = false -> welded/smooth (shared verts, averaged normals); true -> unwelded/faceted
        // (each triangle its own 3 verts + face normal), which reveals the planar panels.
        public void Upload(PlanktonMesh P, bool flat = false)
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

            // Build vertex/normal/index buffers. No winding re-orientation: there's no evidence the
            // welded mesh has inconsistent winding (the earlier "reversed" look was the missing depth
            // buffer, now fixed), and the fragment shader orients each normal toward the camera anyway.
            float[] posF, nrmF;
            uint[] indices;
            if (!flat)
            {
                // WELDED / smooth: area-weighted averaged vertex normals -> soft shading across edges.
                var nrm = new Vector3[used];
                foreach (var (a0, b0, c0) in tris)
                {
                    Vector3 fn = Vector3.Cross(pos[b0] - pos[a0], pos[c0] - pos[a0]); // 2*area * unit normal
                    nrm[a0] += fn; nrm[b0] += fn; nrm[c0] += fn;
                }
                posF = new float[used * 3];
                nrmF = new float[used * 3];
                for (int i = 0; i < used; i++)
                {
                    posF[i * 3] = pos[i].X; posF[i * 3 + 1] = pos[i].Y; posF[i * 3 + 2] = pos[i].Z;
                    Vector3 n = nrm[i].LengthSquared > 1e-20f ? Vector3.Normalize(nrm[i]) : Vector3.UnitZ;
                    nrmF[i * 3] = n.X; nrmF[i * 3 + 1] = n.Y; nrmF[i * 3 + 2] = n.Z;
                }
                indices = new uint[tris.Count * 3];
                for (int t = 0; t < tris.Count; t++)
                {
                    indices[t * 3] = (uint)tris[t].a;
                    indices[t * 3 + 1] = (uint)tris[t].b;
                    indices[t * 3 + 2] = (uint)tris[t].c;
                }
            }
            else
            {
                // UNWELDED / flat: expand each triangle to its own 3 verts sharing the face normal
                // -> hard facets that expose the planar panels the developability flow is forming.
                int n3 = tris.Count * 3;
                posF = new float[n3 * 3];
                nrmF = new float[n3 * 3];
                indices = new uint[n3];
                int k = 0;
                foreach (var (a0, b0, c0) in tris)
                {
                    Vector3 pa = pos[a0], pb = pos[b0], pc = pos[c0];
                    Vector3 fn = Vector3.Cross(pb - pa, pc - pa);
                    fn = fn.LengthSquared > 1e-20f ? Vector3.Normalize(fn) : Vector3.UnitZ;
                    posF[k] = pa.X; posF[k + 1] = pa.Y; posF[k + 2] = pa.Z; nrmF[k] = fn.X; nrmF[k + 1] = fn.Y; nrmF[k + 2] = fn.Z; k += 3;
                    posF[k] = pb.X; posF[k + 1] = pb.Y; posF[k + 2] = pb.Z; nrmF[k] = fn.X; nrmF[k + 1] = fn.Y; nrmF[k + 2] = fn.Z; k += 3;
                    posF[k] = pc.X; posF[k + 1] = pc.Y; posF[k + 2] = pc.Z; nrmF[k] = fn.X; nrmF[k + 1] = fn.Y; nrmF[k + 2] = fn.Z; k += 3;
                }
                for (int i = 0; i < n3; i++) indices[i] = (uint)i;
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
