# Paper Formulas

Faithful transcription of the math from the appendices of

> Oded Stein, Eitan Grinspun, Keenan Crane.
> **"Developability of Triangle Meshes."** ACM TOG 37(4), 2018.

Notation key

- `f_i`, `f_j`, `f_k` — positions of vertices `i`, `j`, `k` in `R^3`.
- `A_ijk` — area of triangle `(i, j, k)`. (The paper uses script 𝒜.)
- `N_ijk` — unit normal of triangle `(i, j, k)`.
- `θ_i^jk` — interior angle at vertex `i` in triangle `(i, j, k)`.
- `N_i` — area-weighted unit vertex normal at `i`: direction of `Σ_{ijk∈F} A_ijk N_ijk`.
- `φ_i^jk` — angle from `N_i` to `N_ijk`.
- `v_i^jk` — unit tangent vector at `N_i` toward `N_ijk` (the exp-map direction).
- `Ñ_i^jk := φ_i^jk · v_i^jk` — intrinsic image of `N_ijk` in the tangent plane at `i`.
- `St(i)` — star of vertex `i` (the triangles incident to `i`).
- `⟨·, ·⟩` — Euclidean inner product. `×` — cross product. `^T` — transpose.
- `u ×̂ v := (u × v) / |u × v|` — normalized cross product.
- `λ` — eigenvalue of the covariance matrix `A_i`. `x` — its eigenvector.

---

## Appendix A — Properties of Discrete Developable Triangulations

A vertex `i` is a **hinge** if `St(i)` is embedded and its triangles can be partitioned into two edge-connected flat regions; it is **flat** if the normals of all its triangles are parallel.

### Proposition A.1

If an interior vertex `i` is a non-flat hinge vertex, then it is contained in a pair of antiparallel edges `ia, ib ∈ St(i)`.

*Proof.* Let `N_1, N_2` be the normals of the two flat regions of `St(i)`; since these regions are edge-connected, there will be exactly two edges `ia, ib` that share both normals. Since `i` is not flat, the normals must be distinct (`N_1 ≠ N_2`); since `St(i)` is embedded, they must not be antiparallel (`N_1 ≠ -N_2`). Hence the cross products

```
N_1 × N_2 = -N_2 × N_1
```

yield nonzero vectors parallel to the two edges `ia, ib`. □

### Proposition A.2

Consider a discrete developable immersion `f` with no flat vertices. Then `f` is discrete ruled.

*Proof.* By Proposition A.1, any interior vertex `i` must have a pair of antiparallel edges `ia, ib`; let `N_1, N_2` be the distinct normals determining the edge directions. Since `St(a)` and `St(b)` each share a pair of triangles with normals `N_1, N_2`, they will each contain a pair of antiparallel edges along the same line (or a single edge in the case of boundary vertices). □

### Proposition A.3

Any valence-3 hinge vertex `i ∈ V` is necessarily flat.

*Proof.* Suppose `i` were not flat. Then by Proposition A.1 it would have a pair of antiparallel edges `va, vb`. But since `i` has valence 3, `va` and `vb` must be edges of the same triangle, i.e., `i`, `a`, and `b` are collinear. Hence `St(i)` is not a hinge, since it is not embedded. □

---

## Appendix B.1 — Derivatives of Basic Quantities

The energies depend only on triangle areas `A_ijk`, triangle normals `N_ijk`, and interior angles `θ_i^jk`, which have the following gradients with respect to vertex positions `f`:

```
∇_{f_i} A_ijk = ½ N_ijk × (f_k − f_j)                                          (7)
```

```
∇_{f_i} N_ijk = (1 / A_ijk) · ((f_k − f_j) × N_ijk) · N_ijk^T                  (8)
```

(Outer product of the column vector `(f_k − f_j) × N_ijk` with the row vector `N_ijk^T`.)

```
∇_{f_j} θ_i^jk =  N_ijk × (f_i − f_j) / |f_i − f_j|
∇_{f_k} θ_i^jk =  N_ijk × (f_k − f_i) / |f_k − f_i|                            (9)
∇_{f_i} θ_i^jk = -( ∇_{f_j} θ_i^jk + ∇_{f_k} θ_i^jk )
```

