using System;
using System.Collections.Generic;

namespace AsteroidsSim.Math;

/// <summary>
/// Stable sorting for the deterministic simulation core.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <see cref="Array.Sort{T}(T[])"/> is introsort: quicksort with a
/// heapsort fallback. It is <i>unstable</i>, so elements that compare equal come out in an order
/// that depends on the input permutation and on the pivot choices. Ties happen constantly in this
/// simulation — bonds of equal length, cells at equal distance, contacts at equal depth — and under
/// lockstep an unstable tie-break is a desync waiting for the two machines to have reached the same
/// state by different routes. <c>Array.Sort</c> is therefore banned (see BannedSymbols.txt) and
/// this is its replacement.</para>
///
/// <para>Bottom-up merge sort: stable by construction, O(n log n) worst case with no pivot
/// pathology, and it uses only comparisons and index arithmetic, so it is deterministic on every
/// platform. The scratch buffer is supplied by the caller and reused, because the sim allocates
/// nothing in the steady state.</para>
/// </remarks>
public static class SimSort
{
    /// <summary>
    /// Sorts <paramref name="keys"/>[0..count) ascending, stably, carrying <paramref name="items"/>
    /// along. <paramref name="scratchKeys"/> and <paramref name="scratchItems"/> must each hold at
    /// least <paramref name="count"/> elements and are used as working space.
    /// </summary>
    public static void Stable(
        int[] keys, int[] items, int count,
        int[] scratchKeys, int[] scratchItems)
    {
        if (keys is null) throw new ArgumentNullException(nameof(keys));
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (scratchKeys is null) throw new ArgumentNullException(nameof(scratchKeys));
        if (scratchItems is null) throw new ArgumentNullException(nameof(scratchItems));
        if (count < 0 || count > keys.Length || count > items.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count > scratchKeys.Length || count > scratchItems.Length)
            throw new ArgumentException("Scratch buffers are too small.", nameof(scratchKeys));
        if (count < 2) return;

        // Bottom-up merge. Source and destination swap each pass; an odd number of passes ends in
        // the scratch buffer, so the result is copied back only in that case.
        int[] srcK = keys, srcV = items, dstK = scratchKeys, dstV = scratchItems;

        for (int width = 1; width < count; width <<= 1)
        {
            for (int lo = 0; lo < count; lo += width << 1)
            {
                int mid = lo + width; if (mid > count) mid = count;
                int hi = mid + width; if (hi > count) hi = count;

                int i = lo, j = mid, k = lo;
                while (i < mid && j < hi)
                {
                    // "<=" is what makes this stable: on a tie the left run wins.
                    if (srcK[i] <= srcK[j]) { dstK[k] = srcK[i]; dstV[k] = srcV[i]; i++; }
                    else { dstK[k] = srcK[j]; dstV[k] = srcV[j]; j++; }
                    k++;
                }
                while (i < mid) { dstK[k] = srcK[i]; dstV[k] = srcV[i]; i++; k++; }
                while (j < hi) { dstK[k] = srcK[j]; dstV[k] = srcV[j]; j++; k++; }
            }

            (srcK, dstK) = (dstK, srcK);
            (srcV, dstV) = (dstV, srcV);
        }

        if (!ReferenceEquals(srcK, keys))
        {
            Array.Copy(srcK, keys, count);
            Array.Copy(srcV, items, count);
        }
    }

    /// <summary>
    /// Stable ascending sort of <paramref name="keys"/>[0..count) alone, without a payload.
    /// </summary>
    public static void Stable(int[] keys, int count, int[] scratchKeys)
    {
        if (keys is null) throw new ArgumentNullException(nameof(keys));
        if (scratchKeys is null) throw new ArgumentNullException(nameof(scratchKeys));
        if (count < 0 || count > keys.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count > scratchKeys.Length)
            throw new ArgumentException("Scratch buffer is too small.", nameof(scratchKeys));
        if (count < 2) return;

        int[] src = keys, dst = scratchKeys;

        for (int width = 1; width < count; width <<= 1)
        {
            for (int lo = 0; lo < count; lo += width << 1)
            {
                int mid = lo + width; if (mid > count) mid = count;
                int hi = mid + width; if (hi > count) hi = count;

                int i = lo, j = mid, k = lo;
                while (i < mid && j < hi)
                    dst[k++] = src[i] <= src[j] ? src[i++] : src[j++];
                while (i < mid) dst[k++] = src[i++];
                while (j < hi) dst[k++] = src[j++];
            }
            (src, dst) = (dst, src);
        }

        if (!ReferenceEquals(src, keys)) Array.Copy(src, keys, count);
    }

    /// <summary>
    /// Stable ascending sort of a <see cref="List{T}"/> of indices by an integer key supplied as a
    /// parallel array. Build-time convenience only — not for the tick.
    /// </summary>
    public static void StableByKey(List<int> indices, int[] keyOf)
    {
        if (indices is null) throw new ArgumentNullException(nameof(indices));
        if (keyOf is null) throw new ArgumentNullException(nameof(keyOf));
        int n = indices.Count;
        if (n < 2) return;

        var keys = new int[n];
        var items = new int[n];
        for (int i = 0; i < n; i++) { items[i] = indices[i]; keys[i] = keyOf[indices[i]]; }
        Stable(keys, items, n, new int[n], new int[n]);
        for (int i = 0; i < n; i++) indices[i] = items[i];
    }
}
