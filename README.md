# CreaseMachine - Human authored ReadMe, agentic code.

This GH component builds surfaces based on **Stein, Grinspun & Crane**, 
*"Developability of Triangle Meshes"* (ACM TOG 37(4), 2018), with all of the
papers 'subgradients' (forces) which are listed in sections B.2 / B.4 / B.5.1. 
"Decraze" is my (our?) own force. All four of these seem to conflict with the main
developablize force and create jitter at high step sizes.

Subdividng in the middle of the run helps add resolution. Sometimes
or mid-surface facets accumulate which remind me of ceramic crazing.
I've been unable to resolve them so far.

This is multi-core parallel, and has a tendency to eat all processing power.
Disabling "Running" will freeze all calculations.

## The `CreaseMachine` component

Category: **Mesh → CreaseMachine**.

Many of the force names may still be esoteric, only relevant to me as I'm trying
to study and  find a meaningful combination that yields origami or sheet-metal like
constructions. The demo.gh provides sliders for each.

I've been using very low values for each subgradient (0 - 0.1).
I typically use 0 for the subgradients except sometimes "DeCrazing" which
is meant to help bust up thin facets that accumulate on twisty surfaces.
the "DetMix" slider may do a better job of this, but I'm still trying
to figure that out. AI explains it as such (Sorry, can't put into my own words)

- **DetMix** smoothly trades the paper's `λ_min(M)`, non-smooth at degenerate
  vertices and `λ_min·λ_max`, whose gradient combines both
  tangent-plane eigenvectors and is basis-invariant

## Building

Requires the .NET SDK and Rhino 8, or probably Rhino WIP installed at the default location
(`C:\Program Files\Rhino 8`). 

```sh
dotnet build src/CreaseMachine.csproj -c Release
```

yields

```
src/bin/Release/net48/CreaseMachine.gha
```

To install, copy that `.gha` into your Grasshopper Libraries folder
(`%APPDATA%\Grasshopper\Libraries`). It's probably not blocked, but worth a check anyway for first build.

## Tests / bench

A Rhino-free console bench was developed to help AI development. This is nearly a standalone app, but wat
originally only so we could test gradients in a 'headless' mode (rhino being the head)

```sh
dotnet build test/GradCheck.csproj -c Release
test/bin/Release/net48/GradCheck.exe
```

## Vendored dependencies

`lib/Plankton.dll` and `lib/PlanktonGh.dll` are stock, unmodified upstream
[Plankton](https://github.com/meshmash/Plankton) (0.4.3) by Daniel Piker, 
Will Pearson, and David Stasiuk. We may expect these are C:/Repo/meshmash/Plankton
You'll definitely nee the DLL and GHA for the Grasshopper node.

## License

This project is released under the **GNU General Public License, version 2
(GPL-v2)**. See `LICENSE` for the full text, and `NOTICE.md` for the upstream
attributions and how the license decision was reached.

Commercial use is permitted under the GPL-v2 terms: you may distribute and
sell builds of this `.gha`, provided you also make the source available and
preserve the license on any derivative work.
