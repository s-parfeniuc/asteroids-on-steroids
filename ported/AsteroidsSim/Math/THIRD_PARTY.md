# Third-party attribution — AsteroidsSim/Math

## FDLIBM / FreeBSD msun

`SimMath.cs` ports the polynomial kernels, argument-reduction constants and special-case handling for
`Sin`, `Cos`, `Tan`, `Atan`, `Atan2`, `Exp`, `Log` and `Pow` from **FDLIBM**, via the FreeBSD **msun**
library. This is the same lineage that musl libc, OpenLibm and Rust's `libm` crate derive from — chosen
deliberately, because OpenLibm is used by Julia specifically for its bit-identical results across Apple,
Windows and Linux.

Original notice:

```
Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.

Developed at SunSoft, a Sun Microsystems, Inc. business.
Permission to use, copy, modify, and distribute this software is freely granted,
provided that this notice is preserved.
```

FreeBSD msun is distributed under the 2-clause BSD licence.

### What was changed

- Translated from C to C#; unions replaced by `BitConverter.{Double,Single}To{Int64,Int32}Bits` and back.
- Single-precision entry points evaluate the double-precision kernels and narrow on return. The extra
  ~29 bits of headroom absorb polynomial error, so a simpler kernel set suffices than a full `float` libm
  would need.
- `Exp` uses a degree-10 Taylor series on the reduced range rather than FDLIBM's rational form. The next
  term is below 1.2e-12 relative — far inside double's headroom for a `float` result — and the series
  uses only `+` and `*`, which keeps the determinism argument trivial.
- Argument reduction for the trigonometric functions always runs the second Cody–Waite stage instead of
  branching on whether it is needed. The correction is zero when unnecessary, and an unconditional path
  is easier to reason about for determinism.
- The `__rem_pio2_large` slow path for |x| ≥ 2^20 is **not** ported. See the accuracy note in
  `SimMath`'s remarks: results above that threshold remain bit-identical across platforms (which is the
  contract) but lose accuracy. No angle a game produces comes close.

## PCG32

`DetRng.cs` implements **PCG32** (Melissa O'Neill, 2014, <https://www.pcg-random.org/>), released under
Apache-2.0 / MIT. The algorithm is reimplemented from the published specification rather than copied.
Seeding uses **SplitMix64** (Vigna, public domain) to decorrelate substreams.
