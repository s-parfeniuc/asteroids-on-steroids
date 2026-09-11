// THE ONE SANCTIONED EXEMPTION FROM THE THREADING BAN.
//
// BannedSymbols.txt forbids System.Threading.Thread across AsteroidsSim, and that ban stays: this
// file is the single place it is lifted, so a stray thread anywhere else still fails the build.
// The exemption is file-scoped rather than statement-scoped because the type appears in field
// declarations too, and narrowing it to those would only hide what this file is.
//
// The obligation that comes with it is stated in the class remarks below and enforced by the
// simulation fingerprint: any pass dispatched through here must produce the same bytes on one
// worker as on twelve, and the same bytes as running sequentially.
#pragma warning disable RS0030

using System;
using System.Threading;

namespace AsteroidsSim.Fracture;

/// <summary>
/// A fixed-partition worker pool: the only place in the simulation allowed to use threads.
/// </summary>
/// <remarks>
/// <para><b>The determinism contract.</b> Threads are banned in <c>AsteroidsSim</c> because
/// concurrency normally means the order work happens in depends on the scheduler, and float
/// addition is not associative, so a different order is a different answer and a different answer
/// is a desync. This class is allowed to exist because it never lets that happen:</para>
///
/// <list type="number">
/// <item><b>Chunks own disjoint bodies.</b> This is the one that is not negotiable, and it is a
/// correctness requirement before it is a determinism one: a bond writes both of the cells it
/// couples, so a partition by cell or by bond would hand the same cell to two chunks. Partitioning
/// by body works because a bond never crosses one — asserted by
/// <c>GuardrailTests.BondsNeverCrossBodies</c>, not assumed.</item>
/// <item><b>Cross-chunk reductions merge in chunk-index order</b>, on the calling thread. For the
/// integer counters this is belt and braces: integer addition is associative, so their result does
/// not depend on the partition at all. It matters for floats, where addition is commutative but
/// <i>not</i> associative — regrouping a sum changes it, so the number of chunks becomes part of
/// the answer.</item>
/// <item>Chunk boundaries are integer arithmetic on simulation state, so every machine computes the
/// same ones. Given the two rules above, <see cref="Chunks"/> is today a pure performance knob:
/// nothing that reaches the fingerprint is reduced across chunks, and 2 through 1024 all produce
/// identical bytes. It stops being free the moment a pass needs a float sum across chunks.</item>
/// </list>
///
/// <para>What follows from that is the useful part: running on one worker and running on twelve
/// produce <b>bit-identical</b> results, and so does running the same pass sequentially. The
/// simulation fingerprint therefore tests the decomposition directly — if a pass is parallelised
/// wrongly, the hash moves. That is a far stronger check than inspection, and it is why the passes
/// were first rewritten to iterate per body <i>sequentially</i> and verified against the fingerprint
/// before any thread touched them.</para>
///
/// <para>Workers spin briefly before blocking. A physics step dispatches a few dozen times per tick
/// and a blocking handoff costs more than the work in a small scene, so the spin is what keeps the
/// pool worth using; the fallback to a wait handle is what keeps it from burning a core between
/// ticks.</para>
/// </remarks>
public sealed class SimJobs : IDisposable
{
    public const int DefaultChunks = 32;

    /// <summary>
    /// How many pieces work is cut into, independent of how many cores or workers exist.
    /// </summary>
    /// <remarks>
    /// <para>A pure performance knob <b>as long as no dispatched pass reduces floats across
    /// chunks.</b> Today none does: every pass writes only to state belonging to its own bodies, and
    /// the only cross-chunk accumulators are integer counters, where addition is associative and
    /// order cannot matter. So chunk count changes the split without changing the answer, which the
    /// fingerprint confirms across 2..1024.</para>
    /// <para>The moment a pass needs a float sum across chunks, that stops being true — the sum has
    /// to be merged in chunk order, and the number of chunks then changes the rounding. At that
    /// point this becomes part of the content contract and has to be pinned.</para>
    /// </remarks>
    public readonly int Chunks;

    private readonly Thread[] _workers;
    private readonly SemaphoreSlim _work = new(0);
    private Action<int, int, int>? _body;
    private int _items;
    private int[]? _bounds;
    private int _nextChunk;
    private int _outstanding;
    private volatile bool _quit;

    public int WorkerCount => _workers.Length;

    /// <param name="workers">
    /// Worker threads, not counting the calling thread which also takes chunks. Zero runs
    /// everything inline, which is the reference path.
    /// </param>
    public SimJobs(int workers, int chunks = DefaultChunks)
    {
        if (workers < 0) workers = 0;
        Chunks = chunks < 1 ? 1 : chunks;
        _workers = new Thread[workers];
        for (int i = 0; i < workers; i++)
        {
            var t = new Thread(WorkerLoop, 512 * 1024)
            {
                IsBackground = true,
                Name = $"sim-worker-{i}",
            };
            _workers[i] = t;
            t.Start();
        }
    }

    /// <summary>A pool sized to the machine, leaving the calling thread a core to run on.</summary>
    public static SimJobs ForThisMachine(int chunks = DefaultChunks)
        => new(System.Math.Max(0, System.Environment.ProcessorCount - 1), chunks);

