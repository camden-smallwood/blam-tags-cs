# blam-tags (C#)

A C# / .NET 10 port of [blam-tags](https://github.com/camden-smallwood/blam-tags) —
an implementation of the Halo tag file format: a byte-exact roundtrip-capable
library plus a CLI for inspecting and editing tags.

No ManagedBlam, no engine required. The parser reads each tag's embedded layout
chunk and interprets the bytes directly.

This is a from-scratch port of the Rust
[blam-tags](https://github.com/camden-smallwood/blam-tags) workspace, built
phase-by-phase against a **byte-exact roundtrip gate** and a **differential
oracle**: every phase is verified by sweeping a corpus of real tags, requiring
read → write to reproduce the original bytes exactly, and diffing the C# output
against the original Rust binary.

## Projects

| Project | Role |
|---|---|
| `src/BlamTags/` | The library. Reads, writes, navigates, and edits tag files. Plus group-specific extractors: `bitmap` → TIFF / DDS, `model_animation_graph` → JMA-family, `render_model`/`collision_model`/`physics_model` → JMS, `scenario_structure_bsp` → ASS. |
| `src/BlamTags.Shell/` | Command-line front-end + interactive REPL. Subcommands for header metadata, directory listing / search / dependency walking, field-tree inspection, get / set / flag / block edits, options enumeration, schema and value diffing, integrity checks, replay-script export, raw `tag_data` field dump, and the four group-specific extractors (bitmap → TIFF/DDS, `.model` → JMS or ASS via `extract-geometry`'s content-based dispatch, `.scenario` → ASS per BSP, animation → JMA-family). |

## Status

- **Byte-exact roundtrip validated across 204,521 tags** spanning the Halo 3,
  Halo Reach, and Halo 4 MCC corpora — read → write reproduces the original
  bytes with **zero differences**. A small allowlist covers tags the Rust
  oracle also rejects as genuinely malformed (truncated / corrupt-trailing-chunk).

- **Layout versions 1 – 4** all read/write and are exercised in the sweep.

- **Read path surfaces malformed input as typed exceptions** rather than
  crashing — `TagReadException` / `TagSchemaException` carry the wire-format
  failure (bad chunk signature/version, size/count mismatch, …).

- **Pageable resources walk like any other container.** Exploded resources
  expose a `TagResource.AsStruct()` view onto the header struct; the path
  resolver, REPL `cd`, and `inspect` all step through them transparently.

- **Bitmap → TIFF / DDS extraction**, verified against the Rust oracle:
  **DDS is byte-identical** (150 / 150 sampled across H3 / Reach / H4) and
  **TIFF is decoded-pixel-exact** across all three games — full per-format
  decode (uncompressed + BC1/2/3/4/5 + Halo's `dxn_mono_alpha`), DX cube-cross
  and vertical-strip array layouts, with bcdec-exact rounding. **TIFF is the
  default** (Tool.exe-importable RGBA8, SnowyMouse libtiff field profile);
  `--format dds` preserves original bytes for inspection. See
  `src/BlamTags/Bitmap/` and the `extract-bitmap` verb.

- **`.model` → JMS export**, byte-identical to the oracle across **450 models**
  (150 each H3 / Reach / H4). Polymorphic over
  `render_model`/`collision_model`/`physics_model` — per-purpose source files in
  the H3EK source-tree layout (`render/`, `collision/`, `physics/`). Covers
  skeleton bind-pose quaternion chaining, triangle-strip decode,
  compression-bounds dequant, instance-placement geometry, the collision BSP
  edge-ring walk, and Havok primitives + ragdoll/hinge constraints. The
  `render_model` skeleton provides world-space placement for `coll`/`phmo`. See
  `src/BlamTags/Geometry/Jms.cs` and the `extract-geometry` verb.

- **`.model` / `.scenario` / `.scenario_structure_bsp` → ASS export**,
  byte-identical to the oracle across **~400 ASS files** (scenarios-with-lighting,
  sbsps, and instance-geometry render→ASS) over all three games.
  **Render-side auto-dispatch**: ASS when the render_model carries `instance
  mesh index >= 0` + populated `instance placements[]` (the brute, decorators,
  level objects); JMS otherwise; `--force jms`/`ass` overrides. Scenario export
  emits one ASS per `structure_bsps[]` entry, pairing each BSP with its
  `scenario_structure_lighting_info`: cluster meshes, per-IGD-def meshes +
  per-placement instances, real `BM_LIGHTING_*` material metadata, cluster
  portals, weather polyhedra (convex hull from bounding planes), the structure
  collision BSP, sbsp markers, `environment_objects[]` xrefs, and
  SPOT/DIRECT/OMNI/AMBIENT generic lights. See `src/BlamTags/Geometry/Ass.cs`.

- **`model_animation_graph` → JMA-family text export**, byte-identical to the
  oracle across **~3,200 animations** sampled over the three games (Masterchief:
  1103 / 1103). Decodes all 12 codec slots — UncompressedStatic /
  UncompressedAnimated / EightByteQuantizedRotationOnly / Byte+WordKeyframe
  (forward & reverse) / BlendScreen / Curve / RevisedCurve — composes static +
  animated tracks against the skeleton via the node-flag bitarrays (rest pose
  from render_model nodes + the jmad's `additional node data` cache), and emits
  `.JMM/.JMA/.JMT/.JMZ/.JMO/.JMR/.JMW` re-importable by Halo content tooling.
  Movement deltas fold into the root bone (Foundry-style local→world `dx/dy`
  rotation by accumulated yaw); per-type frame layout matches Tool's importer.
  A handful of isolated 1-ULP differences are confined to movement-folded
  root-bone quaternions — an irreducible `f32 sin/cos` rounding boundary between
  Rust's libm and .NET (verified `< 1e-6`). See `src/BlamTags/Animation/` and
  the `extract-animation` / `list-animations` verbs.

### Not yet ported (backlog)

Tracked against the Rust project, these remain to be ported: **Halo 4
monolithic tag-cache reads** (the `--cache` flag + `list-cache`), **X360
render-geometry hydration**, the **typed group walkers** (render_method,
scenario, structure_bsp, light, decorator_set, …), `extract-import-info`, and
`--json` output on the inspect/get/list family.

## Build

The .NET 10 SDK is at `/usr/local/share/dotnet`. If `dotnet` isn't on your
`PATH` in a given shell:

```sh
export PATH="/usr/local/share/dotnet:$PATH"
dotnet build       # builds the library + the CLI (BlamTags.Shell)
dotnet test        # runs the unit suite (corpus gates skip unless configured)
```

## Use the CLI

The shell takes a `--game <GAME>` flag (alias `-g`) to scope schema lookups and
group-name resolution to `definitions/<GAME>/`. `<GAME>` is a directory name
under `definitions/` (`halo3_mcc`, `haloreach_mcc`, `halo4_mcc`). The shipped
binary resolves `definitions/` relative to the executable, so a precompiled
build is standalone.

```sh
dotnet run --project src/BlamTags.Shell -- --game halo3_mcc header path/to/masterchief.biped
dotnet run --project src/BlamTags.Shell -- --game halo3_mcc get    path/to/masterchief.biped "jump velocity"
dotnet run --project src/BlamTags.Shell -- --game halo3_mcc set    path/to/masterchief.biped "jump velocity" 3.14
```

Run with no arguments (or `repl`) for the interactive REPL.

## Use the library

```csharp
using BlamTags;

var tag = TagFile.Read("path/to/masterchief.biped");

// Read a field by slash-separated path. Value is the per-variant
// TagFieldData (or null for container / padding fields).
var jump = tag.Root.FieldPath("jump velocity")!;
Console.WriteLine($"{jump.Name} ({jump.TypeName}): {jump.Value}");

// Toggle a flag and write the edit back to a new file.
tag.Root.FieldPath("unit/flags")!.Flag("has_hull")!.Toggle();

tag.Write("path/to/edited.biped");
```

## Running the gates

The corpus sweep and oracle comparisons are **skipped** unless their inputs are
configured, so the suite runs cleanly on a fresh checkout.

| Env var | Meaning | Default |
|---|---|---|
| `BLAM_TAGS_CORPUS` | Directory tree of real tag files to sweep | _(unset → skip)_ |
| `BLAM_TAGS_ORACLE` | Path to the Rust `blam-tag-shell` binary | `../blam-tags/target/release/blam-tag-shell` |
| `BLAM_TAGS_SAMPLE` | Cap the sweep to N tags (for quick runs) | _(unlimited)_ |

```sh
# Build the Rust ground-truth oracle once (from a checkout of the repo above):
( cd ../blam-tags && cargo build --release --workspace )

# Run the full byte-exact roundtrip + extractor parity gates against a corpus:
BLAM_TAGS_CORPUS=/path/to/tags dotnet test
```

## Layout

```
BlamTags.slnx
src/BlamTags/            — the library
│   Io/ Layout/ Schema/ Data/ Fields/ Api/   — generic tag tree
│   Math/                                     — Halo real_* value types
│   TagFile.cs, GroupTag.cs, TagPaths.cs      — top-level surface
│   Bitmap/                                   — bitmap → TIFF / DDS
│   Geometry/                                 — JMS + ASS (shared helpers)
│   Animation/                                — jmad → JMA-family
src/BlamTags.Shell/      — CLI + REPL
tests/BlamTags.Tests/    — corpus sweeps + differential oracle harness
definitions/             — schema JSON (reused from the Rust project)
```
