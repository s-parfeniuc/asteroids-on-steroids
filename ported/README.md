# Asteroids on Steroids

A bullet-heaven roguelike built on a physics-based destruction engine: bodies are graphs of convex
**cells** joined by **bonds**, and an impact deposits energy that floods the graph, breaking bonds and
vaporising cells. Every kill is a physics outcome, and cells are simultaneously the unit of damage and
the unit of loot.

**Godot 4.7 · C# / .NET 8 · deterministic simulation core**

> **Status: Phase 0.** Foundations and determinism infrastructure are in place. No gameplay yet.
> See [PORT_PLAN.md](PORT_PLAN.md) for the architecture and roadmap, [PHASE0.md](PHASE0.md) for the
> current phase.

---

## Layout

```
AsteroidsSim/         ★ the simulation. NO Godot reference — enforced by the build.
AsteroidsSim.Tests/     xUnit
AsteroidsGame/          Godot project — presentation, input, netcode shell
tools/MathFingerprint/  determinism gate: hashes SimMath across platforms
scripts/                CI helpers
```

The one architectural rule everything else follows: **`AsteroidsSim` is engine-free.** It runs headless
on a dedicated server, is testable without a window or a GPU, and can be snapshotted with a `memcpy` —
which is what makes lockstep netcode and rollback possible.

## Build and run

```bash
dotnet build -c Release          # everything
dotnet test  -c Release          # 46 tests
dotnet run --project tools/MathFingerprint          # determinism hash
dotnet run --project tools/MathFingerprint -- --verbose   # per-function breakdown
```

The Godot project needs the **Godot 4.7.x .NET editor**; open `AsteroidsGame/` in it.
`Main.tscn` is the game; `Spikes/*.tscn` are the Phase 0 measurements.

## Determinism

The simulation must produce **bit-identical results on every platform**, because lockstep netcode
desyncs on a last-ulp difference. That means:

- All transcendental math goes through `SimMath` — `MathF.Sin` and friends call the platform C runtime
  (glibc / ucrtbase / Apple libm), which differ in the last ulp. `SimMath` is ported from FDLIBM and
  uses only operations IEEE 754 specifies exactly.
- All randomness goes through `DetRng` — PCG32 with independent named substreams, so adding a particle
  effect can never shift a wave roll.
- No wall clock, no threading, no unstable sorts, no `Dictionary` iteration in simulation code.

These are enforced by a banned-API analyzer at build time and by a CI job that compares the fingerprint
hash across Linux, Windows and macOS. Full contract: [PORT_PLAN.md §6](PORT_PLAN.md).

## Contributing

Read [PORT_PLAN.md](PORT_PLAN.md) first — particularly §2 (decisions and why) and §6 (the determinism
contract). The rules there are not style preferences; breaking one produces a desync that is very
expensive to find later.

## Licence

See `AsteroidsSim/Math/THIRD_PARTY.md` for FDLIBM and PCG32 attribution.
