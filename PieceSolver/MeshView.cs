using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Plankton;

namespace PieceSolver
{
    // Uploads a PlanktonMesh to the GPU and draws it with a MatCap / lit-sphere shader (a texture
    // sampled by the view-space surface normal). Two upload modes: WELDED/smooth (shared vertices
    // with area-weighted averaged normals) and UNWELDED/flat (each triangle its own 3 vertices +
    // face normal, which exposes the planar panels). The fragment shader orients each normal toward
    // the camera (n.z >= 0), so a globally-inward mesh or mixed winding can't read as "reversed".
    sealed class MeshView : IDisposable
    {
        int _vao, _vboPos, _vboNrm, _ebo, _prog;
        int _uMvp, _uView, _uNormalMat, _uMatcap, _uHasMatcap, _uSharpness, _uFacetExp, _uEdge, _uEdgeColor;
        int _uNoise, _uLicMode, _uNoiseFreq, _uLicStep, _uFieldMax, _uLicStrength, _uLicTaps, _uCurvMin, _uCurvMax;
        int _tex;
        int _vboField;        // per-vertex direction field (attribute @2), for the surface LIC
        int _noiseTex;        // solid (3D) blue-noise volume the LIC convolves along the field
        int[] _vMap;          // original-vertex -> compacted-vertex index (set by Upload; -1 = unused)
        int _usedCount;       // compacted vertex count (matches the @0/@1 buffers)
        float _fieldMax = 1f; // magnitude normaliser for the field tint

        // --- Surface-LIC controls (set per frame by the host) ---
        public int LicMode = 0;            // 0 = off, 1 = ruling field, 2 = gradient field
        public float NoiseFreq = 4f;       // model-space position -> noise texcoord scale (tiles across the mesh)
        public float LicStep = 0.05f;      // march length per convolution tap, model units
        public int   LicTaps = 30;         // convolution taps each side (streak length); live uniform uLicTaps
        public float CurvMin = 0.05f, CurvMax = 0.2f; // ruling curvature remap window (smoothstep levels)
        public float LicStrength = 0.7f;   // 0..1 depth of the grain modulation on the matcap
        bool _hasMatcap;
        int _indexCount;
        bool _ready;

        public Vector3 Center { get; private set; }
        public float Radius { get; private set; } = 1f;
        public float Sharpness = 1f;     // 0 = smooth, 1 = faceted; set per frame from the Facet slider
        public float FacetExp = 1f;      // facet response exponent (Curve slider)
        public Vector3 ModelOffset = Vector3.Zero;   // world translation applied before view (draw a mesh beside another)
        public bool HasMesh => _ready;   // true once a mesh has been uploaded
        public bool ShowEdges = false;   // overlay the triangle edges (used for the flat map M', which is otherwise a featureless flat blob)
        public Vector3 EdgeColor = new Vector3(0.10f, 0.10f, 0.13f);
        public bool ShowRulings = false;                                  // overlay per-vertex ruling segments
        public Vector3 RulingColor = new Vector3(1.0f, 0.82f, 0.18f);     // gold
        int _rulVao, _rulVbo, _rulCount;                                  // GL_LINES buffer for rulings (positions only)
        public bool ShowSeams = false;                                    // overlay fixed B-spline seam wires
        public Vector3 SeamColor = new Vector3(0.30f, 0.85f, 1.0f);       // cyan: the smooth degree-3 curve
        public Vector3 SeamCtrlColor = new Vector3(1.0f, 0.65f, 0.20f);   // amber: control polygon + control points
        int _seamVao, _seamVbo, _seamCount;                               // GL_LINES buffer for the seam curve
        int _seamCtrlVao, _seamCtrlVbo, _seamCtrlCount;                   // GL_LINES buffer for the control polygon + crosses

        const string VERT = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec3 aField;    // per-vertex direction field (model space, length = strength)
uniform mat4 uMvp;
uniform mat4 uView;
uniform mat3 uNormalMat;
out vec3 vN;
out vec3 vViewPos;
out vec3 vField;
out vec3 vModelPos;
void main() {
    gl_Position = uMvp * vec4(aPos, 1.0);
    vN = uNormalMat * aNormal;            // smooth (averaged) normal -> view space
    vViewPos = (uView * vec4(aPos, 1.0)).xyz;   // view-space position, for the faceted normal
    vField = aField;                      // interpolated across the triangle; re-normalized per fragment
    vModelPos = aPos;                     // object-space position, for sampling the solid noise (LIC)
}";