Since these quantities depend only on the positions of vertices `i`, `j`, and `k`, the gradients with respect to any other vertex are zero.

---

## Appendix B.2 — Combinatorial Energy

To evaluate the gradient of the combinatorial energy `E_i^P` associated with vertex `i`, first identify the partition `P` minimizing `π(P)` (Equation 1). The gradient of a single term in this sum with respect to the position `f_p` of any vertex `p ∈ V` can be expressed via

```
∇_{f_p} |N_{σ_1} − N_{σ_2}|^2 = 2 ⟨ N_{σ_1} − N_{σ_2}, ∇_{f_p} N_{σ_1} − ∇_{f_p} N_{σ_2} ⟩
```

where the normal gradient is given in Equation 8. The energy gradient is the sum over all such terms. In the case where there are two or more partitions of equal energy, the gradient of any of them will be a **subgradient** of the piecewise smooth energy `E^λ`, which is still suitable for the first-order descent strategy outlined in Section 4.3. To avoid branching (Section 4.1.4), the gradient of any maximal term provides a subgradient for Equation 5.

---

## Appendix B.3 — Covariance Energy

At any vertex `i ∈ V`, let `λ` be an eigenvalue of the matrix

```
A_i := Σ_{ijk ∈ F} θ_i^jk · N_ijk · N_ijk^T
```

with associated eigenvector `x`. Then the gradient of `λ` with respect to the position `f_p ∈ R^3` of any vertex `p ∈ V` is

```
∇_{f_p} λ = Σ_{ijk ∈ F}  (x^T N_ijk)^2 · ∇_{f_p} θ_i^jk
                       +  2 θ_i^jk · (x^T N_ijk) · (∇_{f_p} N_ijk)^T · x        (10)
```

— obtained by applying the chain rule and the identity `∇_A λ = x x^T`. Expressions for `∇_{f_p} N_ijk` and `∇_{f_p} θ_i^jk` are Equations 8 and 9.

---

## Appendix B.4 — Maximal Covariance

### Energy

To evaluate the energy given by Equation 6, let

```
φ(u) := max_{ijk ∈ F}  ⟨u, N_ijk⟩^2
```

This function is piecewise smooth over spherical Voronoi cells associated with the unit normals `N_ijk` and their antipodes `−N_ijk` (Figure 28, left). Its minimum is therefore found at a vertex of the spherical Voronoi diagram, which will be the spherical centroid of some triple of sites. Since `φ` achieves a minimum at a Voronoi vertex, minimizing `φ` over **all** triples necessarily yields the optimal value `λ_i^max`. From the perspective of performance and numerical stability, simply evaluating `φ` for all triples is more attractive than explicitly building the Voronoi diagram, especially since the number of distinct triples is typically very small.

To compute the spherical centroid of three unit vectors `a, b, c`, note that the geodesic circumcenter of a spherical triangle coincides with the unit normal of the plane containing the triangle's vertices (Figure 28, right). The location of the site is therefore just

```
w = (b − a) × (c − a) / |(b − a) × (c − a)|
```

To avoid a zero denominator, simply omit redundant sites.

### Subgradient

Since `φ` is a maximum over a collection of convex differentiable functions, the gradient of any maximizing term provides a subgradient that can be used for optimization (Section 4.3). In particular, let `v` be the unit vector minimizing `φ`, let `M` be the maximizing normal, and let `a, b, c ∈ F` be the triple of triangles whose normals define `v`. Then the subgradient `∇_{f_p} λ_i^max` with respect to the position `f_p` of a vertex `p ∈ V` can be expressed as

```
∇_{f_p} λ_i^max =
  2 ⟨v, M⟩ · (
      ⟨v, e_p × M⟩ / (2 A_M) · M
    + Σ_{σ ∈ {a,b,c}}  ⟨v, e_{σ|p} × N_σ⟩ · ⟨e_σ × v, M⟩ / (4 A_abc · A_σ) · N_σ
  )
```

where `N_σ` is the unit normal of triangle `σ ∈ F`, `A_M` and `A_p` are the areas of triangles with normals `M` and `p` (resp.), `A_abc` is the Euclidean area of a triangle with vertices `a, b, c`, `e_p ∈ R^3` is the edge vector opposite vertex `p` in the triangle with normal `M` (or zero if `p` is not contained in this triangle), and `e_{σ|p}` is the edge across from vertex `p` in triangle `σ`.

