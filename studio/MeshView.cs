using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Plankton;

namespace CreaseStudio
{
    // Uploads a PlanktonMesh to the GPU and draws it with a procedural MatCap / lit-sphere shader
    // (view-space-normal shading - reads surface form, no light rig, no texture asset yet).
    // Call Upload() to (re)build buffers after the mesh changes; Draw() each frame.
    sealed class MeshView : IDisposable
    {
        int _vao, _vboPos, _vboNrm, _ebo, _prog;
        int _uMvp, _uNormal;
        int _indexCount;
        bool _ready;

        public Vector3 Center { get; private set; }
        public float Radius { get; private set; } = 1f;

        const string VERT = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
uniform mat4 uMvp;
uniform mat3 uNormal;
out vec3 vN;
void main() {
    gl_Position = uMvp * vec4(aPos, 1.0);
    vN = normalize(uNormal * aNormal);
}";

        // Procedural matcap: shade purely from the view-space normal. Soft wrapped key light +
        // faint rim, clay palette. (A real matcap texture lookup can replace this later.)
        const string FRAG = @"#version 330 core
in vec3 vN;
out vec4 FragColor;
void main() {
    vec3 n = normalize(vN);
    if (!gl_FrontFacing) n = -n;
    vec3 L = normalize(vec3(0.35, 0.55, 0.75));
    float wrap = clamp(dot(n, L) * 0.5 + 0.5, 0.0, 1.0);
    float rim  = pow(1.0 - clamp(n.z, 0.0, 1.0), 3.0);
    float spec = pow(clamp(dot(n, L), 0.0, 1.0), 32.0) * 0.25;
    vec3 shadow = vec3(0.16, 0.17, 0.21);
    vec3 lit    = vec3(0.82, 0.80, 0.77);
    vec3 col = mix(shadow, lit, wrap) + spec + rim * 0.10;
    FragColor = vec4(col, 1.0);
}";

        public void EnsureProgram()
        {
            if (_prog != 0) return;
            _prog = Link(VERT, FRAG);
            _uMvp = GL.GetUniformLocation(_prog, "uMvp");
            _uNormal = GL.GetUniformLocation(_prog, "uNormal");
        }

        // Build compact (used-vertices-only) position + area-weighted normal + index arrays.
        public void Upload(PlanktonMesh P)
        {
            EnsureProgram();
            int nV = P.Vertices.Count;
            int[] map = new int[nV];
            int used = 0;
            for (int v = 0; v < nV; v++) map[v] = P.Vertices[v].IsUnused ? -1 : used++;

            var pos = new float[used * 3];
            var nrm = new Vector3[used];
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

            // indices + accumulate area-weighted face normals into vertex normals
            var idx = new System.Collections.Generic.List<uint>(P.Faces.Count * 3);
            for (int f = 0; f < P.Faces.Count; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int[] fv = P.Faces.GetFaceVertices(f);
                if (fv.Length != 3) continue;
                int a = map[fv[0]], b = map[fv[1]], c = map[fv[2]];
                if (a < 0 || b < 0 || c < 0) continue;
                Vector3 pa = V(pos, a), pb = V(pos, b), pc = V(pos, c);
                Vector3 fn = Vector3.Cross(pb - pa, pc - pa); // length = 2*area, direction = normal
                nrm[a] += fn; nrm[b] += fn; nrm[c] += fn;
                idx.Add((uint)a); idx.Add((uint)b); idx.Add((uint)c);
            }
            var nrmFlat = new float[used * 3];
            for (int i = 0; i < used; i++)
            {
                Vector3 n = nrm[i].LengthSquared > 1e-20f ? Vector3.Normalize(nrm[i]) : Vector3.UnitZ;
                nrmFlat[i * 3] = n.X; nrmFlat[i * 3 + 1] = n.Y; nrmFlat[i * 3 + 2] = n.Z;
            }
            uint[] indices = idx.ToArray();
            _indexCount = indices.Length;

            if (_vao == 0)
            {
                _vao = GL.GenVertexArray();
                _vboPos = GL.GenBuffer();
                _vboNrm = GL.GenBuffer();
                _ebo = GL.GenBuffer();
            }
            GL.BindVertexArray(_vao);

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboPos);
            GL.BufferData(BufferTarget.ArrayBuffer, pos.Length * sizeof(float), pos, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboNrm);
            GL.BufferData(BufferTarget.ArrayBuffer, nrmFlat.Length * sizeof(float), nrmFlat, BufferUsageHint.DynamicDraw);
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
            // OpenTK row-major + transpose=false: GLSL reads the transpose, so building the combined
            // matrix in OpenTK's (model*view*proj) order and using column-vector multiply in GLSL is
            // correct. Model is identity here.
            Matrix4 mvp = view * proj;
            Matrix3 normalMat = new Matrix3(view);   // rigid view -> rotation suffices for normals

            GL.UseProgram(_prog);
            GL.UniformMatrix4(_uMvp, false, ref mvp);
            GL.UniformMatrix3(_uNormal, false, ref normalMat);
            GL.BindVertexArray(_vao);
            GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
        }

        static Vector3 V(float[] p, int i) { return new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]); }

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
            if (_prog != 0) GL.DeleteProgram(_prog);
        }
    }
}