        const string FRAG = @"#version 330 core
in vec3 vN;
in vec3 vViewPos;
in vec3 vField;
in vec3 vModelPos;
out vec4 FragColor;
uniform sampler2D uMatcap;
uniform sampler3D uNoise;     // solid blue-noise volume, GL_REPEAT-wrapped
uniform int uHasMatcap;
uniform float uSharpness;     // 0 = smooth (averaged normal), 1 = faceted (per-face normal)
uniform float uFacetExp;      // response curve: blend = pow(sharpness, exp). 1 = linear
uniform int uEdge;            // 1 = edge/wireframe pass -> output solid uEdgeColor
uniform vec3 uEdgeColor;
uniform int uLicMode;         // 0 = off, 1/2 = on (the field is chosen host-side)
uniform float uNoiseFreq;     // model position -> noise texcoord scale
uniform float uLicStep;       // march length per tap, model units
uniform float uFieldMax;      // curvature normaliser (kappa_max scale)
uniform float uLicStrength;   // 0..1 depth of the grain modulation
uniform int uLicTaps;         // LIC convolution taps each side (streak length); live -> no recompile
uniform float uCurvMin;       // ruling curvature remap: kappa_max at/below this -> fully low (specks)
uniform float uCurvMax;       // ruling curvature remap: kappa_max at/above this -> fully high (bold hairs)

void main() {
    if (uEdge == 1) { FragColor = vec4(uEdgeColor, 1.0); return; }
    vec3 sn = normalize(vN);
    // Geometric face normal from screen-space derivatives = the 'unwelded' normal, for free.
    vec3 fn = normalize(cross(dFdx(vViewPos), dFdy(vViewPos)));
    if (dot(fn, sn) < 0.0) fn = -fn;       // align with the smooth normal before blending
    float s = pow(clamp(uSharpness, 0.0, 1.0), max(uFacetExp, 0.001));
    vec3 n = normalize(mix(sn, fn, s));
    if (n.z < 0.0) n = -n;       // orient toward camera (view looks down -z) - winding-independent

    vec3 surf;
    if (uHasMatcap == 1) { vec2 uv = n.xy * 0.5 + 0.5; surf = texture(uMatcap, uv).rgb; }
    else {
        vec3 L = normalize(vec3(0.35, 0.55, 0.75));
        float wrap = clamp(dot(n, L) * 0.5 + 0.5, 0.0, 1.0);
        surf = mix(vec3(0.16, 0.17, 0.21), vec3(0.82, 0.80, 0.77), wrap);
    }

    // Surface-LIC ruling overlay: convolve the solid noise along the per-fragment ruling direction
    // (symmetric +/-d taps -> a line, not an arrow). Per-vertex CURVATURE (kappa_max = 1/radius-of-max-
    // curvature, m) drives streak LENGTH and ALPHA - nothing on flat areas, short faint specks where
    // barely curved, long bold wind-swept hairs where tightly curved. uLicTaps (Length) and uLicStrength
    // (Alpha) set the high-curvature extremes; curvature scales them down. Developable-vs-not reads from
    // the LIC coherence (combed vs swirly).
    if (uLicMode != 0) {
        float fmag = length(vField);
        float m = clamp(fmag / max(uFieldMax, 1e-8), 0.0, 1.0);
        float c = smoothstep(uCurvMin, uCurvMax, m);                    // remapped curvature (Curv min/max)
        float fade = smoothstep(0.0, 0.03, m);                          // kill grain where the field vanishes (no pop)
        float effLen = mix(2.0, float(uLicTaps) + 1.0, c);              // kernel half-width -> streak LENGTH
        float alpha  = uLicStrength * c * fade;                         // grain depth -> streak ALPHA (0 at/below Curv min)
        float grain = 1.0;
        if (alpha > 0.0) {
            vec3 d = vField / max(fmag, 1e-6);
            float acc = 0.0, wsum = 0.0;
            for (int i = -uLicTaps; i <= uLicTaps; i++) {
                float t = float(i);
                float wgt = max(0.0, 1.0 - abs(t) / effLen);            // window tapers at effLen (per-fragment length)
                vec3 sp = (vModelPos + d * (t * uLicStep)) * uNoiseFreq;
                acc += wgt * textureLod(uNoise, sp, 0.0).r;   // explicit LOD: implicit derivs undefined in this branch
                wsum += wgt;
            }
            float gg = acc / max(wsum, 1e-5);
            gg = clamp((gg - 0.5) * 2.2 + 0.5, 0.0, 1.0);                // contrast the streaks
            grain = mix(1.0 - alpha, 1.0, gg);
        }
        surf *= grain;                                                  // curvature-modulated ruling hairs
    }
    FragColor = vec4(surf, 1.0);
}";

