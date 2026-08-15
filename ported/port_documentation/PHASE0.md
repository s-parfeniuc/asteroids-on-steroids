# Phase 0 — Foundations, Determinism, and Profiling

**Duration:** ~2 weeks · **Prerequisite for:** everything
**Parent:** [PORT_PLAN.md](port_documentation/PORT_PLAN.md)

Phase 0 writes no game code. It builds the skeleton, settles the decisions that are expensive to
change later, and produces **numbers** for the two questions the rest of the plan depends on.

## Exit criteria — current status

| # | Criterion | Status |
|---|---|---|
| 1 | `AsteroidsSim` compiles with **zero Godot references**, enforced by the build | ✅ **done** — MSBuild guard + banned-API analyzer, both verified to fire |
| 2 | **`SimMath` bit-identical on Linux x64 / Windows x64 / macOS arm64** | ✅ **done** — all three agree on `7513035a81fe26663489e2bc2be77c41`. The gate caught a real bug on its first run (below) |
| 3 | **Spike A has numbers** — rendering throughput, marshalling isolated | ✅ **done** — see below |
| 4 | **Spike B has numbers** — physics path decided | ✅ **done — decision: port our own solver** |
| 5 | **Independence gate** passes | ✅ **done** — verified locally by clean extraction |

**Environment for all measurements:** Intel i5-13420H (12 threads), Ubuntu 24.04, .NET 8.0.129,
Godot 4.7.1-stable mono, headless. Godot Rapier 2D v0.35.2 (`godot-rapier-2d-single`).

---

## Results so far

### `SimMath` — accuracy vs the platform libm

Every function is within **1 ulp** across its tested domain. Measured on Linux x64, .NET 8.0.129:

| Function | Worst error | Domain sampled |
|---|---|---|
| `Sin` | 1 ulp | ±8π, and ±100,000 |
| `Cos` | 1 ulp | ±8π |
| `Tan` | 1 ulp | ±1.5 |
| `Atan` | 1 ulp | ±50 |
| `Atan2` | 1 ulp | 600×600 grid over all four quadrants |
| `Exp` | 1 ulp | ±80 |
| `Log` | 1 ulp | (0, 200] and every binade from 2⁻¹²⁰ to 2¹²⁰ |
| `Pow` | 1 ulp | 500×500 grid, x ∈ (0,20], y ∈ [−8,8] |
| `Pow(x, 1.6)` | 1 ulp | the `FractureKernel.StepFront` hot path, x ∈ [0,1] |

**46 tests pass**, covering accuracy, the full C99 special-case tables for `Pow` and `Atan2`, signed
zeros, subnormals, NaN propagation, and `DetRng` substream independence.

### Fingerprint stability

`MathFingerprint` hashes ~251k inputs per unary function, ~382k pairs per binary function, and 64 seeds
× 512 RNG draws. Current value (post-fix, see below):

```
7513035a81fe26663489e2bc2be77c41
```

**Identical under every JIT configuration tested** — default, `TieredCompilation=0`, `TieredPGO=0`,
`ReadyToRun=0`, `QuickJitForLoops=1`. That rules out JIT tiering as a divergence source, which was one of
the named risks.

### The determinism gate caught a real bug on its first CI run

This is the headline result of Phase 0. The first three-platform run came back:

```
linux-x64      ab816f84937c3e1b5473aca6fcc4dfcc
windows-x64    ab816f84937c3e1b5473aca6fcc4dfcc
macos-arm64    4e8450bd87b8596f89d7ea951e68acdb   ← diverged
```

The per-function breakdown localised it immediately: **`Cos`, `Tan` and `Sqrt` differed; `Sin`, `Atan`,
`Exp`, `Log`, `Log2`, `Atan2`, `Pow` and `DetRng` did not.**

`Sqrt` was the tell. It is a passthrough to `MathF.Sqrt`, which IEEE 754 requires to be *correctly
rounded* — it cannot differ for any real input. So the divergence had to involve **NaN**, which pointed
at the input vector rather than the algorithm. Two distinct causes, one benign and one serious:

**Cause 1 (serious) — an unspecified `double`→`int` cast in `ReducePio2`.**

```csharp
double fn = x * InvPio2;
int n = (int)(fn >= 0 ? fn + 0.5 : fn - 0.5);   // undefined when fn exceeds int range
```

