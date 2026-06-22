using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace PieceSolver
{
    // A subtle reference grid of dots on the world ground plane (Z = 0; Rhino Z-up) at a fixed world
    // spacing. World-anchored (dots sit on the global N*spacing lattice); rebuilt to cover the current
    // mesh footprint. Drawn as GL_POINTS, depth-tested so the mesh occludes the dots behind it.
    sealed class GroundGrid : IDisposable
    {
        int _vao, _vbo, _prog, _uMvp, _uColor;
        int _count;

        const string VERT = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uMvp;
void main() { gl_Position = uMvp * vec4(aPos, 1.0); }";

        const string FRAG = @"#version 330 core
out vec4 FragColor;
uniform vec3 uColor;
void main() { FragColor = vec4(uColor, 1.0); }";

        void EnsureProgram()
        {
            if (_prog != 0) return;
            int v = Compile(ShaderType.VertexShader, VERT);
            int f = Compile(ShaderType.FragmentShader, FRAG);
            _prog = GL.CreateProgram();
            GL.AttachShader(_prog, v); GL.AttachShader(_prog, f);
            GL.LinkProgram(_prog);
            GL.GetProgram(_prog, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new Exception("grid shader link failed: " + GL.GetProgramInfoLog(_prog));
            GL.DetachShader(_prog, v); GL.DetachShader(_prog, f);
            GL.DeleteShader(v); GL.DeleteShader(f);
            _uMvp = GL.GetUniformLocation(_prog, "uMvp");
            _uColor = GL.GetUniformLocation(_prog, "uColor");
        }

        // Build dots at (i*spacing, j*spacing, 0) on the global lattice, covering the mesh's XY footprint
        // plus a margin. Extent is clamped so the dot count stays sane at any mesh scale.
        public void Build(Vector3 meshCenter, float meshRadius, float spacing = 10f)
        {
            EnsureProgram();
            float half = Math.Clamp(meshRadius * 1.5f, 50f, 1000f);
            int i0 = (int)MathF.Floor((meshCenter.X - half) / spacing), i1 = (int)MathF.Ceiling((meshCenter.X + half) / spacing);
            int j0 = (int)MathF.Floor((meshCenter.Y - half) / spacing), j1 = (int)MathF.Ceiling((meshCenter.Y + half) / spacing);
            var pts = new List<float>((i1 - i0 + 1) * (j1 - j0 + 1) * 3);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                { pts.Add(i * spacing); pts.Add(j * spacing); pts.Add(0f); }
            _count = pts.Count / 3;

            if (_vao == 0) { _vao = GL.GenVertexArray(); _vbo = GL.GenBuffer(); }
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            var arr = pts.ToArray();
            GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.BindVertexArray(0);
        }

        public void Draw(Matrix4 view, Matrix4 proj)
        {
            if (_count == 0) return;
            Matrix4 mvp = view * proj;
            GL.UseProgram(_prog);
            GL.UniformMatrix4(_uMvp, false, ref mvp);
            GL.Uniform3(_uColor, 0.28f, 0.28f, 0.32f);   // subtle, just above the background
            GL.PointSize(2f);
            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Points, 0, _count);
            GL.BindVertexArray(0);
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
            if (_vao != 0) { GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); }
            if (_prog != 0) GL.DeleteProgram(_prog);
        }
    }
}
