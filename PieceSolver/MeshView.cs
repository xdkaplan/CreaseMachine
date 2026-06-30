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
        int _vao, _vboPos, _vboNrm, _vboFaceN, _ebo, _prog;
        int _uMvp, _uView, _uNormalMat, _uMatcap, _uHasMatcap, _uSharpness, _uFacetExp, _uEdge, _uEdgeColor, _uUseFaceN;
        bool _useFaceN;       // last Upload supplied a per-face normal (@3) — n-gon/PQ mesh; faceting blends to it
        int _uNoise, _uLicMode, _uNoiseFreq, _uLicStep, _uFieldMax, _uLicStrength, _uLicTaps, _uCurvMin, _uCurvMax;
        int _uNeutral, _uEnv, _uHasShade, _uUseMatcap, _uShine;   // Shine: neutral+environment default-shading blend
        int _tex;
        int _texNeutral, _texEnv;   // default shading pair (neutral light + environment), blended by Shine
        bool _hasNeutral, _hasEnv;
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

        // --- Piece visualization: a per-piece-tinted render of the crease-bounded pieces. Uploaded SPLIT
        // (one set of 3 corners per triangle) so each piece carries its own colour + boundary-distance with
        // no bleed across creases. The baseline shader is flat tint; subagent aesthetics restyle PIECE_FRAG. ---
        int _pieceProg, _pieceVao, _pieceVboPos, _pieceVboNrm, _pieceVboCol, _pieceVboDist, _pieceVboEdge;
        int _uPMvp, _uPView, _uPNormalMat, _uPNeutral, _uPHasNeutral, _uPSharpness, _uPFacetExp, _uPInset;
        int _pieceVertCount;
        public bool ShowPieces = false;     // host sets this (auto-on after Propose) to draw the piece view
        public float InsetWidth = 0.05f;    // world-relative inset band width (the distance field is in world units)

        public Vector3 Center { get; private set; }
        public float Radius { get; private set; } = 1f;
        public float Sharpness = 1f;     // 0 = smooth, 1 = faceted; set per frame from the Facet slider
        public float FacetExp = 1f;      // facet response exponent (Curve slider)
        public float Shine = 0.4f;       // neutral(0) -> environment(1) default-shading blend (Shine slider)
        public bool UseMatcap = false;   // true -> the picked matcap overrides the neutral+Shine shading
        public Vector3 ModelOffset = Vector3.Zero;   // world translation applied before view (draw a mesh beside another)
        public bool HasMesh => _ready;   // true once a mesh has been uploaded
        public bool ShowEdges = false;   // overlay the triangle edges (used for the flat map M', which is otherwise a featureless flat blob)
        public Vector3 EdgeColor = OpenColor.Gray4;   // open-color Gray 4 (#ced4da)
        public bool ShowSeams = false;                                    // overlay fixed B-spline seam wires
        public Vector3 SeamColor = new Vector3(0.30f, 0.85f, 1.0f);       // cyan: the smooth degree-3 curve
        public Vector3 SeamCtrlColor = new Vector3(1.0f, 0.65f, 0.20f);   // amber: control polygon + control points
        int _seamVao, _seamVbo, _seamCount;                               // GL_LINES buffer for the seam curve
        int _seamCtrlVao, _seamCtrlVbo, _seamCtrlCount;                   // GL_LINES buffer for the control polygon + crosses
        public bool ShowCreases = false;                                  // overlay proposed piece-boundary creases
        public Vector3 CreaseColor = OpenColor.Gray7;   // open-color Gray 7 (#495057)
        int _creaseVao, _creaseVbo, _creaseCount;                         // GL_LINES buffer for proposed creases

        const string VERT = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec3 aField;    // per-vertex direction field (model space, length = strength)