        public void EnsureProgram()
        {
            if (_prog != 0) return;
            _prog = Link(VERT, FRAG);
            _uMvp = GL.GetUniformLocation(_prog, "uMvp");
            _uView = GL.GetUniformLocation(_prog, "uView");
            _uNormalMat = GL.GetUniformLocation(_prog, "uNormalMat");
            _uMatcap = GL.GetUniformLocation(_prog, "uMatcap");
            _uHasMatcap = GL.GetUniformLocation(_prog, "uHasMatcap");
            _uSharpness = GL.GetUniformLocation(_prog, "uSharpness");
            _uFacetExp = GL.GetUniformLocation(_prog, "uFacetExp");
            _uEdge = GL.GetUniformLocation(_prog, "uEdge");
            _uEdgeColor = GL.GetUniformLocation(_prog, "uEdgeColor");
            _uNoise = GL.GetUniformLocation(_prog, "uNoise");
            _uLicMode = GL.GetUniformLocation(_prog, "uLicMode");
            _uNoiseFreq = GL.GetUniformLocation(_prog, "uNoiseFreq");
            _uLicStep = GL.GetUniformLocation(_prog, "uLicStep");
            _uFieldMax = GL.GetUniformLocation(_prog, "uFieldMax");
            _uLicStrength = GL.GetUniformLocation(_prog, "uLicStrength");
            _uLicTaps = GL.GetUniformLocation(_prog, "uLicTaps");
            _uCurvMin = GL.GetUniformLocation(_prog, "uCurvMin");
            _uCurvMax = GL.GetUniformLocation(_prog, "uCurvMax");
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

        // Always uploads the welded/smooth mesh (shared verts + area-averaged normals). The faceted
        // look is produced in the shader from screen-space derivatives, blended by uSharpness — so
        // there's no separate unwelded upload, and "facet amount" is a live shader knob.
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

            // Area-weighted averaged vertex normals -> smooth shading. No winding re-orientation: the
            // welded mesh has no proven inconsistent winding, and the fragment shader orients each
            // normal toward the camera anyway. (The faceted look is the shader's job, not the upload's.)
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
            // The field VBO (@2) is sized to the PREVIOUS vertex count; disable it until SetField
            // re-uploads for the new mesh, so a stale buffer can't be read out of range. A disabled
            // attribute reads constant 0 -> the LIC sees a zero field -> no grain (just the matcap).
            GL.DisableVertexAttribArray(2);
            GL.BindVertexArray(0);
            _vMap = map; _usedCount = used;
            _ready = _indexCount > 0;
        }

        // Upload the per-vertex direction field for the surface LIC. dir3 is indexed by ORIGINAL vertex
        // (length nV*3); it is remapped to the compacted vertex order Upload produced. fieldMax is the
        // magnitude used to normalise the tint. Call after Upload (and after each mesh change).
        public void SetField(float[] dir3, float fieldMax)
        {
            if (_vMap == null || _vao == 0 || dir3 == null) return;
            var fF = new float[_usedCount * 3];
            for (int v = 0; v < _vMap.Length; v++)
            {
                int u = _vMap[v]; if (u < 0) continue;
                fF[u * 3] = dir3[v * 3]; fF[u * 3 + 1] = dir3[v * 3 + 1]; fF[u * 3 + 2] = dir3[v * 3 + 2];
            }
            if (_vboField == 0) _vboField = GL.GenBuffer();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboField);
            GL.BufferData(BufferTarget.ArrayBuffer, fF.Length * sizeof(float), fF, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(2);
            GL.BindVertexArray(0);
            _fieldMax = fieldMax;
        }

