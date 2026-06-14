# NOTICE / Attribution

This project builds on the work of others. Attribution and known licensing
constraints are recorded here. (This project itself has **no license chosen
yet** — see the README's "License — OPEN TODO" section.)

## Developability energy & gradient

The covariance ("hinge") developability energy and its analytic gradient
implemented in `src/DevelopabilityEnergy.cs` are **re-derived from the paper**:

> Oded Stein, Eitan Grinspun, and Keenan Crane.
> *"Developability of Triangle Meshes."*
> ACM Transactions on Graphics (TOG) 37, 4 (2018).

The reference implementation released by the authors alongside that paper is
licensed under the **GNU General Public License, version 2 (GPL-v2)**. Although
the code here is an independent re-derivation, this lineage is noted so the
licensing implications can be evaluated before this project is given a license
or distributed.

## Plankton

`lib/Plankton.dll` and `lib/PlanktonGh.dll` are stock, unmodified builds of
upstream **Plankton** (0.4.3):

> Plankton — a flexible and efficient library for handling polygon meshes
> (half-edge data structure) for .NET.
> Copyright © Daniel Piker and Will Pearson.
> https://github.com/meshmash/Plankton

Plankton is licensed under the **GNU Lesser General Public License (LGPL)**.

## Origin

The `SheetBender` component was originally developed inside a fork of Dan
Piker's **MeshMachine** Grasshopper component. This repository extracts only
the new, self-contained developability-flow code into a clean history; **no
MeshMachine / MeshMachine-derived `remesher` code is included here.**