layout(location=3) in vec3 aFaceN;    // per-FACE normal (n-gon/PQ meshes) -> the facet-end normal
uniform mat4 uMvp;
uniform mat4 uView;
uniform mat3 uNormalMat;
out vec3 vN;
out vec3 vViewPos;
out vec3 vField;
out vec3 vModelPos;
out vec3 vFaceN;
void main() {
    gl_Position = uMvp * vec4(aPos, 1.0);
    vN = uNormalMat * aNormal;            // smooth (averaged) normal -> view space
    vViewPos = (uView * vec4(aPos, 1.0)).xyz;   // view-space position, for the faceted normal
    vField = aField;                      // interpolated across the triangle; re-normalized per fragment
    vModelPos = aPos;                     // object-space position, for sampling the solid noise (LIC)
    vFaceN = uNormalMat * aFaceN;         // per-face normal -> view space (used when uUseFaceN==1)
}";

        const string FRAG = @"#version 330 core
in vec3 vN;
in vec3 vViewPos;
in vec3 vField;
in vec3 vModelPos;
in vec3 vFaceN;
out vec4 FragColor;
uniform sampler2D uMatcap;
uniform sampler3D uNoise;     // solid blue-noise volume, GL_REPEAT-wrapped
uniform int uHasMatcap;
uniform float uSharpness;     // 0 = smooth (averaged normal), 1 = faceted (per-face normal)
uniform int uUseFaceN;        // 1 = facet to the per-FACE normal attribute (n-gon/PQ); 0 = screen-space per-triangle
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
uniform sampler2D uNeutral;   // default shading: neutral lighting matcap
uniform sampler2D uEnv;       // default shading: environment-map matcap
uniform int uHasShade;        // the neutral+env pair is loaded
uniform int uUseMatcap;       // Advanced: sample the picked matcap instead of the neutral+Shine blend
uniform float uShine;         // neutral(0) -> environment(1)

void main() {
    if (uEdge == 1) { FragColor = vec4(uEdgeColor, 1.0); return; }
    vec3 sn = normalize(vN);
    // Geometric face normal from screen-space derivatives = the 'unwelded' normal, for free.
    // Facet-end normal: the per-FACE normal for n-gon/PQ meshes (so a quad's false diagonal never shows), else
    // the screen-space per-triangle normal (fine for triangle meshes - they have no false diagonal).
    vec3 fn = (uUseFaceN == 1) ? normalize(vFaceN) : normalize(cross(dFdx(vViewPos), dFdy(vViewPos)));
    if (dot(fn, sn) < 0.0) fn = -fn;       // align with the smooth normal before blending
    float s = pow(clamp(uSharpness, 0.0, 1.0), max(uFacetExp, 0.001));
    vec3 n = normalize(mix(sn, fn, s));
    if (n.z < 0.0) n = -n;       // orient toward camera (view looks down -z) - winding-independent

    vec3 surf;
    vec2 uv = n.xy * 0.5 + 0.5;
    if (uUseMatcap == 1 && uHasMatcap == 1) { surf = texture(uMatcap, uv).rgb; }   // Advanced: explicit matcap
    else if (uHasShade == 1) { surf = mix(texture(uNeutral, uv).rgb, texture(uEnv, uv).rgb, clamp(uShine, 0.0, 1.0)); }   // default: neutral + environment by Shine
    else if (uHasMatcap == 1) { surf = texture(uMatcap, uv).rgb; }   // fallback: single matcap
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
            _uUseFaceN = GL.GetUniformLocation(_prog, "uUseFaceN");
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
            _uNeutral = GL.GetUniformLocation(_prog, "uNeutral");
            _uEnv = GL.GetUniformLocation(_prog, "uEnv");
            _uHasShade = GL.GetUniformLocation(_prog, "uHasShade");
            _uUseMatcap = GL.GetUniformLocation(_prog, "uUseMatcap");
            _uShine = GL.GetUniformLocation(_prog, "uShine");
        }

        // Hand-picked override matcap (Advanced > Use Matcap).
        public void SetMatcap(byte[] bgra, int w, int h) { UploadTex(ref _tex, bgra, w, h); _hasMatcap = true; }
        // Default shading pair: neutral lighting + environment map, blended by Shine.
        public void SetNeutralMatcap(byte[] bgra, int w, int h) { UploadTex(ref _texNeutral, bgra, w, h); _hasNeutral = true; }
        public void SetEnvMatcap(byte[] bgra, int w, int h) { UploadTex(ref _texEnv, bgra, w, h); _hasEnv = true; }

        void UploadTex(ref int tex, byte[] bgra, int w, int h)
        {
            EnsureProgram();
            if (tex == 0) tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, w, h, 0,
                PixelFormat.Bgra, PixelType.UnsignedByte, bgra);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        // ===================== Piece visualization =====================

        const string PIECE_VERT = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec3 aPieceCol;
