# NOTICE / Attribution

This project is released under the **GNU General Public License, version 2
(GPL-v2)**. See `LICENSE` for the full text. This file records third-party
attribution and the licensing posture of upstream work this project builds on.

## Developability energy & gradient

The covariance ("hinge") developability energy and its analytic gradient
implemented in `src/DevelopabilityEnergy.cs` are **re-derived from the paper**:

> Oded Stein, Eitan Grinspun, and Keenan Crane.
> *"Developability of Triangle Meshes."*
> ACM Transactions on Graphics (TOG) 37, 4 (2018).

The reference implementation released by the authors alongside that paper is
licensed under the **GNU General Public License, version 2 (GPL-v2)**. This
project adopts the same license to keep the lineage clean and to honor the
upstream copyleft.

The extended energy variants implemented here (App B.2 combinatorial /
consolidation, App B.4 max-covariance, App B.5.1 branching) are likewise
re-derived from the paper's appendices and described in `PAPER_FORMULAS.md`.

## L1 dihedral sparsity (`deCraze`)

The L1 dihedral sparsity penalty is **not** from Stein/Grinspun/Crane. It is a
mesh adaptation of L1 sparse regularization following:

> Robert Tibshirani.
> *"Regression Shrinkage and Selection via the Lasso."*
> Journal of the Royal Statistical Society, Series B 58, 1 (1996).
>
> Lei He and Scott Schaefer.
> *"Mesh denoising via L0 minimization."*
> ACM Transactions on Graphics (TOG) 32, 4 (2013).

## DetMix energy blend

The `DetMix` parameter blends between the paper's `lambda_min(M)` energy (mix=0)
and `det(M_tangent) = lambda_min * lambda_max` (mix=1). The det form is a
common smooth surrogate but is not the paper's recipe; it is included here as a
practical workaround for the well-known non-smoothness of `lambda_min` at
degenerate vertices (symmetric quads, icosahedral corners) and is documented in
`README.md` and `HANDOFF.md`.

## Plankton

`lib/PlanktonGh.dll` is a stock, unmodified upstream **Plankton** (0.4.3) build.
`lib/Plankton.dll` is the unmodified upstream Plankton 0.4.3 **source recompiled to
`netstandard2.0`** (zero source changes) so one assembly serves both the net48
Grasshopper plugin and a net8 headless GUI. The assembly version is unchanged
(`0.4.3.0`), so `PlanktonGh.dll` binds to it exactly as before, and net48 loads
netstandard2.0 transparently.

> Plankton — a flexible and efficient library for handling polygon meshes
> (half-edge data structure) for .NET.
> Copyright © Daniel Piker and Will Pearson.
> https://github.com/meshmash/Plankton

To reproduce `lib/Plankton.dll`: clone upstream at tag `v0.4.3` and build
`src/Plankton` with this SDK-style project (the only added file — no code touched):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <AssemblyName>Plankton</AssemblyName>
    <RootNamespace>Plankton</RootNamespace>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

Plankton is licensed under the **GNU Lesser General Public License (LGPL)**.
LGPL is compatible with this project's GPL-v2 license: GPL-v2 code may link
against an LGPL library, and the LGPL library remains LGPL and independently
swappable.

## Origin

This component was originally prototyped inside a fork of Dan Piker's
**MeshMachine** Grasshopper component (https://github.com/Dan-Piker/MeshMachine),
used as scaffolding while the developability-flow code was authored alongside.
This repository extracts **only the new, author-written code**.

MeshMachine itself has **no LICENSE file** as of this writing, which under
default copyright means "all rights reserved." Accordingly, **no MeshMachine /
MeshMachine-derived code (no remesher, no target-length pipeline) is included
in this repository.** The `remesher`-class code from the original fork lived in
the legacy `CreaseMachine-old` project and was deliberately left behind.