    /// <summary>
    /// Splits <paramref name="items"/> into <see cref="Chunks"/> ranges and runs
    /// <paramref name="body"/> over each as (chunkIndex, lo, hi).
    /// </summary>
    /// <remarks>
    /// Workers block on a semaphore between dispatches rather than spinning. The first version of
    /// this spun, on the theory that a physics step dispatches often enough to keep latency the
    /// thing that matters — and it made the frame 33% SLOWER: eleven threads spinning stole the
    /// cores the sequential phases were running on, so even the passes that were never parallelised
    /// slowed down. Blocking costs a microsecond or two per wakeup and gives those cores back.
    /// </remarks>
    public void For(int items, Action<int, int, int> body)
    {
        if (items <= 0) return;

        // Below a few hundred items the dispatch costs more than the work, and a small scene is
        // exactly where the frame is already comfortable.
        if (_workers.Length == 0 || items < 64)
        {
            for (int i = 0; i < Chunks; i++)
            {
                int lo = (int)((long)items * i / Chunks);
                int hi = (int)((long)items * (i + 1) / Chunks);
                if (hi > lo) body(i, lo, hi);
            }
            return;
        }

        // Publish the description, then the outstanding count, then open claiming — in that order.
        // A worker still looping from the previous dispatch must not be able to claim before the
        // description it will read is in place.
        _bounds = null;
        _items = items;
        Volatile.Write(ref _body, body);
        Volatile.Write(ref _outstanding, Chunks);
        Volatile.Write(ref _nextChunk, 0);
        _work.Release(_workers.Length);

        RunChunks();                       // the calling thread is a worker too

        AwaitChunks();
        _body = null;
    }

    /// <summary>
    /// Runs <paramref name="body"/> over ranges given explicitly, one per chunk, as
    /// <c>bounds[i]..bounds[i+1]</c>.
    /// </summary>
    /// <remarks>
    /// Splitting an array of bodies into equal COUNTS is not splitting it into equal work. After a
    /// few seconds of fracture a scene holds 140-cell asteroids next to single-cell rubble, so a
    /// chunk's cost varies by two orders of magnitude and the fattest one sets the critical path.
    /// The caller passes boundaries derived from a real weight — bonds, cells — and because those
    /// weights are a pure function of simulation state, every machine computes the same split.
    /// </remarks>
    public void For(int[] bounds, Action<int, int, int> body)
    {
        int items = bounds[Chunks] - bounds[0];
        if (items <= 0) return;

        if (_workers.Length == 0 || items < 64)
        {
            for (int i = 0; i < Chunks; i++)
                if (bounds[i + 1] > bounds[i]) body(i, bounds[i], bounds[i + 1]);
            return;
        }

        _bounds = bounds;
        _items = -1;                       // marks the explicit-bounds path
        Volatile.Write(ref _body, body);
        Volatile.Write(ref _outstanding, Chunks);
        Volatile.Write(ref _nextChunk, 0);
        _work.Release(_workers.Length);

        RunChunks();

        AwaitChunks();
        _body = null;
        _bounds = null;
    }

    /// <summary>
    /// Waits for the last chunk, without ever sleeping.
    /// </summary>
    /// <remarks>
    /// This used <c>SpinWait.SpinOnce</c>, which is the wrong tool here: it escalates to
    /// <c>Thread.Sleep(1)</c> after about twenty spins, and on Linux that parks the thread for a
    /// millisecond or more. The caller reaches this point having already run its own share, so it is
    /// waiting on a straggler that is microseconds from done — and instead it slept, eighteen times
    /// a tick. That one line was most of the reason parallelism looked like a loss: at two chunks
    /// the dispatched work measured 2.7x SLOWER than sequential, and the penalty faded as chunk
    /// counts rose only because more chunks left the caller less idle time in which to fall asleep.
    /// Pausing and then yielding costs a core for a few microseconds instead of parking for a
    /// millisecond.
    /// </remarks>
    private void AwaitChunks()
    {
        int spins = 0;
        while (Volatile.Read(ref _outstanding) > 0)
        {
            if (spins < 24) Thread.SpinWait(8 << System.Math.Min(spins++, 6));
            else Thread.Yield();
        }
    }

    /// <summary>
    /// Claims chunks until there are none left.
    /// </summary>
    /// <remarks>
    /// <b>The job description is read AFTER the claim, never before.</b> This is the fork-join
    /// state-reuse hazard and it is worth spelling out, because the first version cached the
    /// delegate in a local on entry and desynced roughly one run in five. The sequence: a worker
    /// finishes the last chunk of dispatch N and loops to claim another; the caller sees
    /// <c>_outstanding</c> reach zero and starts dispatch N+1, which resets the claim counter; the
    /// looping worker's claim now succeeds against the NEW counter and it runs a chunk of the new
    /// dispatch with the OLD delegate and the OLD bounds. Reading the description after the claim
    /// closes it: the caller publishes the description before resetting the counter, so any claim
    /// that succeeds is matched with the description it belongs to.
    /// </remarks>
    private void RunChunks()
    {
        while (true)
        {
            int i = Interlocked.Increment(ref _nextChunk) - 1;
            if (i >= Chunks) return;

            var body = Volatile.Read(ref _body);
            if (body == null) { Interlocked.Decrement(ref _outstanding); continue; }
            int[]? bounds = _bounds;

            int lo, hi;
            if (bounds != null) { lo = bounds[i]; hi = bounds[i + 1]; }
            else
            {
                int items = _items;
                lo = (int)((long)items * i / Chunks);
                hi = (int)((long)items * (i + 1) / Chunks);
            }
            if (hi > lo) body(i, lo, hi);
            Interlocked.Decrement(ref _outstanding);
        }
    }

    private void WorkerLoop()
    {
        while (!_quit)
        {
            _work.Wait();
            if (_quit) return;
            RunChunks();
        }
    }

    public void Dispose()
    {
        _quit = true;
        if (_workers.Length > 0) _work.Release(_workers.Length);
        foreach (var t in _workers) t.Join(200);
        _work.Dispose();
    }
}