layout(location=3) in float aDist;
layout(location=4) in vec4 aEdgeDist;   // xyz = per-corner perp dist to this tri's 3 edges (BIG if not a crease); w = bridge-tri flat flag
uniform mat4 uMvp;
uniform mat4 uView;
uniform mat3 uNormalMat;
out vec3 vN;
out vec3 vViewPos;
out vec3 vPieceCol;
out float vDist;
out vec4 vEdgeDist;
void main() {
    gl_Position = uMvp * vec4(aPos, 1.0);
    vN = uNormalMat * aNormal;
    vViewPos = (uView * vec4(aPos, 1.0)).xyz;
    vPieceCol = aPieceCol;
    vDist = aDist;            // per-vertex geodesic dist to nearest crease (good in piece interiors)
    vEdgeDist = aEdgeDist;    // xyz interpolates to exact perp dist to this tri's crease edges; w = flat flag
}";

        // AESTHETIC 'letterpress': each piece reads as a thick matte card debossed at its crease edges.
        // The boundary band (vDist < uInset) is sculpted into a directional bevel using the SCREEN-SPACE
        // gradient of vDist as a virtual edge normal: bright sheen on the light-facing slope of the crease,
        // a soft occlusion roll on the shaded slope, and a thin dark seam line right at vDist==0. The result
        // is a tactile quilted / pressed-card panel look. World-stable (vDist is world space; nothing
        // animates) and distinct from contour-line / ceramic-tile / blueprint treatments. ASCII only.
        const string PIECE_FRAG = @"#version 330 core
in vec3 vN;
in vec3 vViewPos;
in vec3 vPieceCol;
in float vDist;
in vec4 vEdgeDist;
out vec4 FragColor;
uniform sampler2D uNeutral;
uniform int uHasNeutral;
uniform float uSharpness;
uniform float uFacetExp;
uniform float uInset;        // world-relative inset band width