---

## Appendix B.5 — Intrinsic Width

### Energy

The energy `E^λ` (Section 4.1) quantifies the width of a polygon on the sphere via the covariance of extrinsic unit vectors `N_ijk ∈ R^3`, which can lead to artifacts (e.g., **SPIKES**) for large polygons. An intrinsic notion of width is obtained by instead expressing this polygon in terms of the exponential map at the center of the polygon (Figure 29).

If `N_i` is the area-weighted vertex normal at a vertex `i ∈ V` (the unit vector in the direction `Σ_{ijk ∈ F} A_ijk · N_ijk`) and `φ_i^jk` is the angle from `N_i` to some triangle normal `N_ijk` in `St(i)`, then the triangle normal itself can be expressed as

```
N_ijk = exp_{N_i}( φ_i^jk · v_i^jk )
```

for some unit tangent vector `v`, where `exp_p` denotes the exponential map at a point `p` on the 2-sphere `S^2` (Figure 29). More explicitly, this vector is obtained by projecting `N_ijk` onto the plane of `N_i` and normalizing:

```
ṽ_i^jk := N_ijk − ⟨N_ijk, N_i⟩ · N_i
v_i^jk := ṽ_i^jk / |ṽ_i^jk|
```

Letting `Ñ_i^jk := φ_i^jk · v_i^jk`, the width of the spherical polygon can then be quantified via the smallest eigenvalue of the 2 × 2 matrix

```
Ã_i := Σ_{ijk ∈ F} θ_i^jk · Ñ_i^jk · (Ñ_i^jk)^T
```

mirroring Equation 4.

### Gradient

Let `N_i` be the area-weighted normal at vertex `i ∈ V`, let

```
v_i^jk := N_i ×̂ N_ijk
μ_i    := v_i^jk ×̂ N_i
μ_f    := v_i^jk ×̂ N_ijk
```

where `u ×̂ v := u × v / |u × v|` denotes the normalized cross product. Then the gradient of `Ñ_i^jk` with respect to the position `f_p` of a vertex `p ∈ V` can be expressed as

```
∇_{f_p} Ñ_i^jk =
    ( μ_i μ_f^T + (φ_ijk / sin φ_ijk) · v_i^jk (v_i^jk)^T )      · ∇_{f_p} N_ijk
  − ( μ_i μ_f^T + φ_ijk · N_ijk · μ_i^T
                + (φ_ijk / tan φ_ijk) · v_i^jk (v_i^jk)^T )      · ∇_{f_p} N_i   (11)
```

The gradients for `N_i` and `N_ijk` can be expressed via the expressions from Appendix B.1; the gradient of the overall energy can then be expressed by substituting `Ñ_i^jk` for `N_ijk` in Equation 10.

---

### B.5.1 — Branching

#### Energy

In the intrinsic case, one can avoid the branching artifacts described in Section 4.1.4 by penalizing the minimum width of the convex hull of the `n` points `Ñ ∈ R^2`. This width can be computed via the method of rotating calipers in `O(n log n)` time, including construction of the convex hull. However, since `n` is always quite small (about six on average) a simpler implementation is to just minimize the energy

```
ψ := min_{|u|=1}  max_{ijk, ipq ∈ St(i)}  ⟨ Ñ_i^jk − Ñ_i^pq, u ⟩^2
```

by enumerating all distinct pairs of vectors `x_a := ±Ñ_i^jk`, `x_b := ±Ñ_i^pq`. The minimizing vector `u*_ab` for any such pair will be the vector pointing along the altitude of the triangle `(0, x_a, x_b)` (see inset), and one can easily show that the minimum width of the convex hull is then the value of `ψ` among all such vectors `u*_ab`.

The subgradient is found by simply taking the gradient of the term maximizing `ψ` — the only new expression is the gradient of the unit altitude `u*_ab`, given by

```
∇_{x_a} u*_ab = − (1 / |w|^3) · (N_i × w) · w^T
```

where `w := x_b − x_a` (and likewise for `x_b`).
