namespace AsteroidsEngine.Engine.Core;

// ---------------------------------------------------------------------------
// Delegate types for World.ForEach — allow ref access to component data
// directly inside the lambda without copying.
//
// Usage:
//   world.ForEach((Entity e, ref Transform t, ref Velocity v) =>
//   {
//       t.Position += v.Linear * (float)dt;
//   });
// ---------------------------------------------------------------------------

public delegate void EcsAction<T>(Entity e, ref T c)
    where T : struct;

public delegate void EcsAction<T1, T2>(Entity e, ref T1 c1, ref T2 c2)
    where T1 : struct
    where T2 : struct;

public delegate void EcsAction<T1, T2, T3>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3)
    where T1 : struct
    where T2 : struct
    where T3 : struct;

public delegate void EcsAction<T1, T2, T3, T4>(Entity e, ref T1 c1, ref T2 c2, ref T3 c3, ref T4 c4)
    where T1 : struct
    where T2 : struct
    where T3 : struct
    where T4 : struct;