C# leaves out-of-range float→int conversion unspecified, and the architectures genuinely disagree:
x86-64's `cvttsd2si` yields `int.MinValue`; **ARM64's `fcvtzs` saturates to `int.MaxValue`.** So `n & 3`
selected quadrant **0** on x86 and quadrant **3** on ARM — a different kernel entirely. The fingerprint's
input vector reaches `float.MaxValue` and every binade to 2¹²⁷, which trips it. (`Sin` happened not to
diverge because its quadrant-0 and quadrant-3 branches coincided on those garbage inputs; `Cos` and `Tan`
did not. A neat illustration of why a broad input vector matters — a narrower sweep would have missed it.)

**Fix:** guard at |x| ≥ 2²⁶ and return a fixed result. This is principled rather than arbitrary: above
2²⁶ a float's ulp exceeds 2π, so consecutive representable inputs differ by more than a full period and
the argument carries *no phase information at all*. Every answer is equally defensible; what matters is
that it is the same answer everywhere. Regression-tested in `Trig_HugeArguments_AreDefined`.

**Cause 2 (benign) — NaN payload bits in the hash.** IEEE 754 leaves the sign and payload of a NaN
unspecified, and x86-64 and ARM64 produce different bit patterns for e.g. `sqrt(-1)`. Hashing them raw
reported a divergence with no semantic content. The fingerprint now canonicalises every NaN to one value
— a NaN where another platform produced a *number* still differs, which is the case that matters.

**Post-fix fingerprint: `7513035a81fe26663489e2bc2be77c41`** — confirmed identical on Linux x64,
Windows x64 and macOS arm64.

**A third, purely-CI bug surfaced on the confirming run:** all three platforms reported the same hash, yet
the gate still failed. The Windows runner's .NET writes **CRLF**, so a byte-identical hash differs from
the Linux/macOS files by a trailing `\r` — `sort -u` counted two unique lines while `printf` rendered
both identically, which made the failure look impossible. Fixed with `tr -d '\r'` before comparison.
Worth remembering the shape of it: when a comparison fails but the displayed values match, suspect
invisible bytes.

The lesson worth keeping: this bug would have shipped, and would have surfaced as an unreproducible
mid-match desync between a Mac player and everyone else, months from now. It cost one CI run to find.

### One real bug the unit tests caught

`Sin(-0f)` returned `+0f`. In the FDLIBM kernel `x + v*(S1 + …)` with `S1` negative turns `-0.0` into
`+0.0`, and `-0.0 + 0.0` is `+0.0`. Fixed with FDLIBM's tiny-argument early-out (`|x| < 2⁻²⁷ → return x`).
This is exactly the class of defect the special-case tests exist to catch: a wrong zero sign propagates
through the simulation and desyncs a match, where a last-ulp difference in `Sin` would not.

### Spike A — batched rendering throughput ✅

Run headless so software rasterization (this machine falls back to llvmpipe — no DRI3) doesn't compete
for the CPU being measured. Times are per frame, averaged over 180 frames after 45 warmup.

| cells | verts | tris | submit path | build | submit | multimesh 20k | **total** |
|---:|---:|---:|---|---:|---:|---:|---:|
| 5,000 | 30,000 | 20,000 | ReadOnlySpan | 0.428 | 0.396 | 2.063 | **2.886 ms** |
| 5,000 | 30,000 | 20,000 | managed array | 0.431 | 0.401 | 2.068 | **2.900 ms** |
| 20,000 | 120,000 | 80,000 | ReadOnlySpan | 1.173 | 1.188 | 1.369 | **3.731 ms** |
| 20,000 | 120,000 | 80,000 | managed array | 1.249 | 1.250 | 1.414 | **3.913 ms** |
| 50,000 | 300,000 | 200,000 | ReadOnlySpan | 2.417 | 5.608 | 1.303 | **9.328 ms** |
| 50,000 | 300,000 | 200,000 | managed array | 2.468 | 5.720 | 1.316 | **9.504 ms** |

**Verdict: the render path carries the target comfortably.** 20,000 cells costs **3.7 ms** of a 16.7 ms
frame, leaving ~13 ms for the simulation. Four findings:

1. **Caching body-local geometry is worth ~10×.** The first version of this spike ran a `SinCos` per
   *vertex* — 120k trig calls a frame — and measured **11.9 ms** for the build stage at 20k cells.
   Rewriting it the way `WorldRenderer` actually works (cell polygons cached in body-local space, one
   rotation per *body*, ~30 cells each) took the same stage to **1.17 ms**. This confirms the design in
   PORT_PLAN.md §5 lever 3, and it is the single largest render-side win available.
2. **The `ReadOnlySpan` overload makes no measurable difference** (within ~4%, i.e. noise). The cost is
   Godot's internal command-buffer copy, not managed→native marshalling. Useful negative result: there is
   no need to design around the boundary, and no reason to prefer one overload.
3. **Submit cost scales super-linearly past ~120k verts** — 0.40 ms at 30k, 1.19 ms at 120k, 5.61 ms at
   300k. From 120k→300k verts (2.5×) the cost grows 4.7×, most likely cache pressure. 20k cells is
   comfortable; 50k is where this starts to hurt and would want per-body culling.
4. **MultiMesh at 20,000 instances costs ~1.3 ms**, confirming the T2 debris tier is cheap to render.

**Not measured:** GPU-side cost. This machine has Intel UHD graphics but Vulkan and OpenGL both fall back
to llvmpipe (no DRI3 under X11), so no meaningful GPU number is obtainable here. 200k triangles is trivial
for any real GPU, but that remains unverified. Re-run on a machine with working acceleration before
relying on it.

**Still open from the original spike brief:** the 8-bit vertex colour banding check needs a *visual*
inspection, which headless cannot provide. The gradient mode (`G`) is implemented and ready.

### Spike B — physics ✅ **decision: port our own solver**

Identical scenario for all three contenders, generated by `AsteroidsSim.Spikes.PhysicsScenario`:
2,000 bodies (600 compound × 30 convex shapes + 1,400 single-shape) = **19,400 shapes**, seeded
velocities, no gravity, 600 ticks at 60 Hz, headless.

| coverage | backend | median tick | p99 tick | wall clock |
|---|---|---:|---:|---:|
| **56%** (arena ×1.0, stress) | Godot DEFAULT | 90.809 ms | 137.748 ms | 48.5 s |
| 56% | Rapier2D v0.35.2 | 27.947 ms | 73.297 ms | 12.5 s |
| 56% | **ours — existing `src/Engine`, unoptimised** | **14.842 ms** | 107.374 ms | 10.4 s |
| **14%** (arena ×2.0, representative) | Godot DEFAULT | 34.401 ms | 43.711 ms | 18.4 s |
| 14% | Rapier2D v0.35.2 | 16.649 ms | 81.054 ms | 9.9 s |
| 14% | **ours — existing `src/Engine`, unoptimised** | **4.733 ms** | 31.998 ms | 3.5 s |

Budget at 60 Hz is **16.667 ms** for the whole tick; the physics share should be **~8 ms** to leave room
for fracture, AI, waves and a 16-player server.

**Result: our existing solver is the only contender that meets the target — by a wide margin, and before
any optimisation.**

- At representative density it runs at **4.73 ms**, comfortably inside the 8 ms physics share.
  That is **3.5× faster than Rapier** and **7.3× faster than Godot DEFAULT**.
- At the stress density it is still **1.9× faster than Rapier** and **6.1× faster than Godot DEFAULT**.
- Rapier is nonetheless **2–3.3× faster than Godot's built-in physics**, which makes Godot DEFAULT
  unusable for this workload independent of everything else.

And the decisive point against Rapier even setting speed aside: the measured build is the standard
`-single` flavour. **The cross-platform-deterministic flavour explicitly disables SIMD and parallel
solving**, so it is strictly slower than the numbers above — while already being 3.5× behind ours.

Criteria 2 (snapshot round-trip fidelity) and 3 (cross-platform state hash) were therefore **not
measured**; the decision rule short-circuits on criterion 1.

**→ Port our own solver in Phase 2.** This was the plan's anticipated outcome, and it is now supported by
a measurement rather than an inference. It also recovers snapshot, restore and rollback as properties we
own rather than vendor promises.

#### Honest caveats on these numbers

- **This is not a like-for-like comparison of physics *quality*.** The three backends do different amounts
  of work per tick — our `CollisionSystem` runs 6 solver iterations, Godot's default is 16, Rapier has its
  own scheme — and contact stability, penetration recovery and joint behaviour were not compared at all.
  What the table establishes is that our solver is firmly in the viable performance range, which is the
  question the decision needed answering. It does **not** establish that it produces better contacts.