void main() {
    // --- base surface normal (smooth<->faceted blend, same controls as baseline) ---
    vec3 sn = normalize(vN);
    vec3 fn = normalize(cross(dFdx(vViewPos), dFdy(vViewPos)));
    if (dot(fn, sn) < 0.0) fn = -fn;
    float s = pow(clamp(uSharpness, 0.0, 1.0), max(uFacetExp, 0.001));
    vec3 n = normalize(mix(sn, fn, s));
    if (n.z < 0.0) n = -n;

    // --- flat (no-deboss) baseline: the piece tint lit on the plain normal. The final colour mixes from
    // this toward the full deboss, so 'effect strength' (and the bridge-triangle flat case) is a simple lerp.
    vec3 tint = mix(vPieceCol, vPieceCol * 1.06, 0.5);
    vec3 litFlat = (uHasNeutral == 1) ? texture(uNeutral, n.xy * 0.5 + 0.5).rgb : vec3(clamp(n.z, 0.0, 1.0));
    vec3 faceFlat = litFlat * tint;

    // --- corrected distance-to-crease: the per-vertex field (vDist) collapses to ~0 across a triangle
    // whose corners all sit on creases (e.g. a two-crease 'V' triangle). vEdgeDist interpolates to the
    // EXACT perpendicular distance to this triangle's own crease edges (BIG sentinel for non-crease
    // edges), so take the nearest, then max() with the per-vertex field so deeper interiors stay smooth. ---
    float own = min(min(vEdgeDist.x, vEdgeDist.y), vEdgeDist.z);
    float d = max(vDist, (own < 1e8) ? own : 0.0);

    // --- normalized distance into the groove band: t==0 at crease, t==1 at the band rim ---
    float band = max(uInset, 1e-6);
    float t = clamp(d / band, 0.0, 1.0);

    // --- groove profile w(t): the single weight that SHAPES the whole deboss (bevel tilt, darkening and
    // sheen all scale by it). A smooth DOME -- STRONGEST at the crease (the centre, t=0), feathering to a
    // zero-slope landing at the rim (t=1). Both ends are flat, so the effect peaks broadly on the seam and
    // fades out softly (no thin spike, no harsh edge where the crease-edge triangle ends). ---
    float w = 0.5 + 0.5 * cos(t * 3.14159265);

    // --- debossed (pressed-in) bevel: tilt the shading normal toward the crease (downhill), scaled by w.
    // grad(d) points uphill (away from the crease); negate it for the deboss. ---
    vec2 g = vec2(dFdx(d), dFdy(d));
    vec2 dir = (length(g) > 1e-9) ? -normalize(g) : vec2(0.0, -1.0);
    float slope = 0.45 * w;   // bevel tilt strength (reduced 25% from 0.6)
    vec3 bn = normalize(n + vec3(dir * slope, 0.0));
    if (bn.z < 0.0) bn = -bn;

    // --- matcap lighting on the beveled normal (the groove wall catches a real highlight) ---
    vec3 lit = (uHasNeutral == 1) ? texture(uNeutral, bn.xy * 0.5 + 0.5).rgb
                                  : vec3(clamp(bn.z, 0.0, 1.0));
    vec3 face = lit * tint;

    // --- directional sheen on the inner wall + ellipsoidal occlusion (darkens toward the crease by w) ---
    float facing = clamp(dir.y * 0.7 + 0.3, 0.0, 1.0);
    face += w * facing * 0.30 * vec3(1.0);
    face *= mix(1.0, 0.45, w);

    // --- crisp seam line: a thin dark line right at the crease (world-stable, AA'd by fwidth) ---
    float aa = fwidth(d) * 1.5 + 1e-6;
    float seam = 1.0 - smoothstep(0.0, aa, d);       // 1 exactly on the crease
    face = mix(face, face * 0.30, seam * 0.9);

    // Triangles with NO crease edge (vEdgeDist.w == 1) get no deboss (flat tint -- a groove belongs only to a
    // triangle that contains a crease edge); crease-edge tris get the ellipsoidal deboss at 50% strength.
    float fx = (vEdgeDist.w > 0.5) ? 0.0 : 0.5;
    FragColor = vec4(mix(faceFlat, face, fx), 1.0);
}";

        void EnsurePieceProgram()
        {
            if (_pieceProg != 0) return;
            _pieceProg = Link(PIECE_VERT, PIECE_FRAG);
            _uPMvp = GL.GetUniformLocation(_pieceProg, "uMvp");
            _uPView = GL.GetUniformLocation(_pieceProg, "uView");
            _uPNormalMat = GL.GetUniformLocation(_pieceProg, "uNormalMat");
            _uPNeutral = GL.GetUniformLocation(_pieceProg, "uNeutral");
            _uPHasNeutral = GL.GetUniformLocation(_pieceProg, "uHasNeutral");
            _uPSharpness = GL.GetUniformLocation(_pieceProg, "uSharpness");
            _uPFacetExp = GL.GetUniformLocation(_pieceProg, "uFacetExp");
            _uPInset = GL.GetUniformLocation(_pieceProg, "uInset");
        }

        // Upload the piece-tinted mesh, SPLIT per triangle (3 corners each, sequential — no EBO). Arrays are
        // parallel: pos/nrm = 3 floats per corner, col = 3 floats per corner (the piece colour), dist = 1
        // float per corner (world distance to the piece's crease boundary). vertCount = pos.Length/3.
        public void SetPieces(float[] pos, float[] nrm, float[] col, float[] dist, float[] edge)
        {
            EnsurePieceProgram();
            _pieceVertCount = (pos == null) ? 0 : pos.Length / 3;
            if (_pieceVertCount == 0) return;
            if (_pieceVao == 0) { _pieceVao = GL.GenVertexArray(); _pieceVboPos = GL.GenBuffer(); _pieceVboNrm = GL.GenBuffer(); _pieceVboCol = GL.GenBuffer(); _pieceVboDist = GL.GenBuffer(); _pieceVboEdge = GL.GenBuffer(); }
            GL.BindVertexArray(_pieceVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _pieceVboPos);
            GL.BufferData(BufferTarget.ArrayBuffer, pos.Length * sizeof(float), pos, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0); GL.EnableVertexAttribArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _pieceVboNrm);
            GL.BufferData(BufferTarget.ArrayBuffer, nrm.Length * sizeof(float), nrm, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0); GL.EnableVertexAttribArray(1);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _pieceVboCol);
            GL.BufferData(BufferTarget.ArrayBuffer, col.Length * sizeof(float), col, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0); GL.EnableVertexAttribArray(2);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _pieceVboDist);
            GL.BufferData(BufferTarget.ArrayBuffer, dist.Length * sizeof(float), dist, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, 1 * sizeof(float), 0); GL.EnableVertexAttribArray(3);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _pieceVboEdge);
            GL.BufferData(BufferTarget.ArrayBuffer, edge.Length * sizeof(float), edge, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0); GL.EnableVertexAttribArray(4);
            GL.BindVertexArray(0);
        }

        public void ClearPieces() { _pieceVertCount = 0; }
        public bool HasPieces => _pieceVertCount > 0;

        // Draw the piece-tinted mesh (replaces the matcap mesh while ShowPieces is on). The neutral matcap
        // lights it; per-piece colour tints it. Sharpness/FacetExp reuse the Facet shading; uInset carries
        // the world-relative band width for the aesthetic shaders.
        public void DrawPieces(Matrix4 view, Matrix4 proj)
        {
            if (_pieceVertCount == 0 || _pieceProg == 0) return;
            Matrix4 model = Matrix4.CreateTranslation(ModelOffset);
            Matrix4 mvp = model * view * proj;
            Matrix3 normalMat = new Matrix3(view);
            GL.UseProgram(_pieceProg);
            GL.UniformMatrix4(_uPMvp, false, ref mvp);
            GL.UniformMatrix4(_uPView, false, ref view);
            GL.UniformMatrix3(_uPNormalMat, false, ref normalMat);
            GL.Uniform1(_uPSharpness, Sharpness);
            GL.Uniform1(_uPFacetExp, FacetExp);
            GL.Uniform1(_uPInset, InsetWidth);
            GL.Uniform1(_uPHasNeutral, _hasNeutral ? 1 : 0);
            GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, _texNeutral); GL.Uniform1(_uPNeutral, 0);
            // Push the fill back when creases are overlaid, so the orange crease lines win the near-surface
            // depth test against the piece fill (same hidden-line trick as the matcap path).
            bool offset = ShowCreases && _creaseCount > 0;
            if (offset) { GL.Enable(EnableCap.PolygonOffsetFill); GL.PolygonOffset(1f, 1f); }
            GL.BindVertexArray(_pieceVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _pieceVertCount);
            GL.BindVertexArray(0);
            if (offset) GL.Disable(EnableCap.PolygonOffsetFill);
        }

        // Always uploads the welded/smooth mesh (shared verts + area-averaged normals). The faceted
        // look is produced in the shader from screen-space derivatives, blended by uSharpness — so
        // there's no separate unwelded upload, and "facet amount" is a live shader knob.
        // posOverride (optional): per-ORIGINAL-vertex positions (length nV*3) to display instead of P's
        // own coordinates — used to preview the proposed/developed geometry while the live mesh stays M0.
        public void Upload(PlanktonMesh P, double[] posOverride = null)
        {
            EnsureProgram();
            int nV = P.Vertices.Count;
            bool useOv = posOverride != null && posOverride.Length == nV * 3;
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
                var p = useOv ? new Vector3((float)posOverride[v * 3], (float)posOverride[v * 3 + 1], (float)posOverride[v * 3 + 2])
                              : new Vector3(pv.X, pv.Y, pv.Z);
                pos[map[v]] = p;
                bb0 = Vector3.ComponentMin(bb0, p); bb1 = Vector3.ComponentMax(bb1, p);
            }
            Center = (bb0 + bb1) * 0.5f;
            Radius = MathF.Max(1e-4f, (bb1 - bb0).Length * 0.5f);

            // Shared averaged (smooth) vertex normals — face Newell normals accumulated to each face's verts.
            // This is the Facet=0 (smooth) end for BOTH paths.
            var nrm = new Vector3[used];
            for (int f = 0; f < P.Faces.Count; f++)
            {
                if (P.Faces[f].IsUnused) continue;
                int[] fv = P.Faces.GetFaceVertices(f);
                if (fv == null || fv.Length < 3) continue;
                int n = fv.Length; bool ok = true;
                for (int k = 0; k < n; k++) if (map[fv[k]] < 0) { ok = false; break; }
                if (!ok) continue;
                Vector3 fn = Vector3.Zero;
                for (int k = 0; k < n; k++) { var a = pos[map[fv[k]]]; var b = pos[map[fv[(k + 1) % n]]]; fn.X += (a.Y - b.Y) * (a.Z + b.Z); fn.Y += (a.Z - b.Z) * (a.X + b.X); fn.Z += (a.X - b.X) * (a.Y + b.Y); }
                for (int k = 0; k < n; k++) nrm[map[fv[k]]] += fn;
            }

            // Faceting: the Facet slider blends smooth (averaged @1) -> faceted. For an n-gon mesh (Dev2PQ strips)
            // the faceted end is a PER-FACE normal (@3) over SPLIT verts, so it's per-QUAD — the triangulation's
            // diagonal ("false edge") never shows at any Facet, while real quad edges stay crisp. A triangle mesh
            // keeps shared verts + the shader's screen-space (per-triangle) facet normal, and keeps the LIC field.
            bool hasNgon = false;
            for (int f = 0; f < P.Faces.Count; f++)
            { if (!P.Faces[f].IsUnused) { var ff = P.Faces.GetFaceVertices(f); if (ff != null && ff.Length > 3) { hasNgon = true; break; } } }

            float[] posF; float[] nrmF; float[] faceF; uint[] indices;
            if (hasNgon)
            {
                // SPLIT per face: each vert carries the smooth averaged normal (@1) AND the face's Newell normal (@3).
                var pl = new List<float>(); var nl = new List<float>(); var fl = new List<float>(); var il = new List<uint>();
                var cp = new Vector3[8];
                for (int f = 0; f < P.Faces.Count; f++)
                {
                    if (P.Faces[f].IsUnused) continue;
                    int[] fv = P.Faces.GetFaceVertices(f);
                    if (fv == null || fv.Length < 3) continue;
                    int n = fv.Length; if (cp.Length < n) cp = new Vector3[n];
                    bool ok = true;
                    for (int k = 0; k < n; k++) { int mi = map[fv[k]]; if (mi < 0) { ok = false; break; } cp[k] = pos[mi]; }
                    if (!ok) continue;
                    Vector3 fn = Vector3.Zero;   // per-face Newell normal (the facet end)
                    for (int k = 0; k < n; k++) { var a = cp[k]; var b = cp[(k + 1) % n]; fn.X += (a.Y - b.Y) * (a.Z + b.Z); fn.Y += (a.Z - b.Z) * (a.X + b.X); fn.Z += (a.X - b.X) * (a.Y + b.Y); }
                    fn = fn.LengthSquared > 1e-20f ? Vector3.Normalize(fn) : Vector3.UnitZ;
                    int baseV = pl.Count / 3;
                    for (int k = 0; k < n; k++)
                    {
                        int mi = map[fv[k]]; Vector3 sn = nrm[mi].LengthSquared > 1e-20f ? Vector3.Normalize(nrm[mi]) : Vector3.UnitZ;
                        pl.Add(cp[k].X); pl.Add(cp[k].Y); pl.Add(cp[k].Z);
                        nl.Add(sn.X); nl.Add(sn.Y); nl.Add(sn.Z);
                        fl.Add(fn.X); fl.Add(fn.Y); fl.Add(fn.Z);
                    }
                    for (int k = 1; k + 1 < n; k++) { il.Add((uint)baseV); il.Add((uint)(baseV + k)); il.Add((uint)(baseV + k + 1)); }
                }
                posF = pl.ToArray(); nrmF = nl.ToArray(); faceF = fl.ToArray(); indices = il.ToArray();
                _vMap = null; _usedCount = posF.Length / 3; _useFaceN = true;   // split verts -> LIC field N/A (SetField no-ops)
            }
            else
            {
                // All-triangle mesh: shared verts + averaged normals; faceting via the shader's screen-space normal.
                var tris = new List<(int a, int b, int c)>(P.Faces.Count);
                for (int f = 0; f < P.Faces.Count; f++)
                {
                    if (P.Faces[f].IsUnused) continue;
                    int[] fv = P.Faces.GetFaceVertices(f);
                    if (fv == null || fv.Length < 3) continue;
                    int a = map[fv[0]]; if (a < 0) continue;
                    for (int k = 1; k + 1 < fv.Length; k++) { int b = map[fv[k]], c = map[fv[k + 1]]; if (b < 0 || c < 0) continue; tris.Add((a, b, c)); }
                }
                posF = new float[used * 3]; nrmF = new float[used * 3];
                for (int i = 0; i < used; i++)
                {
                    posF[i * 3] = pos[i].X; posF[i * 3 + 1] = pos[i].Y; posF[i * 3 + 2] = pos[i].Z;
                    Vector3 nn = nrm[i].LengthSquared > 1e-20f ? Vector3.Normalize(nrm[i]) : Vector3.UnitZ;
                    nrmF[i * 3] = nn.X; nrmF[i * 3 + 1] = nn.Y; nrmF[i * 3 + 2] = nn.Z;
                }
                indices = new uint[tris.Count * 3];
                for (int t = 0; t < tris.Count; t++) { indices[t * 3] = (uint)tris[t].a; indices[t * 3 + 1] = (uint)tris[t].b; indices[t * 3 + 2] = (uint)tris[t].c; }
                faceF = null; _vMap = map; _usedCount = used; _useFaceN = false;
            }
            _indexCount = indices.Length;

            if (_vao == 0) { _vao = GL.GenVertexArray(); _vboPos = GL.GenBuffer(); _vboNrm = GL.GenBuffer(); _vboFaceN = GL.GenBuffer(); _ebo = GL.GenBuffer(); }
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboPos);
            GL.BufferData(BufferTarget.ArrayBuffer, posF.Length * sizeof(float), posF, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vboNrm);
            GL.BufferData(BufferTarget.ArrayBuffer, nrmF.Length * sizeof(float), nrmF, BufferUsageHint.DynamicDraw);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(1);
            if (_useFaceN)   // per-face normal @3 (n-gon/PQ): the faceted end of the Facet blend
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, _vboFaceN);
                GL.BufferData(BufferTarget.ArrayBuffer, faceF.Length * sizeof(float), faceF, BufferUsageHint.DynamicDraw);
                GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
                GL.EnableVertexAttribArray(3);
            }
            else GL.DisableVertexAttribArray(3);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.DynamicDraw);
            // The field VBO (@2) is sized to the PREVIOUS vertex count; disable it until SetField
            // re-uploads for the new mesh, so a stale buffer can't be read out of range. A disabled
            // attribute reads constant 0 -> the LIC sees a zero field -> no grain (just the matcap).
            GL.DisableVertexAttribArray(2);
            GL.BindVertexArray(0);
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

        // Upload proposed-crease line segments (GL_LINES position pairs), flat-coloured like the seams.
        public void SetCreases(float[] posF)
        {
            EnsureProgram();
            _creaseCount = posF == null ? 0 : posF.Length / 3;
            if (_creaseCount == 0) return;
            if (_creaseVao == 0) { _creaseVao = GL.GenVertexArray(); _creaseVbo = GL.GenBuffer(); }
            GL.BindVertexArray(_creaseVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _creaseVbo);
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
            GL.Uniform1(_uUseFaceN, _useFaceN ? 1 : 0);
            GL.Uniform1(_uHasMatcap, _hasMatcap ? 1 : 0);
            if (_hasMatcap)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _tex);
                GL.Uniform1(_uMatcap, 0);
            }
            // Shine default-shading pair (neutral unit 2, environment unit 3 — clear of matcap@0/noise@1).
            GL.Uniform1(_uShine, Shine);
            GL.Uniform1(_uUseMatcap, UseMatcap ? 1 : 0);
            GL.Uniform1(_uHasShade, (_hasNeutral && _hasEnv) ? 1 : 0);
            GL.ActiveTexture(TextureUnit.Texture2); GL.BindTexture(TextureTarget.Texture2D, _texNeutral); GL.Uniform1(_uNeutral, 2);
            GL.ActiveTexture(TextureUnit.Texture3); GL.BindTexture(TextureTarget.Texture2D, _texEnv); GL.Uniform1(_uEnv, 3);
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
            // Filled pass: when ShowPieces is on (host sets it after Propose), the piece-tinted view REPLACES
            // the matcap fill; otherwise the matcap/Shine mesh draws. Either way the line overlays below draw
            // on top at true depth.
            bool pieces = ShowPieces && _pieceVertCount > 0;
            if (pieces)
            {
                DrawPieces(view, proj);                   // separate program; pushes its fill back if creases are shown
                GL.UseProgram(_prog);                     // re-bind the line-overlay program + its mvp for the overlays
                GL.UniformMatrix4(_uMvp, false, ref mvp);
            }
            else
            {
                GL.BindVertexArray(_vao);
                // When edges OR on-surface creases are shown, push the fill back a touch (polygon offset) so a
                // line overlay drawn at true depth wins on the NEAR surface while the mesh occludes the FAR.
                GL.Uniform1(_uEdge, 0);
                bool offsetFill = ShowEdges || (ShowCreases && _creaseCount > 0);
                if (offsetFill) { GL.Enable(EnableCap.PolygonOffsetFill); GL.PolygonOffset(1f, 1f); }
                GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
                if (offsetFill) GL.Disable(EnableCap.PolygonOffsetFill);   // line overlays below draw at true depth
                if (ShowEdges)
                {
                    GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                    GL.Uniform1(_uEdge, 1);
                    GL.Uniform3(_uEdgeColor, EdgeColor.X, EdgeColor.Y, EdgeColor.Z);
                    GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
                    GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                }
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
            // Proposed creases (orange): drawn at true depth (depth test ON) so the mesh occludes the far
            // side — 3D, like the B-spline seam preview. The fill was pushed back above, so near-surface
            // segments still win the depth test instead of z-fighting the faces they lie on.
            if (ShowCreases && _creaseCount > 0)
            {
                GL.BindVertexArray(_creaseVao);
                GL.Uniform1(_uEdge, 1);
                GL.Uniform3(_uEdgeColor, CreaseColor.X, CreaseColor.Y, CreaseColor.Z);
                GL.LineWidth(2f);
                GL.DrawArrays(PrimitiveType.Lines, 0, _creaseCount);
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
            if (_seamVao != 0) { GL.DeleteVertexArray(_seamVao); GL.DeleteBuffer(_seamVbo); }
            if (_seamCtrlVao != 0) { GL.DeleteVertexArray(_seamCtrlVao); GL.DeleteBuffer(_seamCtrlVbo); }
            if (_creaseVao != 0) { GL.DeleteVertexArray(_creaseVao); GL.DeleteBuffer(_creaseVbo); }
            if (_pieceVao != 0) { GL.DeleteVertexArray(_pieceVao); GL.DeleteBuffer(_pieceVboPos); GL.DeleteBuffer(_pieceVboNrm); GL.DeleteBuffer(_pieceVboCol); GL.DeleteBuffer(_pieceVboDist); GL.DeleteBuffer(_pieceVboEdge); }
            if (_pieceProg != 0) GL.DeleteProgram(_pieceProg);
            if (_tex != 0) GL.DeleteTexture(_tex);
            if (_texNeutral != 0) GL.DeleteTexture(_texNeutral);
            if (_texEnv != 0) GL.DeleteTexture(_texEnv);
            if (_prog != 0) GL.DeleteProgram(_prog);
        }
    }
}