        // Upload the solid (3D) blue-noise volume the LIC convolves the field along. n^3 single-channel
        // bytes; GL_REPEAT-wrapped so it tiles seamlessly across the mesh.
        public void SetNoiseVolume(byte[] data, int n)
        {
            EnsureProgram();
            if (_noiseTex == 0) _noiseTex = GL.GenTexture();
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.BindTexture(TextureTarget.Texture3D, _noiseTex);
            GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R8, n, n, n, 0,
                PixelFormat.Red, PixelType.UnsignedByte, data);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.Repeat);
            GL.BindTexture(TextureTarget.Texture3D, 0);
        }

        // Upload ruling line segments (positions only, GL_LINES) computed externally from the live mesh.
        // posF = [x0,y0,z0, x1,y1,z1, ...]; drawn flat-coloured via the shader's uEdge path.
        public void SetRulings(float[] posF)
        {
            EnsureProgram();
            _rulCount = posF.Length / 3;
            if (_rulVao == 0) { _rulVao = GL.GenVertexArray(); _rulVbo = GL.GenBuffer(); }
            GL.BindVertexArray(_rulVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _rulVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, posF.Length * sizeof(float), posF, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.BindVertexArray(0);
        }

        // Upload the fitted seam curve (GL_LINES position pairs), same flat-coloured path as rulings.
        public void SetSeams(float[] posF)
        {
            _seamCount = posF.Length / 3; if (_seamCount == 0) return;
            EnsureProgram();
            if (_seamVao == 0) { _seamVao = GL.GenVertexArray(); _seamVbo = GL.GenBuffer(); }
            GL.BindVertexArray(_seamVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _seamVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, posF.Length * sizeof(float), posF, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.BindVertexArray(0);
        }

        // Upload the seam control polygon + control-point markers (GL_LINES position pairs).
        public void SetSeamControls(float[] posF)
        {
            _seamCtrlCount = posF.Length / 3; if (_seamCtrlCount == 0) return;
            EnsureProgram();
            if (_seamCtrlVao == 0) { _seamCtrlVao = GL.GenVertexArray(); _seamCtrlVbo = GL.GenBuffer(); }
            GL.BindVertexArray(_seamCtrlVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _seamCtrlVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, posF.Length * sizeof(float), posF, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.BindVertexArray(0);
        }

        public void Draw(Matrix4 view, Matrix4 proj)
        {
            if (!_ready) return;
            Matrix4 model = Matrix4.CreateTranslation(ModelOffset);   // pure translation; doesn't affect normals
            Matrix4 mvp = model * view * proj;         // OpenTK row-major + transpose=false convention
            Matrix3 normalMat = new Matrix3(view);     // rigid view -> rotation is the normal transform
            GL.UseProgram(_prog);
            GL.UniformMatrix4(_uMvp, false, ref mvp);
            GL.UniformMatrix4(_uView, false, ref view);   // for the view-space position (faceted normal)
            GL.UniformMatrix3(_uNormalMat, false, ref normalMat);
            GL.Uniform1(_uSharpness, Sharpness);
            GL.Uniform1(_uFacetExp, FacetExp);
            GL.Uniform1(_uHasMatcap, _hasMatcap ? 1 : 0);
            if (_hasMatcap)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _tex);
                GL.Uniform1(_uMatcap, 0);
            }
            // Surface-LIC uniforms. The noise sampler is pinned to unit 1 (never unit 0, so it can't
            // collide with the sampler2D matcap on a strict driver). LicMode is forced 0 unless a noise
            // volume exists, so an un-fed view just draws the matcap.
            GL.Uniform1(_uLicMode, _noiseTex != 0 ? LicMode : 0);
            GL.Uniform1(_uNoiseFreq, NoiseFreq);
            GL.Uniform1(_uLicStep, LicStep);
            GL.Uniform1(_uFieldMax, _fieldMax);
            GL.Uniform1(_uLicStrength, LicStrength);
            GL.Uniform1(_uLicTaps, LicTaps);
            GL.Uniform1(_uCurvMin, CurvMin);
            GL.Uniform1(_uCurvMax, CurvMax);
            GL.Uniform1(_uNoise, 1);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture3D, _noiseTex);
            GL.BindVertexArray(_vao);
            // Filled pass. When edges are on, push the fill back a touch (polygon offset) so the
            // wireframe overlay drawn at true depth wins the depth test (clean hidden-line look).
            GL.Uniform1(_uEdge, 0);
            if (ShowEdges) { GL.Enable(EnableCap.PolygonOffsetFill); GL.PolygonOffset(1f, 1f); }
            GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
            if (ShowEdges)
            {
                GL.Disable(EnableCap.PolygonOffsetFill);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                GL.Uniform1(_uEdge, 1);
                GL.Uniform3(_uEdgeColor, EdgeColor.X, EdgeColor.Y, EdgeColor.Z);
                GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            }
            // Ruling overlay: GL_LINES from a separate position-only buffer, flat-coloured (uEdge=1). Lifted
            // off the surface in the field computation, so a normal depth test gives a clean on-surface look.
            if (ShowRulings && _rulCount > 0)
            {
                GL.BindVertexArray(_rulVao);
                GL.Uniform1(_uEdge, 1);
                GL.Uniform3(_uEdgeColor, RulingColor.X, RulingColor.Y, RulingColor.Z);
                GL.LineWidth(1.5f);
                GL.DrawArrays(PrimitiveType.Lines, 0, _rulCount);
                GL.LineWidth(1f);
            }
            // Fixed B-spline seam wires: the smooth degree-3 curve (cyan) ...
            if (ShowSeams && _seamCount > 0)
            {
                GL.BindVertexArray(_seamVao);
                GL.Uniform1(_uEdge, 1);
                GL.Uniform3(_uEdgeColor, SeamColor.X, SeamColor.Y, SeamColor.Z);
                GL.LineWidth(2.5f);
                GL.DrawArrays(PrimitiveType.Lines, 0, _seamCount);
                GL.LineWidth(1f);
            }
            // ... and its control polygon + control-point markers (amber).
            if (ShowSeams && _seamCtrlCount > 0)
            {
                GL.BindVertexArray(_seamCtrlVao);
                GL.Uniform1(_uEdge, 1);
                GL.Uniform3(_uEdgeColor, SeamCtrlColor.X, SeamCtrlColor.Y, SeamCtrlColor.Z);
                GL.LineWidth(1.5f);
                GL.DrawArrays(PrimitiveType.Lines, 0, _seamCtrlCount);
                GL.LineWidth(1f);
            }
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
            // OpenTK's GL.ShaderSource marshals the source to UTF-8 but passes the .NET char COUNT as the
            // byte length, so a single non-ASCII char makes the driver read short and report a cryptic
            // 'pre-mature EOF' (the trailing brace is truncated). Catch it here with a clear message;
            // shader source MUST stay ASCII.
            for (int i = 0; i < src.Length; i++)
                if (src[i] > 127)
                    throw new Exception(type + " shader has a non-ASCII char '" + src[i] + "' (U+" +
                        ((int)src[i]).ToString("X4") + ") at index " + i + " — shaders must be ASCII (OpenTK truncates otherwise)");
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
            if (_vboField != 0) GL.DeleteBuffer(_vboField);
            if (_noiseTex != 0) GL.DeleteTexture(_noiseTex);
            if (_rulVao != 0) { GL.DeleteVertexArray(_rulVao); GL.DeleteBuffer(_rulVbo); }
            if (_seamVao != 0) { GL.DeleteVertexArray(_seamVao); GL.DeleteBuffer(_seamVbo); }
            if (_seamCtrlVao != 0) { GL.DeleteVertexArray(_seamCtrlVao); GL.DeleteBuffer(_seamCtrlVbo); }
            if (_tex != 0) GL.DeleteTexture(_tex);
            if (_prog != 0) GL.DeleteProgram(_prog);
        }
    }
}