- **Our p99 is the worst of the three at representative density** (32.0 ms vs Rapier's 81.1 ms at stress
  but 43.7 ms for DEFAULT). The spikes come from the per-frame `SpatialGrid` rebuild
  (`Dictionary<long, List<Entity>>`, ~18k dictionary operations per frame) and allocation churn — exactly
  the two things Phase 2's persistent broad phase and SoA layout target. Expect median to improve and p99
  to improve *more*.
- **The scenario has no tiering, no sleeping tuning and no active region.** All 2,000 bodies are fully
  simulated every tick, so absolute figures are pessimistic for every backend. The relative comparison is
  sound — all three ran the identical scenario.
- Coverage figures are computed from actual cell-polygon area (`PhysicsScenario.ArealCoverage`), not from
  enclosing discs. An earlier draft of this document quoted 82% for arena ×1.0 from a hand estimate; the
  measured value is **56%**.
- Single machine, single run per configuration. Adequate for a 2–7× decision, not for tuning.
- Rapier segfaulted once during project import, and Godot emits `Unreferenced static string` errors on
  shutdown in every headless run. Neither affected the measurements; noted as stability signals.

### Godot API findings (from the scaffolding work)

- **`CanvasItemAddTriangleArray` has a `ReadOnlySpan` overload** in Godot 4.7 — so geometry buffers can be
  built once and submitted with no managed array copy at the boundary. This answers Spike A's
  "can the packed arrays be reused" question better than expected. Spike A measures both paths (`S` toggles).
  Note the span overload declares **no default parameters**; all nine arguments must be explicit.
- `MultimeshSetBuffer` also has a span overload.
- `MultimeshAllocateData` parameters are **positional** in the C# binding — `(rid, instances,
  transformFormat, useColors, useCustomData)`.
- `Godot.NET.Sdk` **restores from NuGet without the editor installed**, so the Godot-side C# can be
  compile-checked in CI on machines that have no Godot. Useful for the build matrix.

---

## Decisions recorded

| Decision | Outcome | Evidence |
|---|---|---|
| Godot / .NET / GodotSharp versions | **Godot 4.7.1-stable mono · net8.0 · Godot.NET.Sdk 4.7.1** | pinned in `global.json` + csprojs |
| Physics path | **Port our own solver** (Phase 2) | Spike B: ours 4.73 ms vs Rapier 16.6 ms vs Godot 34.4 ms at representative density |
| Packed-array reuse strategy | **Reuse buffers; overload choice is irrelevant** | Spike A finding 2 |
| Geometry build strategy | **Cache body-local, one transform per body** | Spike A finding 1 (~10×) |
| Fixed tick rate | **60 Hz confirmed** | set in `project.godot`; Spike B budgeted against it |
| Vertex colour precision | **open** — needs a visual check | Spike A, headless can't answer |

## Remaining work

**Phase 0 is complete — all five exit criteria met.** Carried into later phases:

1. **Visual check for 8-bit vertex colour banding** — run `Spikes/SpikeRender.tscn` windowed with `G`
   on a machine that has working GPU acceleration.
3. **Optional: re-run Spike A on real hardware** to get a GPU-side number. The CPU-side conclusion does
   not depend on it.

Deliberately **not** done:
- Rapier snapshot round-trip and cross-platform hash — the decision rule short-circuited on criterion 1,
  and our own solver beat it by 3.5× regardless.

The `tools/SpikePhysicsBaseline` project holds the sanctioned temporary reference to `src/Engine`
(PORT_PLAN.md §11). It is whitelisted in `scripts/check_independence.py` and is deleted at the Phase 5
gate along with the rest of the `LEGACY_DIFF` scaffold.

---

## Deliverable 1 — Repo and solution skeleton

```
ported/
├── AsteroidsOnSteroids.sln
├── Directory.Build.props           # shared: net8.0, nullable, warnings-as-errors, LangVersion
├── .editorconfig
├── .gitignore                      # + .godot/, .mono/, exports/, *.translation
├── global.json                     # pin the .NET SDK
├── BannedSymbols.txt               # see Deliverable 2
│
├── AsteroidsSim/                   ★ class library, NO Godot
│   ├── AsteroidsSim.csproj
│   └── Math/SimMath.cs, DetRng.cs, Vec2.cs
│
├── AsteroidsSim.Tests/             xUnit
│   └── Math/SimMathTests.cs
│
├── AsteroidsGame/                  Godot project
│   ├── project.godot
│   ├── AsteroidsGame.csproj        → ProjectReference AsteroidsSim
│   └── Sim/SimRoot.cs              draws one triangle; nothing else
│
├── tools/
│   ├── MathFingerprint/            headless, prints a hash of SimMath over a fixed input set
│   ├── SpikeRender/                Spike A
│   └── SpikePhysics/               Spike B
│
└── .github/workflows/ci.yml
```

**Versions to pin, exactly, in `global.json` and the csprojs:**
- **Godot 4.7.x .NET build.** Record the exact patch version; GDExtension-free but the C# ABI still moves.
- **.NET SDK 8.0.x**, `TargetFramework=net8.0`. Godot 4.4+ standardised on net8.0 and requires SDK 8.0+.
- `GodotSharp` / `Godot.NET.Sdk` **4.7.0** — matched to the editor version.

**`Directory.Build.props` baseline:** `Nullable=enable`, `TreatWarningsAsErrors=true`,
`InvariantGlobalization=true`, `LangVersion=latest`, deterministic build flags
(`Deterministic=true`, `ContinuousIntegrationBuild=true` in CI).

---

## Deliverable 2 — Boundary enforcement

Three mechanisms, all enforced by the build so nobody has to remember them.

### 2.1 No Godot in the sim

The primary enforcement is simply not referencing `GodotSharp` — any Godot type becomes a compile error.
Back it with an MSBuild guard in `AsteroidsSim.csproj` so nobody can quietly add it:

```xml
<Target Name="AssertNoGodot" AfterTargets="ResolveReferences">
  <Error Condition="'%(ReferencePath.Filename)' == 'GodotSharp'"
         Text="AsteroidsSim must not reference Godot. See PORT_PLAN.md §2.2." />
