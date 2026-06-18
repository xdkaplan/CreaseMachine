using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Plankton;

namespace CreaseStudio
{
    // Uploads a PlanktonMesh to the GPU and draws it with a procedural MatCap / lit-sphere shader.
    // The shading normal is the GEOMETRIC face normal recovered per-fragment from screen-space
    // position derivatives (cross(dFdx, dFdy)), oriented toward the camera. This is independent of
    // the mesh's (often inconsistent, post-weld) vertex-normal winding and of any normal-matrix
    // convention - it cannot be "reversed". Flat/faceted look, which also reads the panel structure
    // a developability flow produces. Call Upload() after the mesh changes; Draw() each frame.
    sealed class MeshView : IDisposable
    {
        int _vao, _vboPos, _ebo, _prog;
        int _uMvp, _uMV, _uMatcap, _uHasMatcap;
        int _tex;
        bool _hasMatcap;
        int _indexCount;
        bool _ready;

        public Vector3 Center { get; private set; }
        public float Radius { get; private set; } = 1f;

        const string VERT = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uMvp;
uniform mat4 uMV;
out vec3 vViewPos;
void main() {
    gl_Position = uMvp * vec4(aPos, 1.0);
    vViewPos = (uMV * vec4(aPos, 1.0)).xyz;   // view-space position, for the derivative normal
}";

        const string FRAG = @"#version 330 core
in vec3 vViewPos;
out vec4 FragColor;
uniform sampler2D uMatcap;
uniform int uHasMatcap;
void main() {
    // True face normal from the rendered triangle, in view space. View looks down -z, so a
    // camera-facing normal has n.z > 0; force it that way to ignore winding entirely.
    vec3 n = normalize(cross(dFdx(vViewPos), dFdy(vViewPos)));
    if (n.z < 0.0) n = -n;
    if (uHasMatcap == 1) {
        vec2 uv = n.xy * 0.5 + 0.5;          // standard matcap lookup: view-space normal -> sphere UV
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
            _uMV = GL.GetUniformLocation(_prog, "uMV");
            _uMatcap = GL.GetUniformLocation(_prog, "uMatcap");
            _uHasMatcap = GL.GetUniformLocation(_prog, "uHasMatcap");
        }

        // Upload a matcap texture (BGRA bytes, rows already bottom-up for GL). Sampled by the
        // view-space normal. Call on the GL thread.
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

        // Build compact (used-vertices-only) positions + triangle indices. No normals needed.
        public void Upload(PlanktonMesh P)
        {
            EnsureProgram();
            int nV = P.Vertices.Count;
            int[] map = new int[nV];
            int used = 0;
            for (int v = 0; v < nV; v++) map[v] = P.Vertices[v].IsUnused ? -1 : used++;

            var pos = new float[used * 3];
            var bb0 = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var bb1 = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int v = 0; v < nV; v++)
            {
                if (map[v] < 0) continue;
                var pv = P.Vertices[v];
                int i = map[v];
                pos[i * 3] = pv.X; pos[i * 3 + 1] = pv.Y; pos[i * 3 + 2] = pv.Z;
                var p = new Vector3(pv.X, pv.Y, pv.Z);
                bb0 = Vector3.ComponentMin(bb0, p); bb1 = Vector3.ComponentMax(bb1, p);
            }
            Center = (bb0 + bb1) * 0.5f;
            Radius = MathF.Max(1e-4f, (bb1 - bb0).Length * 0.5f);

            var idx = new System.Collections.Generic.List<uint>(P.Faces.Count * 3);
            for (int f = 0; f < P.Faces.Count; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int[] fv = P.Faces.GetFaceVertices(f);
                if (fv.Length != 3) continue;
                int a = map[fv[0]], b = map[fv[1]], c = map[fv[2]];
                if (a < 0 || b < 0 || c < 0) continue;
                idx.Add((uint)a); idx.Add((uint)b); idx.Add((uint)c);
            }
            uint[] indices = idx.ToArray();
            _indexCount = indices.Length;

            if (_vao == 0) { _vao = GL.GenVertexArray(); _vboPos = GL.GenBuffer(); _ebo = GL.GenBuffer(); }
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboPos);
            GL.BufferData(BufferTarget.ArrayBuffer, pos.Length * sizeof(float), pos, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.DynamicDraw);
            GL.BindVertexArray(0);
            _ready = _indexCount > 0;
        }

        public void Draw(Matrix4 view, Matrix4 proj)
        {
            if (!_ready) return;
            // OpenTK row-major + transpose=false: building the combined matrix in OpenTK's
            // (view*proj) order and using column-vector multiply in GLSL is correct (model = I).
            Matrix4 mvp = view * proj;
            GL.UseProgram(_prog);
            GL.UniformMatrix4(_uMvp, false, ref mvp);
            GL.UniformMatrix4(_uMV, false, ref view);
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
            if (_vao != 0) { GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vboPos); GL.DeleteBuffer(_ebo); }
            if (_tex != 0) GL.DeleteTexture(_tex);
            if (_prog != 0) GL.DeleteProgram(_prog);
        }
    }
}