</Target>
```

### 2.2 Banned APIs

Package: `Microsoft.CodeAnalysis.BannedApiAnalyzers`, applied to `AsteroidsSim` only (the Godot layer may
use whatever it likes — it isn't in the deterministic path).

`BannedSymbols.txt`:
```
T:System.Random;Not reproducible across runtimes — use DetRng
M:System.MathF.Sin(System.Single);Use SimMath.Sin
M:System.MathF.Cos(System.Single);Use SimMath.Cos
M:System.MathF.Pow(System.Single,System.Single);Use SimMath.Pow
M:System.MathF.Atan2(System.Single,System.Single);Use SimMath.Atan2
M:System.MathF.Exp(System.Single);Use SimMath.Exp
M:System.MathF.Tan(System.Single);Use SimMath.Tan
M:System.MathF.Log(System.Single);Use SimMath.Log
M:System.MathF.Asin(System.Single);Use SimMath.Asin
M:System.MathF.Acos(System.Single);Use SimMath.Acos
T:System.DateTime;No wall clock in the sim
T:System.Diagnostics.Stopwatch;No wall clock in the sim
T:System.Threading.Tasks.Parallel;No threading in the deterministic path
```

Deliberately **not** banned:
- `MathF.Sqrt` / `Abs` / `Min` / `Max` / `Floor` / `Ceiling` / `Round` — IEEE-exact or exactly specified.
  `MathF.Round` uses banker's rounding, which is deterministic.
- `Dictionary` — safe to *build*, dangerous to *iterate*. An analyzer can't cleanly distinguish the two,
  so this stays a code-review rule backed by the fingerprint test, which catches it empirically.

Add `Array.Sort` → a stable `SimSort` helper. `Array.Sort` is introsort and unstable.

### 2.3 Independence gate

A CI job that copies `ported/` (only) into a clean directory, restores, builds, tests and runs the
fingerprint tool. Runs on every PR from day one — not at the end.

---

## Deliverable 3 — `SimMath`

The five functions the sim actually needs. Everything else in `Destruction/` and `Collision/` is
`Sqrt`, `Abs`, `Min`, `Max`, `Floor`, `Round` — all exactly specified and safe.

| Function | Call sites in the current sim | Notes |
|---|---|---|
| `Sin` | 11 | Voronoi seed angles, procedural generation |
| `Cos` | 11 | same |
| `Pow` | 2 | **hot** — the alignment weight in `FractureKernel.StepFront` |
| `Atan2` | 2 | aiming, angles |
| `Exp` | 2 | drag in `PhysicsSystem` |

### Why we're writing it

`MathF.Sin` calls the platform C runtime — glibc on Linux, ucrtbase on Windows, Apple's libm on macOS.
Those differ in the last ulp. That divergence is what desyncs a lockstep match.

There is **no usable managed float libm in the ecosystem**: `Determon` operates on `decimal` (orders of
magnitude too slow for a hot loop). So we port.

### Approach

Port the `float` implementations from **OpenLibm** (`sinf`, `cosf`, `powf`, `atan2f`, `expf`) — the same
FDLIBM/msun lineage that musl and Rust's `libm` crate use, and the library Julia relies on specifically
*because* it gives bit-identical results across Apple, Windows and Linux.

These are self-contained and use only:
- basic `float`/`double` arithmetic (`+ − × ÷`), which **is** bit-exact on every IEEE-754 platform
- bit manipulation, via `BitConverter.SingleToInt32Bits` / `Int32BitsToSingle`

Two .NET properties make this safe: the JIT does **not** contract `a*b+c` into FMA (only explicit
`Math.FusedMultiplyAdd` does), and .NET Core has no x87 excess precision on any supported target.

Licensing: OpenLibm is MIT/BSD-family (FreeBSD msun derived); musl is MIT. Both are fine to port with
attribution. Record it in `AsteroidsSim/Math/THIRD_PARTY.md`.

Estimated ~600–800 lines total.

### Tests

1. **Accuracy** — within 1 ulp of `MathF.*` across a wide sampled domain. Not equality; ulp bounds.
2. **Edge cases** — ±0, ±∞, NaN, subnormals, `Pow` special cases (there are many; the FDLIBM source
   documents them), very large/small arguments, `Atan2` quadrants.
3. **Bit-identity across platforms** — see Deliverable 4.
4. **No NaN leakage** — asserted at the sim boundary later, but the unit tests establish the contract now.

Also in this deliverable: **`DetRng`** — a PCG32 with named substreams (`Tessellation`, `Waves`,
`Clusters`, `Ai`, `Fx`), replacing the single shared `Random` in `GameContext` where "adding a particle
effect silently shifts wave rolls."

---

## Deliverable 4 — CI and the determinism harness

`tools/MathFingerprint` evaluates all five functions over a fixed, checked-in input vector (~1M values
including boundaries, subnormals and specials), hashes every result bit-pattern, prints one 128-bit hash.

```yaml
jobs:
  build:        [ubuntu-latest, windows-latest, macos-14]   # macos-14 = arm64
  fingerprint:  run MathFingerprint on each, upload hash
  compare:      fail if the three hashes differ
  independence: copy ported/ to a clean dir → restore, build, test
```

This job exists from week one and stays mandatory forever. It is the only thing that will catch a stray
`MathF.Sin` slipping into the sim two years from now.

---

## Spike A — Rendering throughput · 2 days

**Question:** does batched `RenderingServer` submission carry the target cell counts, and what does the
C#→Godot marshalling actually cost?

**Build:** `tools/SpikeRender` — a Godot scene with one `SimRoot`-shaped node that, each frame, generates
N cells' worth of geometry from plain C# arrays and submits it.

**Measure, separately:**
1. **Geometry build** (pure C#: fan-triangulating cells into flat arrays)
2. **Marshalling** (C# arrays → `PackedVector2Array` / `PackedColorArray` / `PackedInt32Array`)
3. **Submission** (`canvas_item_add_triangle_array`)
4. **MultiMesh** (`multimesh_set_buffer` with a `PackedFloat32Array`) at 20k instances

At cell counts of **5k / 20k / 50k** (≈6 verts each, so up to ~300k verts).

**Also answer:**
- Can the packed arrays be **reused across frames** (resize + overwrite) rather than reallocated? This is
  the difference between a copy and a churn.
- Do **8-bit vertex colours** show banding on `CellColorizer`'s Laplacian-smoothed gradients? Godot 4 forces
  8-bit per channel in 2D; Godot 3 allowed float.
- Does the `count` parameter of `canvas_item_add_triangle_array` still get ignored (a known issue)? If so,
  size arrays exactly rather than over-allocating and slicing.

**Bar:** the whole render path comfortably inside a few ms at 20k cells, leaving the frame to the sim.

---

## Spike B — Physics · 3 days

**Question, stated as a decision rule:** *can we avoid writing a solver?*

**Adopt `godot-rapier-physics` (deterministic build) if and only if all three hold:**
1. It meets the tick budget at target body counts.
2. `Restore(Snapshot(s))` round-trips **bit-exactly** — required for Phase 8a rollback, which §4.1 says is
   likely mandatory at 16 players.
3. Its state hash matches across Linux, Windows and macOS.

Otherwise we port our own solver in Phase 2. Adopting it would save ~3–4 weeks.

**Scenario** (identical for both contenders): 2,000 mixed bodies in a bounded arena — 600 compound bodies
of ~30 convex shapes each + 1,400 single-shape bodies — seeded velocities, no gravity, 600 ticks at 60 Hz.

**Contender 1:** `godot-rapier-physics`, the *"Slower Version with Cross Platform Deterministic"* build,
registered via `PhysicsServer2DManager.register_server`.

**Contender 2 (baseline):** the **existing** `src/Engine` solver, referenced via a temporary project
reference behind the `LEGACY_DIFF` symbol — the same scaffold Phase 1 uses for differential testing, so
set it up now.

> **Read the baseline honestly.** The current solver rebuilds `SpatialGrid` from scratch every frame
> (`Dictionary<long, List<Entity>>`, ~18k dictionary ops/frame at 2k bodies) and `CompoundShape` is
> ~201 heap objects per 100-cell asteroid. It is a **floor**, not a fair comparison — our optimised version
> would be substantially faster. The decision rule above is therefore about whether Rapier clears the bar,
> not about which contender wins.

**Record:** median and p99 tick time, allocations per tick, snapshot round-trip fidelity, cross-platform
hash match.

---

## Decisions to record at the end of Phase 0

Write each into `PORT_PLAN.md` §2 with its measurement:

| Decision | Source |
|---|---|
| Exact Godot / .NET / GodotSharp versions | pinned in Deliverable 1 |
| Physics path — Rapier vs our own | Spike B decision rule |
| Vertex colour precision — 8-bit acceptable? | Spike A |
| Packed-array reuse strategy | Spike A |
| Fixed tick rate confirmed at **60 Hz** | §2.7, reconfirm against Spike B numbers |
| Namespace and assembly naming | convention, settle once |

---

## Sequencing

**Week 1**
| Days | Work |
|---|---|
| 1–2 | Repo skeleton, solution, `Directory.Build.props`, gitignore, Godot project, CI building green on three OSes |
| 3–5 | `SimMath`: port the five functions, accuracy + edge-case tests, `DetRng`, `MathFingerprint`, cross-platform compare job |

**Week 2**
| Days | Work |
|---|---|
| 1–2 | Spike A |
| 3–5 | Spike B (includes installing the Rapier plugin and wiring the `LEGACY_DIFF` reference) |

Boundary enforcement (Deliverable 2) lands alongside week 1; the independence gate is part of the CI work.

---

## Not in Phase 0

No fracture code. No physics implementation. No gameplay. No content pipeline. No rendering beyond what
Spike A needs. The differential test harness is *scaffolded* for Spike B but is a Phase 1 deliverable.

---

## Risks inside Phase 0

| Risk | Signal | Response |
|---|---|---|
| `Pow` is the hardest of the five to port correctly (many special cases, and it's in the hot loop) | edge-case tests fail | Budget it first, not last. FDLIBM's `powf` documents every case; port the table verbatim |
| Ported functions are slower than `MathF` | Spike A / a microbenchmark | Expected and acceptable — `Pow` runs ~1×/bond/pop. If it dominates, cache `AlignExponent` powers or use a lookup, but **never** at the cost of determinism |
| macOS arm64 CI runners cost money on private repos | billing | Fall back to Linux + Windows for the gate; add arm64 before any console/Mac target |
| Rapier plugin version doesn't match Godot 4.7 | Spike B blocked | Pin to whatever Godot version the plugin supports and record it; the pin already had to be exact |
| 8-bit vertex colour banding | Spike A visual check | Move gradient into a shader (per-cell uniform or a texture lookup) instead of per-vertex colour |
