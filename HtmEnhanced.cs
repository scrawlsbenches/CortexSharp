// ============================================================================
// HIERARCHICAL TEMPORAL MEMORY — Enhanced C# Pseudocode
// ============================================================================
// A comprehensive, production-oriented HTM implementation covering:
//   - SIMD-optimized SDRs with noise tolerance and subsampling
//   - Full encoder suite (Scalar, RDSE, DateTime, Category, Geospatial, Composite)
//   - Synapse lifecycle management with pruning and segment limits
//   - Spatial Pooler with global AND local inhibition
//   - Enhanced Temporal Memory with segment cleanup
//   - Grid Cell Modules with path integration
//   - Displacement Cells for object-relative location
//   - Cortical Column with full dendritic computation
//   - Lateral voting and Thousand Brains consensus engine
//   - NetworkAPI region-based wiring and computation graphs
//   - Binary serialization / persistence
//   - Diagnostics, metrics, and health monitoring
//   - Multi-stream concurrent processing
//
// Based on Numenta's HTM/BAMI theory and Thousand Brains Framework.
// ============================================================================

#pragma warning disable CS8618 // Non-nullable field initialization
#pragma warning disable CS8600 // Null conversion

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace HierarchicalTemporalMemory.Enhanced;

// ============================================================================
// SECTION 1: Sparse Distributed Representation — SIMD-Optimized
// ============================================================================
// The SDR is the fundamental data type of HTM. This implementation uses
// a dual representation: a sorted sparse index array for iteration and a
// dense ulong[] bitvector for SIMD-accelerated set operations (AND, OR,
// XOR, POPCOUNT). The bitvector is lazily materialized and cached.
//
// Key enhancements over the basic version:
//   - Hardware-accelerated overlap via POPCNT on AND'd bitvectors
//   - Noise injection for robustness testing
//   - Random subsampling for dimensionality reduction
//   - Union/intersection/difference as first-class operations
//   - Semantic similarity via normalized overlap score
// ============================================================================

public sealed class SDR : IEquatable<SDR>
{
    // --- Core Storage ---
    private readonly int _size;                    // Total bit count (e.g., 2048)
    private int[] _activeBits;                     // Sorted indices of ON bits
    private ulong[]? _denseCache;                  // Lazy bitvector: ceil(size/64) words
    private int _version;                          // Invalidation counter for cache

    // --- Properties ---
    public int Size => _size;
    public int ActiveCount => _activeBits.Length;
    public float Sparsity => (float)ActiveCount / _size;
    public ReadOnlySpan<int> ActiveBits => _activeBits;

    // --- Construction ---

    public SDR(int size)
    {
        _size = size;
        _activeBits = Array.Empty<int>();
    }

    public SDR(int size, IEnumerable<int> activeBits)
    {
        _size = size;
        _activeBits = activeBits.Where(b => b >= 0 && b < size).Distinct().OrderBy(x => x).ToArray();
    }

    public SDR(int size, ReadOnlySpan<int> activeBits)
    {
        _size = size;
        var list = new List<int>(activeBits.Length);
        foreach (int b in activeBits)
            if (b >= 0 && b < size) list.Add(b);
        list.Sort();
        _activeBits = list.Distinct().ToArray();
    }

    /// Create from a dense boolean array
    public static SDR FromDense(ReadOnlySpan<bool> dense)
    {
        var active = new List<int>();
        for (int i = 0; i < dense.Length; i++)
            if (dense[i]) active.Add(i);
        return new SDR(dense.Length, active);
    }

    /// Create from a raw bitvector (ulong[])
    public static SDR FromBitvector(int size, ReadOnlySpan<ulong> bitvector)
    {
        var active = new List<int>();
        for (int word = 0; word < bitvector.Length; word++)
        {
            ulong bits = bitvector[word];
            while (bits != 0)
            {
                int bit = BitOperations.TrailingZeroCount(bits);
                int index = word * 64 + bit;
                if (index < size) active.Add(index);
                bits &= bits - 1; // Clear lowest set bit
            }
        }
        return new SDR(size, active);
    }

    // --- Dense Bitvector Access (lazy, cached) ---

    private int WordCount => (_size + 63) / 64;

    /// Materialize the dense bitvector representation, cached until mutation.
    public ReadOnlySpan<ulong> GetBitvector()
    {
        if (_denseCache == null)
        {
            _denseCache = new ulong[WordCount];
            foreach (int bit in _activeBits)
                _denseCache[bit >> 6] |= 1UL << (bit & 63);
        }
        return _denseCache;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool GetBit(int index) => _activeBits.AsSpan().BinarySearch(index) >= 0;

    // =========================================================================
    // SIMD-Accelerated Set Operations
    // =========================================================================
    // These use hardware POPCNT + vectorized AND/OR/XOR when available.
    // Falls back to scalar ulong operations on older hardware.
    // =========================================================================

    /// Overlap: count of shared active bits. This is the primary similarity
    /// metric in HTM — two SDRs are "similar" if they share many active bits.
    public int Overlap(SDR other)
    {
        Debug.Assert(_size == other._size, "SDR size mismatch");

        // For very sparse SDRs, sorted merge is faster than bitvector AND
        if (ActiveCount < 64 && other.ActiveCount < 64)
            return SortedIntersectionCount(_activeBits, other._activeBits);

        return BitvectorPopcount(GetBitvector(), other.GetBitvector());
    }

    /// SIMD popcount of AND'd bitvectors — the hot path for overlap computation
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int BitvectorPopcount(ReadOnlySpan<ulong> a, ReadOnlySpan<ulong> b)
    {
        int count = 0;
        int len = Math.Min(a.Length, b.Length);

        // Vectorized path: process 4 ulongs (256 bits) per iteration if AVX2 available
        if (Avx2.IsSupported && len >= 4)
        {
            ref ulong refA = ref MemoryMarshal.GetReference(a);
            ref ulong refB = ref MemoryMarshal.GetReference(b);

            int i = 0;
            for (; i + 3 < len; i += 4)
            {
                // Load 4 ulongs from each, AND them, popcount each result
                ulong w0 = Unsafe.Add(ref refA, i) & Unsafe.Add(ref refB, i);
                ulong w1 = Unsafe.Add(ref refA, i + 1) & Unsafe.Add(ref refB, i + 1);
                ulong w2 = Unsafe.Add(ref refA, i + 2) & Unsafe.Add(ref refB, i + 2);
                ulong w3 = Unsafe.Add(ref refA, i + 3) & Unsafe.Add(ref refB, i + 3);

                count += BitOperations.PopCount(w0) + BitOperations.PopCount(w1)
                       + BitOperations.PopCount(w2) + BitOperations.PopCount(w3);
            }

            // Scalar tail
            for (; i < len; i++)
                count += BitOperations.PopCount(a[i] & b[i]);
        }
        else
        {
            // Pure scalar fallback
            for (int i = 0; i < len; i++)
                count += BitOperations.PopCount(a[i] & b[i]);
        }

        return count;
    }

    /// Sorted merge intersection count — faster than bitvector for very sparse SDRs
    private static int SortedIntersectionCount(int[] a, int[] b)
    {
        int count = 0, i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { count++; i++; j++; }
            else if (a[i] < b[j]) i++;
            else j++;
        }
        return count;
    }

    /// Normalized overlap: [0.0 - 1.0] similarity score
    public float MatchScore(SDR other)
    {
        if (ActiveCount == 0) return 0f;
        return (float)Overlap(other) / ActiveCount;
    }

    /// Jaccard similarity: overlap / union size
    public float JaccardSimilarity(SDR other)
    {
        int overlap = Overlap(other);
        int unionSize = ActiveCount + other.ActiveCount - overlap;
        return unionSize == 0 ? 0f : (float)overlap / unionSize;
    }

    /// Union (OR) of two SDRs
    public SDR Union(SDR other)
    {
        Debug.Assert(_size == other._size);
        var bv = new ulong[WordCount];
        var a = GetBitvector();
        var b = other.GetBitvector();
        for (int i = 0; i < bv.Length; i++)
            bv[i] = a[i] | b[i];
        return FromBitvector(_size, bv);
    }

    /// Intersection (AND) of two SDRs
    public SDR Intersect(SDR other)
    {
        Debug.Assert(_size == other._size);
        var bv = new ulong[WordCount];
        var a = GetBitvector();
        var b = other.GetBitvector();
        for (int i = 0; i < bv.Length; i++)
            bv[i] = a[i] & b[i];
        return FromBitvector(_size, bv);
    }

    /// Difference (A AND NOT B)
    public SDR Except(SDR other)
    {
        Debug.Assert(_size == other._size);
        var bv = new ulong[WordCount];
        var a = GetBitvector();
        var b = other.GetBitvector();
        for (int i = 0; i < bv.Length; i++)
            bv[i] = a[i] & ~b[i];
        return FromBitvector(_size, bv);
    }

    /// Symmetric difference (XOR)
    public SDR SymmetricDifference(SDR other)
    {
        Debug.Assert(_size == other._size);
        var bv = new ulong[WordCount];
        var a = GetBitvector();
        var b = other.GetBitvector();
        for (int i = 0; i < bv.Length; i++)
            bv[i] = a[i] ^ b[i];
        return FromBitvector(_size, bv);
    }

    // =========================================================================
    // Noise & Subsampling — for robustness testing and dimensionality reduction
    // =========================================================================

    /// Add noise by randomly flipping a fraction of bits.
    /// `noiseFraction` of active bits are turned off and replaced with random new bits.
    /// This simulates sensor noise and tests representation robustness.
    public SDR AddNoise(float noiseFraction, Random? rng = null)
    {
        rng ??= Random.Shared;
        int bitsToFlip = (int)(ActiveCount * Math.Clamp(noiseFraction, 0f, 1f));

        var remaining = new HashSet<int>(_activeBits);

        // Remove random active bits
        var toRemove = _activeBits.OrderBy(_ => rng.Next()).Take(bitsToFlip).ToList();
        foreach (int bit in toRemove)
            remaining.Remove(bit);

        // Add random new bits (not already active)
        int added = 0;
        while (added < bitsToFlip)
        {
            int newBit = rng.Next(_size);
            if (remaining.Add(newBit)) added++;
        }

        return new SDR(_size, remaining);
    }

    /// Random subsampling: select a random subset of active bits.
    /// Used for dimensionality reduction while preserving semantic overlap properties.
    public SDR Subsample(int targetActiveBits, Random? rng = null)
    {
        rng ??= Random.Shared;
        if (targetActiveBits >= ActiveCount) return this;

        var sampled = _activeBits.OrderBy(_ => rng.Next()).Take(targetActiveBits);
        return new SDR(_size, sampled);
    }

    /// Subsample with spatial locality: prefer bits near the center of mass.
    /// Useful for preserving spatial structure in encoder outputs.
    public SDR SubsampleLocal(int targetActiveBits, Random? rng = null)
    {
        rng ??= Random.Shared;
        if (targetActiveBits >= ActiveCount) return this;

        float center = (float)_activeBits.Average();
        var weighted = _activeBits
            .Select(b => (bit: b, dist: Math.Abs(b - center)))
            .OrderBy(x => x.dist + rng.NextDouble() * 20) // locality + jitter
            .Take(targetActiveBits)
            .Select(x => x.bit);

        return new SDR(_size, weighted);
    }

    /// Project SDR to a different size while approximately preserving overlap ratios.
    /// Uses consistent hashing so the same input bit always maps to the same output bit.
    public SDR Project(int newSize, int seed = 42)
    {
        var rng = new Random(seed);
        var mapping = new int[_size];
        for (int i = 0; i < _size; i++)
            mapping[i] = rng.Next(newSize);

        var projected = _activeBits.Select(b => mapping[b]).Distinct();
        return new SDR(newSize, projected);
    }

    // --- Dense export ---
    public bool[] ToDense()
    {
        var dense = new bool[_size];
        foreach (int bit in _activeBits) dense[bit] = true;
        return dense;
    }

    // --- Equality ---
    public bool Equals(SDR? other) => other != null && _size == other._size
        && _activeBits.SequenceEqual(other._activeBits);
    public override bool Equals(object? obj) => Equals(obj as SDR);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_size);
        foreach (int b in _activeBits.Take(8)) hash.Add(b); // Sample for speed
        return hash.ToHashCode();
    }

    public override string ToString()
        => $"SDR({_size}, active={ActiveCount}, sparsity={Sparsity:P1})";
}


// ============================================================================
// SECTION 2: Encoders
// ============================================================================
// Encoders are the sensory interface of HTM. They convert raw typed data
// into SDRs with the critical property that semantically similar inputs
// produce SDRs with proportionally high bit overlap.
//
// This section provides:
//   - ScalarEncoder: contiguous sliding window
//   - RandomDistributedScalarEncoder (RDSE): hash-based random assignment
//   - DateTimeEncoder: multi-resolution temporal components
//   - CategoryEncoder: one-hot-style with configurable overlap
//   - GeospatialEncoder: lat/lon with multi-scale spatial hashing
//   - CompositeEncoder: concatenates multiple encoders
// ============================================================================

/// Base contract for all encoders
public interface IEncoder<T>
{
    int OutputSize { get; }
    SDR Encode(T value);
}

/// Scalar encoder: a fixed-width window of ON bits slides along the SDR
/// proportionally to the input value. Adjacent values share most bits.
public sealed class ScalarEncoder : IEncoder<double>
{
    public int OutputSize { get; }
    public int ActiveBits { get; }
    public double MinValue { get; }
    public double MaxValue { get; }
    public double Resolution { get; }
    public bool ClipInput { get; }
    public bool Periodic { get; }

    public ScalarEncoder(
        int size, int activeBits, double minValue, double maxValue,
        bool clipInput = true, bool periodic = false)
    {
        OutputSize = size;
        ActiveBits = activeBits;
        MinValue = minValue;
        MaxValue = maxValue;
        ClipInput = clipInput;
        Periodic = periodic;
        Resolution = (maxValue - minValue) / (periodic ? size : size - activeBits);
    }

    public SDR Encode(double value)
    {
        if (Periodic)
        {
            // Wrap value into [min, max) range
            double range = MaxValue - MinValue;
            value = MinValue + ((value - MinValue) % range + range) % range;
        }
        else if (ClipInput)
        {
            value = Math.Clamp(value, MinValue, MaxValue);
        }

        double normalized = (value - MinValue) / (MaxValue - MinValue);
        int startBit = (int)(normalized * (Periodic ? OutputSize : OutputSize - ActiveBits));

        var active = new int[ActiveBits];
        for (int i = 0; i < ActiveBits; i++)
            active[i] = (startBit + i) % OutputSize; // Wrap for periodic

        return new SDR(OutputSize, active);
    }
}

/// Random Distributed Scalar Encoder (RDSE): maps values to buckets,
/// each bucket has a randomly-assigned set of bits. Adjacent buckets share
/// most bits (differ by a controlled number of flips). This produces more
/// robust representations than contiguous encoding for high-dimensional inputs.
public sealed class RandomDistributedScalarEncoder : IEncoder<double>
{
    public int OutputSize { get; }
    public int ActiveBits { get; }
    public double Resolution { get; }

    private readonly Dictionary<int, int[]> _bucketMap = new();
    private readonly int _seed;
    private readonly int _bitsPerBucketTransition; // Bits that change between adjacent buckets

    public RandomDistributedScalarEncoder(
        int size, int activeBits, double resolution, int seed = 42)
    {
        OutputSize = size;
        ActiveBits = activeBits;
        Resolution = resolution;
        _seed = seed;
        _bitsPerBucketTransition = Math.Max(1, activeBits / 8); // ~12.5% change per bucket
    }

    public SDR Encode(double value)
    {
        int bucket = (int)Math.Floor(value / Resolution);
        return new SDR(OutputSize, GetOrCreateBucket(bucket));
    }

    private int[] GetOrCreateBucket(int bucket)
    {
        if (_bucketMap.TryGetValue(bucket, out var cached))
            return cached;

        // Build outward from bucket 0 to maintain adjacency relationships
        if (bucket == 0 || _bucketMap.Count == 0)
        {
            var rng = new Random(_seed);
            var bits = new HashSet<int>();
            while (bits.Count < ActiveBits)
                bits.Add(rng.Next(OutputSize));
            var result = bits.OrderBy(x => x).ToArray();
            _bucketMap[bucket] = result;
            return result;
        }

        // Find nearest existing bucket and walk toward target
        int nearest = _bucketMap.Keys.OrderBy(k => Math.Abs(k - bucket)).First();
        int direction = bucket > nearest ? 1 : -1;

        for (int b = nearest + direction; ; b += direction)
        {
            if (_bucketMap.ContainsKey(b))
            {
                if (b == bucket) return _bucketMap[b];
                continue;
            }

            // Derive from previous bucket
            var prev = new HashSet<int>(_bucketMap[b - direction]);
            var rng = new Random(_seed ^ b);

            for (int flip = 0; flip < _bitsPerBucketTransition; flip++)
            {
                // Remove one existing bit
                int removeIdx = rng.Next(prev.Count);
                prev.Remove(prev.ElementAt(removeIdx));

                // Add one new bit not already present
                int newBit;
                do { newBit = rng.Next(OutputSize); } while (prev.Contains(newBit));
                prev.Add(newBit);
            }

            _bucketMap[b] = prev.OrderBy(x => x).ToArray();
            if (b == bucket) return _bucketMap[b];
        }
    }
}

/// DateTime encoder: encodes temporal information across multiple resolutions.
/// Each component (hour-of-day, day-of-week, weekend, day-of-month, season)
/// is encoded independently and concatenated. This allows the system to learn
/// patterns at different temporal scales simultaneously.
public sealed class DateTimeEncoder : IEncoder<DateTime>
{
    public int OutputSize { get; }

    private readonly ScalarEncoder _hourEncoder;
    private readonly ScalarEncoder _dayOfWeekEncoder;
    private readonly ScalarEncoder _weekendEncoder;
    private readonly ScalarEncoder _dayOfMonthEncoder;
    private readonly ScalarEncoder _monthEncoder;

    private readonly int[] _componentOffsets;
    private readonly int[] _componentSizes;

    public DateTimeEncoder(
        int hourBits = 64, int dowBits = 64, int weekendBits = 32,
        int domBits = 48, int monthBits = 48,
        int activeBitsPerComponent = 5)
    {
        _hourEncoder = new ScalarEncoder(hourBits, activeBitsPerComponent, 0, 23, periodic: true);
        _dayOfWeekEncoder = new ScalarEncoder(dowBits, activeBitsPerComponent, 0, 6, periodic: true);
        _weekendEncoder = new ScalarEncoder(weekendBits, activeBitsPerComponent, 0, 1);
        _dayOfMonthEncoder = new ScalarEncoder(domBits, activeBitsPerComponent, 1, 31, periodic: true);
        _monthEncoder = new ScalarEncoder(monthBits, activeBitsPerComponent, 1, 12, periodic: true);

        _componentSizes = new[] { hourBits, dowBits, weekendBits, domBits, monthBits };
        _componentOffsets = new int[_componentSizes.Length];
        for (int i = 1; i < _componentSizes.Length; i++)
            _componentOffsets[i] = _componentOffsets[i - 1] + _componentSizes[i - 1];

        OutputSize = _componentSizes.Sum();
    }

    public SDR Encode(DateTime value)
    {
        SDR[] components = {
            _hourEncoder.Encode(value.Hour + value.Minute / 60.0),
            _dayOfWeekEncoder.Encode((int)value.DayOfWeek),
            _weekendEncoder.Encode(value.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.0 : 0.0),
            _dayOfMonthEncoder.Encode(value.Day),
            _monthEncoder.Encode(value.Month),
        };

        var allBits = new List<int>();
        for (int c = 0; c < components.Length; c++)
        {
            int offset = _componentOffsets[c];
            foreach (int bit in components[c].ActiveBits)
                allBits.Add(bit + offset);
        }

        return new SDR(OutputSize, allBits);
    }
}

/// Category encoder: assigns each category a unique block of active bits.
/// Categories can optionally share bits to encode semantic similarity
/// (e.g., "cat" and "dog" might share bits representing "animal").
public sealed class CategoryEncoder : IEncoder<string>
{
    public int OutputSize { get; }
    public int ActiveBits { get; }

    private readonly Dictionary<string, int[]> _categoryBits = new();
    private readonly int _overlapBits;  // Bits shared between ALL categories (semantic base)
    private readonly Random _rng;

    /// <param name="overlapBits">Number of bits shared across all categories (0 = fully disjoint)</param>
    public CategoryEncoder(int size, int activeBits, int overlapBits = 0, int seed = 42)
    {
        OutputSize = size;
        ActiveBits = activeBits;
        _overlapBits = Math.Min(overlapBits, activeBits / 2);
        _rng = new Random(seed);

        // Pre-generate shared base bits
        if (_overlapBits > 0)
        {
            var sharedBits = new HashSet<int>();
            while (sharedBits.Count < _overlapBits)
                sharedBits.Add(_rng.Next(size));
            _sharedBase = sharedBits.ToArray();
        }
    }

    private readonly int[] _sharedBase = Array.Empty<int>();

    /// Register a category ahead of time (optional — auto-registered on first encode)
    public void AddCategory(string category)
    {
        if (_categoryBits.ContainsKey(category)) return;

        var bits = new HashSet<int>(_sharedBase);
        int uniqueNeeded = ActiveBits - _overlapBits;
        while (bits.Count < ActiveBits)
        {
            int candidate = _rng.Next(OutputSize);
            bits.Add(candidate);
        }
        _categoryBits[category] = bits.OrderBy(x => x).ToArray();
    }

    public SDR Encode(string value)
    {
        if (!_categoryBits.ContainsKey(value))
            AddCategory(value);
        return new SDR(OutputSize, _categoryBits[value]);
    }

    /// Encode with explicit similarity: `similar` categories share additional bits.
    /// The returned SDR overlaps with the similar category's SDR by `similarityBits`.
    public SDR EncodeWithSimilarity(string value, string similarTo, int similarityBits)
    {
        var baseSdr = Encode(value);
        var similarSdr = Encode(similarTo);

        // Ensure at least `similarityBits` overlap
        int currentOverlap = baseSdr.Overlap(similarSdr);
        if (currentOverlap >= similarityBits) return baseSdr;

        // Replace some of value's unique bits with bits from similarTo
        var valueBits = new HashSet<int>(baseSdr.ActiveBits.ToArray());
        var sharedBits = new HashSet<int>(baseSdr.Intersect(similarSdr).ActiveBits.ToArray());
        var onlyInSimilar = similarSdr.Except(baseSdr).ActiveBits.ToArray();
        var onlyInValue = baseSdr.Except(similarSdr).ActiveBits.ToArray();

        int needed = similarityBits - currentOverlap;
        for (int i = 0; i < needed && i < onlyInSimilar.Length && i < onlyInValue.Length; i++)
        {
            valueBits.Remove(onlyInValue[i]);
            valueBits.Add(onlyInSimilar[i]);
        }

        var result = valueBits.OrderBy(x => x).ToArray();
        return new SDR(OutputSize, result);
    }
}

/// Geospatial encoder: encodes latitude/longitude using multi-scale spatial hashing.
/// At each scale, the coordinate space is divided into a grid of cells.
/// Each cell maps to a set of SDR bits. Multiple scales are concatenated
/// to capture both neighborhood (coarse) and precise (fine) location.
///
/// Property: nearby coordinates share bits at coarse scales, diverge at fine scales.
/// This creates a "zooming" semantic gradient.
public sealed class GeospatialEncoder : IEncoder<(double Latitude, double Longitude)>
{
    public int OutputSize { get; }

    private readonly int _scaleCount;
    private readonly int _bitsPerScale;
    private readonly int _activeBitsPerScale;
    private readonly double[] _scaleRadii;        // Radius in degrees for each scale
    private readonly int _seed;

    /// <param name="scales">Number of resolution scales (e.g., 3 = coarse/medium/fine)</param>
    /// <param name="bitsPerScale">SDR width allocated to each scale</param>
    /// <param name="activeBitsPerScale">Active bits per scale</param>
    /// <param name="maxRadiusDegrees">Coarsest scale radius in degrees (~111km per degree)</param>
    public GeospatialEncoder(
        int scales = 3, int bitsPerScale = 128, int activeBitsPerScale = 7,
        double maxRadiusDegrees = 1.0, int seed = 42)
    {
        _scaleCount = scales;
        _bitsPerScale = bitsPerScale;
        _activeBitsPerScale = activeBitsPerScale;
        _seed = seed;
        OutputSize = scales * bitsPerScale;

        // Each scale is 4x finer than the previous (quadtree-like)
        _scaleRadii = new double[scales];
        for (int s = 0; s < scales; s++)
            _scaleRadii[s] = maxRadiusDegrees / Math.Pow(4, s);
    }

    public SDR Encode((double Latitude, double Longitude) value)
    {
        var allBits = new List<int>();

        for (int scale = 0; scale < _scaleCount; scale++)
        {
            double radius = _scaleRadii[scale];
            int offset = scale * _bitsPerScale;

            // Quantize lat/lon to grid cell at this scale
            int gridLat = (int)Math.Floor(value.Latitude / radius);
            int gridLon = (int)Math.Floor(value.Longitude / radius);

            // Hash grid cell to get deterministic bit positions
            int cellHash = HashCode.Combine(_seed, scale, gridLat, gridLon);
            var rng = new Random(cellHash);

            var bits = new HashSet<int>();
            while (bits.Count < _activeBitsPerScale)
                bits.Add(offset + rng.Next(_bitsPerScale));

            allBits.AddRange(bits);
        }

        return new SDR(OutputSize, allBits);
    }

    /// Encode with a "smear" that activates bits from neighboring grid cells.
    /// This provides smoother transitions at cell boundaries.
    public SDR EncodeSmeared((double Lat, double Lon) value, float smearRadius = 0.5f)
    {
        var center = Encode(value);
        // Activate bits from the 8 neighboring cells at the finest scale
        double fineRadius = _scaleRadii[^1];
        var neighbors = new[]
        {
            (value.Lat + fineRadius * smearRadius, value.Lon),
            (value.Lat - fineRadius * smearRadius, value.Lon),
            (value.Lat, value.Lon + fineRadius * smearRadius),
            (value.Lat, value.Lon - fineRadius * smearRadius),
        };

        var result = center;
        foreach (var n in neighbors)
            result = result.Union(Encode(n));

        return result;
    }
}

/// Composite encoder: concatenates multiple typed encoders into a single SDR.
/// Each sub-encoder's output is offset into a non-overlapping region of the
/// final SDR. Used to encode multi-feature input records (e.g., timestamp + value).
public sealed class CompositeEncoder
{
    private readonly List<EncoderSlot> _slots = new();
    public int TotalSize => _slots.Sum(s => s.Size);

    private record EncoderSlot(string Name, int Size, Func<object, SDR> Encode);

    public CompositeEncoder AddEncoder<T>(string name, IEncoder<T> encoder)
    {
        _slots.Add(new EncoderSlot(name, encoder.OutputSize, v => encoder.Encode((T)v)));
        return this;
    }

    public CompositeEncoder AddRawEncoder(string name, int size, Func<object, SDR> encode)
    {
        _slots.Add(new EncoderSlot(name, size, encode));
        return this;
    }

    public SDR Encode(Dictionary<string, object> inputs)
    {
        var allBits = new List<int>();
        int offset = 0;

        foreach (var slot in _slots)
        {
            if (inputs.TryGetValue(slot.Name, out var value))
            {
                var sdr = slot.Encode(value);
                foreach (int bit in sdr.ActiveBits)
                    allBits.Add(bit + offset);
            }
            offset += slot.Size;
        }

        return new SDR(TotalSize, allBits);
    }
}


// ============================================================================
// SECTION 3: Synapse & Dendrite Infrastructure with Lifecycle Management
// ============================================================================
// HTM synapses model biological synaptic connections with a "permanence"
// value that determines connectivity. Unlike deep learning weights, HTM
// synapses are binary-connected (above/below threshold) with the permanence
// providing a slow-learning analog substrate.
//
// Lifecycle management ensures bounded memory usage:
//   - Max segments per cell prevents unbounded growth
//   - Max synapses per segment caps connectivity
//   - Pruning removes low-permanence synapses
//   - Segment cleanup removes segments with too few remaining synapses
// ============================================================================

public struct Synapse
{
    public int PresynapticIndex;    // Source cell index
    public float Permanence;        // [0.0 - 1.0]
    public int CreatedAtIteration;  // For age-based analysis

    public Synapse(int presynapticIndex, float permanence, int iteration = 0)
    {
        PresynapticIndex = presynapticIndex;
        Permanence = permanence;
        CreatedAtIteration = iteration;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsConnected(float threshold) => Permanence >= threshold;
}

public sealed class DendriteSegment
{
    private readonly List<Synapse> _synapses = new();
    public int CellIndex { get; }                      // Owning cell
    public int CreatedAtIteration { get; }
    public int LastActivatedIteration { get; set; }     // For LRU eviction

    // Read access
    public IReadOnlyList<Synapse> Synapses => _synapses;
    public int SynapseCount => _synapses.Count;

    public DendriteSegment(int cellIndex, int iteration)
    {
        CellIndex = cellIndex;
        CreatedAtIteration = iteration;
    }

    // --- Activity Computation ---

    /// Count connected synapses whose presynaptic cell is active
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputeActivity(HashSet<int> activeCells, float connectedThreshold)
    {
        int count = 0;
        foreach (var syn in CollectionsMarshal.AsSpan(_synapses))
        {
            if (syn.Permanence >= connectedThreshold && activeCells.Contains(syn.PresynapticIndex))
                count++;
        }
        return count;
    }

    /// Count ALL synapses (including sub-threshold) whose presynaptic cell is active.
    /// Used when selecting the best matching segment for learning.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ComputePotentialActivity(HashSet<int> activeCells)
    {
        int count = 0;
        foreach (var syn in CollectionsMarshal.AsSpan(_synapses))
        {
            if (activeCells.Contains(syn.PresynapticIndex))
                count++;
        }
        return count;
    }

    // --- Learning ---

    /// Hebbian learning: reinforce synapses to active cells, weaken to inactive
    public void AdaptSynapses(
        HashSet<int> activeCells,
        float permanenceIncrement,
        float permanenceDecrement)
    {
        var span = CollectionsMarshal.AsSpan(_synapses);
        for (int i = 0; i < span.Length; i++)
        {
            ref var syn = ref span[i];
            syn.Permanence = activeCells.Contains(syn.PresynapticIndex)
                ? Math.Min(1.0f, syn.Permanence + permanenceIncrement)
                : Math.Max(0.0f, syn.Permanence - permanenceDecrement);
        }
    }

    /// Punish all synapses connected to previously active cells
    public void PunishSynapses(HashSet<int> activeCells, float decrement)
    {
        var span = CollectionsMarshal.AsSpan(_synapses);
        for (int i = 0; i < span.Length; i++)
        {
            ref var syn = ref span[i];
            if (activeCells.Contains(syn.PresynapticIndex))
                syn.Permanence = Math.Max(0.0f, syn.Permanence - decrement);
        }
    }

    // --- Synapse Lifecycle ---

    public void AddSynapse(Synapse synapse) => _synapses.Add(synapse);

    public void AddSynapses(IEnumerable<Synapse> synapses) => _synapses.AddRange(synapses);

    /// Remove synapses with permanence below a threshold (dead synapses)
    public int PruneSynapses(float minPermanence)
    {
        int before = _synapses.Count;
        _synapses.RemoveAll(s => s.Permanence < minPermanence);
        return before - _synapses.Count;
    }

    /// Cap synapse count by removing lowest-permanence synapses
    public void EnforceMaxSynapses(int maxSynapses)
    {
        if (_synapses.Count <= maxSynapses) return;

        _synapses.Sort((a, b) => b.Permanence.CompareTo(a.Permanence));
        _synapses.RemoveRange(maxSynapses, _synapses.Count - maxSynapses);
    }

    /// Bump all synapse permanences by a given amount (clamped to [0, 1]).
    /// Used by the Spatial Pooler to rescue dead columns.
    public void BumpAllPermanences(float amount)
    {
        var span = CollectionsMarshal.AsSpan(_synapses);
        for (int i = 0; i < span.Length; i++)
        {
            ref var syn = ref span[i];
            syn.Permanence = Math.Min(1.0f, syn.Permanence + amount);
        }
    }

    /// Check if this segment has a synapse to the given presynaptic cell
    public bool HasSynapseTo(int presynapticIndex)
        => _synapses.Any(s => s.PresynapticIndex == presynapticIndex);
}

/// Manages the dendrite segments for a single cell, enforcing max-segment limits
/// and providing LRU eviction of least-recently-used segments.
public sealed class CellSegmentManager
{
    private readonly List<DendriteSegment> _segments = new();
    private readonly int _maxSegmentsPerCell;
    private readonly int _maxSynapsesPerSegment;

    public IReadOnlyList<DendriteSegment> Segments => _segments;
    public int SegmentCount => _segments.Count;

    public CellSegmentManager(int maxSegmentsPerCell, int maxSynapsesPerSegment)
    {
        _maxSegmentsPerCell = maxSegmentsPerCell;
        _maxSynapsesPerSegment = maxSynapsesPerSegment;
    }

    /// Create a new segment, evicting the least-recently-used if at capacity
    public DendriteSegment CreateSegment(int cellIndex, int iteration)
    {
        if (_segments.Count >= _maxSegmentsPerCell)
        {
            // Evict LRU: the segment activated longest ago
            int lruIdx = 0;
            int lruIteration = int.MaxValue;
            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i].LastActivatedIteration < lruIteration)
                {
                    lruIteration = _segments[i].LastActivatedIteration;
                    lruIdx = i;
                }
            }
            _segments.RemoveAt(lruIdx);
        }

        var segment = new DendriteSegment(cellIndex, iteration);
        _segments.Add(segment);
        return segment;
    }

    /// Remove segments that have decayed to fewer than `minSynapses` synapses
    public int CleanupSegments(int minSynapses)
    {
        int before = _segments.Count;
        _segments.RemoveAll(s => s.SynapseCount < minSynapses);
        return before - _segments.Count;
    }

    /// Enforce max synapses on all segments
    public void EnforceMaxSynapses()
    {
        foreach (var seg in _segments)
            seg.EnforceMaxSynapses(_maxSynapsesPerSegment);
    }

    // --- Serialization Support ---

    /// Clear all segments (used during deserialization to restore state)
    public void ClearSegments() => _segments.Clear();

    /// Add a pre-built segment (used during deserialization)
    public void RestoreSegment(DendriteSegment segment) => _segments.Add(segment);

    /// Full lifecycle maintenance pass
    public SegmentMaintenanceResult Maintain(float pruneThreshold, int minSynapses)
    {
        int prunedSynapses = 0;
        foreach (var seg in _segments)
            prunedSynapses += seg.PruneSynapses(pruneThreshold);

        int removedSegments = CleanupSegments(minSynapses);
        EnforceMaxSynapses();

        return new SegmentMaintenanceResult(prunedSynapses, removedSegments);
    }
}

public record struct SegmentMaintenanceResult(int PrunedSynapses, int RemovedSegments);


// ============================================================================
// SECTION 4: Spatial Pooler with Local Inhibition
// ============================================================================
// The Spatial Pooler converts variable-density encoder output into a
// fixed-sparsity representation using competitive Hebbian learning.
//
// Enhancements:
//   - Local inhibition: columns only compete with neighbors within a radius,
//     enabling topographic map formation
//   - Configurable boosting strategies
//   - Overlap duty cycle tracking for dead-column detection
//   - Diagnostic hooks for monitoring convergence
// ============================================================================

public enum InhibitionMode { Global, Local }
public enum BoostingStrategy { Exponential, Linear, None }

public sealed class SpatialPoolerConfig
{
    public int InputSize { get; init; } = 400;
    public int ColumnCount { get; init; } = 2048;
    public float TargetSparsity { get; init; } = 0.02f;
    public float ConnectedThreshold { get; init; } = 0.5f;
    public float PermanenceIncrement { get; init; } = 0.05f;
    public float PermanenceDecrement { get; init; } = 0.008f;
    public float StimulusThreshold { get; init; } = 3f;
    public int PotentialRadius { get; init; } = -1;        // -1 = global
    public float PotentialPct { get; init; } = 0.85f;
    public InhibitionMode Inhibition { get; init; } = InhibitionMode.Global;
    public int InhibitionRadius { get; init; } = 50;       // For local inhibition
    public BoostingStrategy Boosting { get; init; } = BoostingStrategy.Exponential;
    public float BoostStrength { get; init; } = 1.0f;
    public float MinPctOverlapDutyCycles { get; init; } = 0.001f;
    public int DutyCyclePeriod { get; init; } = 1000;
    public int Seed { get; init; } = 42;
}

public sealed class SpatialPooler
{
    // --- Configuration ---
    private readonly SpatialPoolerConfig _config;

    // --- Proximal dendrites: one per column, connecting to input bits ---
    private readonly DendriteSegment[] _proximalDendrites;

    // --- Homeostatic state ---
    private readonly float[] _boostFactors;
    private readonly float[] _activeDutyCycles;
    private readonly float[] _overlapDutyCycles;
    private readonly float[] _minLocalActivity;

    // --- Topology for local inhibition ---
    // Maps each column to its inhibition neighborhood
    private readonly int[][] _inhibitionNeighborhoods;

    private readonly Random _rng;
    private int _iteration;

    // --- Diagnostics ---
    public SpatialPoolerMetrics Metrics { get; } = new();

    public SpatialPooler(SpatialPoolerConfig config)
    {
        _config = config;
        _rng = new Random(config.Seed);
        _proximalDendrites = new DendriteSegment[config.ColumnCount];
        _boostFactors = new float[config.ColumnCount];
        _activeDutyCycles = new float[config.ColumnCount];
        _overlapDutyCycles = new float[config.ColumnCount];
        _minLocalActivity = new float[config.ColumnCount];

        Array.Fill(_boostFactors, 1.0f);

        _inhibitionNeighborhoods = BuildInhibitionNeighborhoods();
        InitializeProximalConnections();
    }

    private int[][] BuildInhibitionNeighborhoods()
    {
        var neighborhoods = new int[_config.ColumnCount][];

        if (_config.Inhibition == InhibitionMode.Global)
        {
            // Global: every column competes with every other
            var all = Enumerable.Range(0, _config.ColumnCount).ToArray();
            for (int i = 0; i < _config.ColumnCount; i++)
                neighborhoods[i] = all;
        }
        else
        {
            // Local: each column competes only within InhibitionRadius
            for (int col = 0; col < _config.ColumnCount; col++)
            {
                int start = Math.Max(0, col - _config.InhibitionRadius);
                int end = Math.Min(_config.ColumnCount - 1, col + _config.InhibitionRadius);
                neighborhoods[col] = Enumerable.Range(start, end - start + 1).ToArray();
            }
        }

        return neighborhoods;
    }

    private void InitializeProximalConnections()
    {
        for (int col = 0; col < _config.ColumnCount; col++)
        {
            _proximalDendrites[col] = new DendriteSegment(col, 0);

            // Map column to an input-space center (topographic mapping)
            int centerInput = col * _config.InputSize / _config.ColumnCount;

            // Determine potential pool: input bits this column CAN connect to
            int radius = _config.PotentialRadius < 0
                ? _config.InputSize
                : _config.PotentialRadius;

            var potentialInputs = new List<int>();
            for (int i = 0; i < _config.InputSize; i++)
            {
                int distance = Math.Min(
                    Math.Abs(i - centerInput),
                    _config.InputSize - Math.Abs(i - centerInput)); // Wrap-around distance

                if (distance <= radius)
                    potentialInputs.Add(i);
            }

            // Subsample the potential pool
            int keepCount = (int)(potentialInputs.Count * _config.PotentialPct);
            var selected = potentialInputs.OrderBy(_ => _rng.Next()).Take(keepCount);

            foreach (int inputIdx in selected)
            {
                float perm = _config.ConnectedThreshold + (float)(_rng.NextDouble() * 0.1 - 0.05);
                _proximalDendrites[col].AddSynapse(
                    new Synapse(inputIdx, Math.Clamp(perm, 0f, 1f)));
            }
        }
    }

    /// Main compute: encode SDR → fixed-sparsity active columns
    public SDR Compute(SDR input, bool learn = true)
    {
        _iteration++;
        var sw = Stopwatch.StartNew();

        // Phase 1: Overlap
        var overlaps = ComputeOverlaps(input);

        // Phase 2: Boosting
        var boostedOverlaps = new float[_config.ColumnCount];
        for (int i = 0; i < _config.ColumnCount; i++)
            boostedOverlaps[i] = overlaps[i] * _boostFactors[i];

        // Phase 3: Inhibition (local or global)
        var activeColumns = Inhibit(boostedOverlaps);

        // Phase 4: Learning
        if (learn)
        {
            foreach (int col in activeColumns)
            {
                _proximalDendrites[col].AdaptSynapses(
                    input.ActiveBits.ToArray().ToHashSet(),
                    _config.PermanenceIncrement,
                    _config.PermanenceDecrement);
            }

            UpdateDutyCycles(activeColumns, overlaps);
            UpdateBoostFactors();
            BumpWeakColumns(overlaps);
        }

        // Diagnostics
        sw.Stop();
        Metrics.RecordCompute(activeColumns.Count, sw.Elapsed, overlaps, _boostFactors);

        return new SDR(_config.ColumnCount, activeColumns);
    }

    private float[] ComputeOverlaps(SDR input)
    {
        var inputSet = input.ActiveBits.ToArray().ToHashSet();
        var overlaps = new float[_config.ColumnCount];

        for (int col = 0; col < _config.ColumnCount; col++)
        {
            int overlap = _proximalDendrites[col].ComputeActivity(inputSet, _config.ConnectedThreshold);
            overlaps[col] = overlap >= _config.StimulusThreshold ? overlap : 0;
        }

        return overlaps;
    }

    /// Inhibition: select winning columns. For local inhibition, each column
    /// must rank in the top-k within its neighborhood.
    private HashSet<int> Inhibit(float[] overlaps)
    {
        var active = new HashSet<int>();

        if (_config.Inhibition == InhibitionMode.Global)
        {
            int numActive = Math.Max(1, (int)(_config.ColumnCount * _config.TargetSparsity));
            var winners = Enumerable.Range(0, _config.ColumnCount)
                .Where(c => overlaps[c] > 0)
                .OrderByDescending(c => overlaps[c])
                .ThenBy(_ => _rng.Next()) // Tie-breaking
                .Take(numActive);
            active.UnionWith(winners);
        }
        else
        {
            // Local inhibition: for each column, check if it's in the top-k
            // of its local neighborhood. This produces a variable total active
            // count but ensures spatial uniformity.
            for (int col = 0; col < _config.ColumnCount; col++)
            {
                if (overlaps[col] <= 0) continue;

                var neighborhood = _inhibitionNeighborhoods[col];
                int localK = Math.Max(1, (int)(neighborhood.Length * _config.TargetSparsity));

                // Count how many neighbors have higher overlap
                int betterNeighbors = 0;
                foreach (int neighbor in neighborhood)
                {
                    if (neighbor != col && overlaps[neighbor] > overlaps[col])
                        betterNeighbors++;
                }

                if (betterNeighbors < localK)
                    active.Add(col);
            }
        }

        return active;
    }

    private void UpdateDutyCycles(HashSet<int> activeColumns, float[] overlaps)
    {
        float period = Math.Min(_iteration, _config.DutyCyclePeriod);
        float alpha = 1.0f / period;

        for (int col = 0; col < _config.ColumnCount; col++)
        {
            float isActive = activeColumns.Contains(col) ? 1f : 0f;
            _activeDutyCycles[col] = (1 - alpha) * _activeDutyCycles[col] + alpha * isActive;
            _overlapDutyCycles[col] = (1 - alpha) * _overlapDutyCycles[col]
                                      + alpha * (overlaps[col] > 0 ? 1f : 0f);
        }
    }

    private void UpdateBoostFactors()
    {
        if (_config.Boosting == BoostingStrategy.None) return;

        for (int col = 0; col < _config.ColumnCount; col++)
        {
            float targetDensity = _config.TargetSparsity;

            // For local inhibition, compute target from neighborhood
            if (_config.Inhibition == InhibitionMode.Local)
            {
                var neighbors = _inhibitionNeighborhoods[col];
                targetDensity = neighbors.Average(n => _activeDutyCycles[n]);
                if (targetDensity < 0.001f) targetDensity = _config.TargetSparsity;
            }

            _boostFactors[col] = _config.Boosting switch
            {
                BoostingStrategy.Exponential =>
                    MathF.Exp(_config.BoostStrength * -((_activeDutyCycles[col] - targetDensity) / targetDensity)),
                BoostingStrategy.Linear =>
                    1.0f + _config.BoostStrength * (targetDensity - _activeDutyCycles[col]),
                _ => 1.0f,
            };

            _boostFactors[col] = Math.Max(0.01f, _boostFactors[col]); // Floor
        }
    }

    /// Increase permanences on columns that have very low overlap duty cycles.
    /// This rescues "dead" columns that never get enough input overlap.
    private void BumpWeakColumns(float[] overlaps)
    {
        for (int col = 0; col < _config.ColumnCount; col++)
        {
            if (_overlapDutyCycles[col] < _config.MinPctOverlapDutyCycles)
            {
                // Bump all permanences toward connected threshold (BAMI: 0.1 * connectedPerm)
                _proximalDendrites[col].BumpAllPermanences(0.1f * _config.ConnectedThreshold);
            }
        }
    }

    // --- Serialization ---

    /// Serialize all mutable SP state: proximal dendrites, boost factors, duty cycles, iteration.
    public void SerializeState(BinaryWriter bw)
    {
        bw.Write(_config.ColumnCount);
        bw.Write(_config.InputSize);
        bw.Write(_iteration);

        for (int col = 0; col < _config.ColumnCount; col++)
            HtmSerializer.WriteSegment(bw, _proximalDendrites[col]);

        for (int col = 0; col < _config.ColumnCount; col++)
        {
            bw.Write(_boostFactors[col]);
            bw.Write(_activeDutyCycles[col]);
            bw.Write(_overlapDutyCycles[col]);
        }
    }

    /// Restore SP state from serialized data. Must be called on an SP with matching config.
    public void DeserializeState(BinaryReader br)
    {
        int columnCount = br.ReadInt32();
        int inputSize = br.ReadInt32();
        if (columnCount != _config.ColumnCount || inputSize != _config.InputSize)
            throw new FormatException(
                $"SP config mismatch: expected ({_config.ColumnCount}, {_config.InputSize}), " +
                $"got ({columnCount}, {inputSize})");

        _iteration = br.ReadInt32();

        for (int col = 0; col < _config.ColumnCount; col++)
            _proximalDendrites[col] = HtmSerializer.ReadSegment(br);

        for (int col = 0; col < _config.ColumnCount; col++)
        {
            _boostFactors[col] = br.ReadSingle();
            _activeDutyCycles[col] = br.ReadSingle();
            _overlapDutyCycles[col] = br.ReadSingle();
        }
    }
}

public sealed class SpatialPoolerMetrics
{
    private long _computeCount;
    private double _totalComputeMs;
    private float _avgActiveColumns;
    private float _avgOverlap;
    private float _avgBoostFactor;
    private int _deadColumnCount;

    public long ComputeCount => _computeCount;
    public double AvgComputeMs => _computeCount > 0 ? _totalComputeMs / _computeCount : 0;
    public float AvgActiveColumns => _avgActiveColumns;
    public float AvgOverlap => _avgOverlap;
    public float AvgBoostFactor => _avgBoostFactor;
    public int DeadColumnCount => _deadColumnCount;

    internal void RecordCompute(int activeCount, TimeSpan elapsed, float[] overlaps, float[] boosts)
    {
        _computeCount++;
        _totalComputeMs += elapsed.TotalMilliseconds;

        float alpha = 0.01f;
        _avgActiveColumns = (1 - alpha) * _avgActiveColumns + alpha * activeCount;
        _avgOverlap = (1 - alpha) * _avgOverlap + alpha * overlaps.Where(o => o > 0).DefaultIfEmpty(0).Average();
        _avgBoostFactor = (1 - alpha) * _avgBoostFactor + alpha * boosts.Average();
        _deadColumnCount = overlaps.Count(o => o == 0);
    }

    public override string ToString() =>
        $"SP Metrics: computes={ComputeCount}, avgMs={AvgComputeMs:F2}, " +
        $"avgActive={AvgActiveColumns:F1}, avgOverlap={AvgOverlap:F1}, " +
        $"avgBoost={AvgBoostFactor:F3}, deadCols={DeadColumnCount}";
}


// ============================================================================
// SECTION 5: Enhanced Temporal Memory
// ============================================================================
// The Temporal Memory learns sequences by predicting which columns will
// become active next. It uses distal dendrite segments on cells to detect
// the contextual pattern of lateral/recurrent activity.
//
// Enhancements:
//   - Max segments per cell with LRU eviction
//   - Periodic segment cleanup (prune dead synapses, remove empty segments)
//   - Configurable max new synapse growth per learning event
//   - Segment matching cache for performance
//   - Detailed per-step diagnostic output
// ============================================================================

public sealed class TemporalMemoryConfig
{
    public int ColumnCount { get; init; } = 2048;
    public int CellsPerColumn { get; init; } = 32;
    public int ActivationThreshold { get; init; } = 13;
    public int MinThreshold { get; init; } = 10;
    public int MaxNewSynapseCount { get; init; } = 20;
    public int MaxSegmentsPerCell { get; init; } = 128;
    public int MaxSynapsesPerSegment { get; init; } = 64;
    public float ConnectedThreshold { get; init; } = 0.5f;
    public float InitialPermanence { get; init; } = 0.21f;
    public float PermanenceIncrement { get; init; } = 0.1f;
    public float PermanenceDecrement { get; init; } = 0.1f;
    public float PredictedDecrement { get; init; } = 0.01f;
    public float SynapsePruneThreshold { get; init; } = 0.01f;
    public int SegmentCleanupInterval { get; init; } = 1000;   // Cleanup every N iterations
    public int MinSynapsesForViableSegment { get; init; } = 3;
    public int Seed { get; init; } = 42;
}

public sealed class TemporalMemory
{
    private readonly TemporalMemoryConfig _config;

    // --- Cell & Segment Storage ---
    // Each cell owns a CellSegmentManager that enforces max-segment limits
    private readonly CellSegmentManager[] _cellSegments;

    // --- State (current timestep) ---
    private HashSet<int> _activeCells = new();
    private HashSet<int> _winnerCells = new();
    private HashSet<int> _predictiveCells = new();

    // --- State (previous timestep, for learning) ---
    private HashSet<int> _prevActiveCells = new();
    private HashSet<int> _prevWinnerCells = new();
    private HashSet<int> _prevPredictiveCells = new();

    // --- Matching cache: segments that are active or matching this timestep ---
    private readonly Dictionary<int, List<(DendriteSegment Segment, int Activity)>> _activeSegmentCache = new();
    private readonly Dictionary<int, List<(DendriteSegment Segment, int PotentialActivity)>> _matchingSegmentCache = new();

    private readonly Random _rng;
    private int _iteration;

    // --- Outputs ---
    public float Anomaly { get; private set; }
    public int TotalCells => _config.ColumnCount * _config.CellsPerColumn;

    // --- Diagnostics ---
    public TemporalMemoryMetrics Metrics { get; } = new();

    public TemporalMemory(TemporalMemoryConfig config)
    {
        _config = config;
        _rng = new Random(config.Seed);

        _cellSegments = new CellSegmentManager[config.ColumnCount * config.CellsPerColumn];
        for (int i = 0; i < _cellSegments.Length; i++)
            _cellSegments[i] = new CellSegmentManager(config.MaxSegmentsPerCell, config.MaxSynapsesPerSegment);
    }

    // --- Coordinate helpers ---
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellIndex(int column, int cellInColumn) => column * _config.CellsPerColumn + cellInColumn;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellColumn(int cellIndex) => cellIndex / _config.CellsPerColumn;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CellInColumn(int cellIndex) => cellIndex % _config.CellsPerColumn;

    /// Main compute: process one timestep
    public TemporalMemoryOutput Compute(SDR activeColumns, bool learn = true)
    {
        _iteration++;
        var sw = Stopwatch.StartNew();

        // Save previous state
        _prevActiveCells = _activeCells;
        _prevWinnerCells = _winnerCells;
        _prevPredictiveCells = _predictiveCells;

        // Build segment caches against PREVIOUS active cells
        BuildSegmentCaches();

        var predictedColumns = GetPredictedColumns();
        var newActiveCells = new HashSet<int>();
        var newWinnerCells = new HashSet<int>();
        int burstCount = 0;
        int predictedActiveCount = 0;

        // ---- Phase 1: Activate cells ----
        foreach (int col in activeColumns.ActiveBits)
        {
            bool wasPredicted = predictedColumns.Contains(col);

            if (wasPredicted)
            {
                // CORRECTLY PREDICTED: activate only predicted cells
                predictedActiveCount++;
                for (int c = 0; c < _config.CellsPerColumn; c++)
                {
                    int cellIdx = CellIndex(col, c);
                    if (_predictiveCells.Contains(cellIdx))
                    {
                        newActiveCells.Add(cellIdx);
                        newWinnerCells.Add(cellIdx);
                    }
                }
            }
            else
            {
                // BURST: activate all cells in the column (surprise)
                burstCount++;
                for (int c = 0; c < _config.CellsPerColumn; c++)
                    newActiveCells.Add(CellIndex(col, c));

                // Pick one winner cell for learning
                int winner = SelectBestMatchingCell(col);
                newWinnerCells.Add(winner);
            }
        }

        // ---- Phase 2: Anomaly ----
        Anomaly = activeColumns.ActiveCount == 0
            ? 0f
            : 1f - (float)predictedActiveCount / activeColumns.ActiveCount;

        // ---- Phase 3: Learning ----
        if (learn)
        {
            LearnOnActiveSegments(newActiveCells);
            LearnOnBurstingColumns(activeColumns, newWinnerCells);
            PunishIncorrectPredictions(activeColumns);
        }

        // ---- Phase 4: Compute next predictions ----
        _activeCells = newActiveCells;
        _winnerCells = newWinnerCells;
        _predictiveCells = ComputePredictiveCells();

        // ---- Periodic maintenance ----
        if (learn && _iteration % _config.SegmentCleanupInterval == 0)
            RunSegmentCleanup();

        sw.Stop();
        Metrics.RecordCompute(
            activeColumns.ActiveCount, burstCount, predictedActiveCount,
            _activeCells.Count, _predictiveCells.Count, Anomaly, sw.Elapsed);

        return new TemporalMemoryOutput
        {
            ActiveCells = new HashSet<int>(_activeCells),
            WinnerCells = new HashSet<int>(_winnerCells),
            PredictiveCells = new HashSet<int>(_predictiveCells),
            ActiveColumnCount = activeColumns.ActiveCount,
            BurstingColumnCount = burstCount,
            PredictedActiveColumnCount = predictedActiveCount,
            Anomaly = Anomaly,
        };
    }

    /// Build caches of active and matching segments for current timestep.
    /// This avoids redundant computation during activation and learning.
    private void BuildSegmentCaches()
    {
        _activeSegmentCache.Clear();
        _matchingSegmentCache.Clear();

        for (int cellIdx = 0; cellIdx < _cellSegments.Length; cellIdx++)
        {
            foreach (var segment in _cellSegments[cellIdx].Segments)
            {
                int activity = segment.ComputeActivity(_prevActiveCells, _config.ConnectedThreshold);

                if (activity >= _config.ActivationThreshold)
                {
                    if (!_activeSegmentCache.TryGetValue(cellIdx, out var list))
                    {
                        list = new List<(DendriteSegment, int)>();
                        _activeSegmentCache[cellIdx] = list;
                    }
                    list.Add((segment, activity));
                }

                int potentialActivity = segment.ComputePotentialActivity(_prevActiveCells);
                if (potentialActivity >= _config.MinThreshold)
                {
                    if (!_matchingSegmentCache.TryGetValue(cellIdx, out var mlist))
                    {
                        mlist = new List<(DendriteSegment, int)>();
                        _matchingSegmentCache[cellIdx] = mlist;
                    }
                    mlist.Add((segment, potentialActivity));
                }
            }
        }
    }

    private HashSet<int> ComputePredictiveCells()
    {
        var predictive = new HashSet<int>();

        for (int cellIdx = 0; cellIdx < _cellSegments.Length; cellIdx++)
        {
            foreach (var segment in _cellSegments[cellIdx].Segments)
            {
                if (segment.ComputeActivity(_activeCells, _config.ConnectedThreshold)
                    >= _config.ActivationThreshold)
                {
                    predictive.Add(cellIdx);
                    break;
                }
            }
        }

        return predictive;
    }

    private HashSet<int> GetPredictedColumns()
        => _predictiveCells.Select(CellColumn).ToHashSet();

    /// Select the best cell in a bursting column for learning.
    /// Priority: cell with best-matching segment → cell with fewest segments (least used)
    private int SelectBestMatchingCell(int column)
    {
        DendriteSegment? bestSegment = null;
        int bestCellIdx = -1;
        int bestScore = -1;

        for (int c = 0; c < _config.CellsPerColumn; c++)
        {
            int cellIdx = CellIndex(column, c);
            if (_matchingSegmentCache.TryGetValue(cellIdx, out var matches))
            {
                foreach (var (segment, potentialActivity) in matches)
                {
                    if (potentialActivity > bestScore)
                    {
                        bestScore = potentialActivity;
                        bestSegment = segment;
                        bestCellIdx = cellIdx;
                    }
                }
            }
        }

        if (bestCellIdx >= 0)
            return bestCellIdx;

        // No matching segment: pick cell with fewest segments
        int minSegments = int.MaxValue;
        int leastUsedCell = CellIndex(column, 0);

        for (int c = 0; c < _config.CellsPerColumn; c++)
        {
            int cellIdx = CellIndex(column, c);
            int segCount = _cellSegments[cellIdx].SegmentCount;
            if (segCount < minSegments)
            {
                minSegments = segCount;
                leastUsedCell = cellIdx;
            }
        }

        return leastUsedCell;
    }

    /// Strengthen synapses on segments that correctly predicted active cells
    private void LearnOnActiveSegments(HashSet<int> activeCells)
    {
        foreach (int cellIdx in activeCells)
        {
            if (!_activeSegmentCache.TryGetValue(cellIdx, out var segments)) continue;

            foreach (var (segment, _) in segments)
            {
                segment.AdaptSynapses(_prevActiveCells,
                    _config.PermanenceIncrement, _config.PermanenceDecrement);
                segment.LastActivatedIteration = _iteration;

                // Grow new synapses to previously active cells not yet connected
                GrowSynapses(segment, _prevWinnerCells);
            }
        }
    }

    /// Create new segments on winner cells in bursting columns
    private void LearnOnBurstingColumns(SDR activeColumns, HashSet<int> winnerCells)
    {
        if (_prevWinnerCells.Count == 0) return;

        foreach (int col in activeColumns.ActiveBits)
        {
            // Only learn on bursting columns (not predicted)
            bool hasPredictedWinner = false;
            for (int c = 0; c < _config.CellsPerColumn; c++)
            {
                int cellIdx = CellIndex(col, c);
                if (winnerCells.Contains(cellIdx) && _activeSegmentCache.ContainsKey(cellIdx))
                {
                    hasPredictedWinner = true;
                    break;
                }
            }
            if (hasPredictedWinner) continue;

            // Find winner cell in this column
            int winner = -1;
            for (int c = 0; c < _config.CellsPerColumn; c++)
            {
                int cellIdx = CellIndex(col, c);
                if (winnerCells.Contains(cellIdx)) { winner = cellIdx; break; }
            }
            if (winner < 0) continue;

            // Check if there's a matching segment to reinforce
            if (_matchingSegmentCache.TryGetValue(winner, out var matches) && matches.Count > 0)
            {
                var bestMatch = matches.OrderByDescending(m => m.PotentialActivity).First();
                bestMatch.Segment.AdaptSynapses(_prevActiveCells,
                    _config.PermanenceIncrement, _config.PermanenceDecrement);
                bestMatch.Segment.LastActivatedIteration = _iteration;
                GrowSynapses(bestMatch.Segment, _prevWinnerCells);
            }
            else
            {
                // Create a brand new segment
                var newSegment = _cellSegments[winner].CreateSegment(winner, _iteration);

                var targets = _prevWinnerCells
                    .Where(c => !newSegment.HasSynapseTo(c))
                    .OrderBy(_ => _rng.Next())
                    .Take(_config.MaxNewSynapseCount);

                foreach (int target in targets)
                {
                    newSegment.AddSynapse(new Synapse(target, _config.InitialPermanence, _iteration));
                }
            }
        }
    }

    /// Grow new synapses on an existing segment to connect to winner cells
    /// it doesn't already have synapses to.
    private void GrowSynapses(DendriteSegment segment, HashSet<int> targetCells)
    {
        int maxNew = _config.MaxNewSynapseCount - segment.SynapseCount;
        if (maxNew <= 0) return;

        var candidates = targetCells
            .Where(c => !segment.HasSynapseTo(c))
            .OrderBy(_ => _rng.Next())
            .Take(maxNew);

        foreach (int target in candidates)
        {
            segment.AddSynapse(new Synapse(target, _config.InitialPermanence, _iteration));
        }

        segment.EnforceMaxSynapses(_config.MaxSynapsesPerSegment);
    }

    /// Punish segments that predicted columns which did NOT become active
    private void PunishIncorrectPredictions(SDR activeColumns)
    {
        var activeColumnSet = activeColumns.ActiveBits.ToArray().ToHashSet();

        foreach (int cellIdx in _prevPredictiveCells)
        {
            int col = CellColumn(cellIdx);
            if (activeColumnSet.Contains(col)) continue; // Correct prediction

            // This cell predicted incorrectly — punish its active segments
            foreach (var segment in _cellSegments[cellIdx].Segments)
            {
                if (segment.ComputeActivity(_prevActiveCells, _config.ConnectedThreshold)
                    >= _config.ActivationThreshold)
                {
                    segment.PunishSynapses(_prevActiveCells, _config.PredictedDecrement);
                }
            }
        }
    }

    /// Periodic cleanup: prune dead synapses and remove empty segments.
    /// This prevents unbounded memory growth over long-running streams.
    private void RunSegmentCleanup()
    {
        int totalPruned = 0;
        int totalRemoved = 0;

        for (int i = 0; i < _cellSegments.Length; i++)
        {
            var result = _cellSegments[i].Maintain(
                _config.SynapsePruneThreshold,
                _config.MinSynapsesForViableSegment);
            totalPruned += result.PrunedSynapses;
            totalRemoved += result.RemovedSegments;
        }

        Metrics.RecordCleanup(totalPruned, totalRemoved);
    }

    // --- Public accessors ---
    public HashSet<int> GetActiveCells() => new(_activeCells);
    public HashSet<int> GetWinnerCells() => new(_winnerCells);
    public HashSet<int> GetPredictiveCells() => new(_predictiveCells);

    /// Reset transient cell state for sequence boundary (e.g., between independent
    /// sequences). Clears active/winner/predictive cells and segment caches so the
    /// TM does not carry temporal context across sequence boundaries.
    /// Learned synapses and segments are preserved.
    public void Reset()
    {
        _activeCells.Clear();
        _winnerCells.Clear();
        _predictiveCells.Clear();
        _prevActiveCells.Clear();
        _prevWinnerCells.Clear();
        _prevPredictiveCells.Clear();
        _activeSegmentCache.Clear();
        _matchingSegmentCache.Clear();
    }

    /// Get the predicted columns for the NEXT timestep
    public HashSet<int> GetPredictedColumns()
        => _predictiveCells.Select(CellColumn).ToHashSet();

    /// Total segment count across all cells (for monitoring growth)
    public int TotalSegmentCount
        => _cellSegments.Sum(cs => cs.SegmentCount);

    public int TotalSynapseCount
        => _cellSegments.Sum(cs => cs.Segments.Sum(s => s.SynapseCount));

    // --- Serialization ---

    /// Serialize all mutable TM state: cell segments, current cell sets, iteration.
    public void SerializeState(BinaryWriter bw)
    {
        bw.Write(_config.ColumnCount);
        bw.Write(_config.CellsPerColumn);
        bw.Write(_iteration);

        // Cell segments
        for (int i = 0; i < _cellSegments.Length; i++)
        {
            bw.Write(_cellSegments[i].SegmentCount);
            foreach (var seg in _cellSegments[i].Segments)
                HtmSerializer.WriteSegment(bw, seg);
        }

        // Current cell state (becomes prev on next Compute call)
        WriteHashSet(bw, _activeCells);
        WriteHashSet(bw, _winnerCells);
        WriteHashSet(bw, _predictiveCells);
    }

    /// Restore TM state from serialized data. Must be called on a TM with matching config.
    public void DeserializeState(BinaryReader br)
    {
        int columnCount = br.ReadInt32();
        int cellsPerColumn = br.ReadInt32();
        if (columnCount != _config.ColumnCount || cellsPerColumn != _config.CellsPerColumn)
            throw new FormatException(
                $"TM config mismatch: expected ({_config.ColumnCount}, {_config.CellsPerColumn}), " +
                $"got ({columnCount}, {cellsPerColumn})");

        _iteration = br.ReadInt32();

        // Cell segments
        for (int i = 0; i < _cellSegments.Length; i++)
        {
            _cellSegments[i].ClearSegments();
            int segCount = br.ReadInt32();
            for (int s = 0; s < segCount; s++)
                _cellSegments[i].RestoreSegment(HtmSerializer.ReadSegment(br));
        }

        // Current cell state
        _activeCells = ReadHashSet(br);
        _winnerCells = ReadHashSet(br);
        _predictiveCells = ReadHashSet(br);
    }

    private static void WriteHashSet(BinaryWriter bw, HashSet<int> set)
    {
        bw.Write(set.Count);
        foreach (int val in set)
            bw.Write(val);
    }

    private static HashSet<int> ReadHashSet(BinaryReader br)
    {
        int count = br.ReadInt32();
        var set = new HashSet<int>(count);
        for (int i = 0; i < count; i++)
            set.Add(br.ReadInt32());
        return set;
    }
}

public record TemporalMemoryOutput
{
    public HashSet<int> ActiveCells { get; init; }
    public HashSet<int> WinnerCells { get; init; }
    public HashSet<int> PredictiveCells { get; init; }
    public int ActiveColumnCount { get; init; }
    public int BurstingColumnCount { get; init; }
    public int PredictedActiveColumnCount { get; init; }
    public float Anomaly { get; init; }
}

public sealed class TemporalMemoryMetrics
{
    private long _computeCount;
    private double _totalComputeMs;
    private float _avgAnomaly;
    private float _avgBurstFraction;
    private float _avgPredictedFraction;
    private int _lastActiveCells;
    private int _lastPredictiveCells;
    private long _totalPrunedSynapses;
    private long _totalRemovedSegments;

    public long ComputeCount => _computeCount;
    public double AvgComputeMs => _computeCount > 0 ? _totalComputeMs / _computeCount : 0;
    public float AvgAnomaly => _avgAnomaly;
    public float AvgBurstFraction => _avgBurstFraction;
    public int LastActiveCells => _lastActiveCells;
    public int LastPredictiveCells => _lastPredictiveCells;
    public long TotalPrunedSynapses => _totalPrunedSynapses;
    public long TotalRemovedSegments => _totalRemovedSegments;

    internal void RecordCompute(
        int activeCols, int burstCols, int predictedCols,
        int activeCells, int predictiveCells, float anomaly, TimeSpan elapsed)
    {
        _computeCount++;
        _totalComputeMs += elapsed.TotalMilliseconds;
        float alpha = 0.01f;
        _avgAnomaly = (1 - alpha) * _avgAnomaly + alpha * anomaly;
        float burstFrac = activeCols > 0 ? (float)burstCols / activeCols : 0;
        _avgBurstFraction = (1 - alpha) * _avgBurstFraction + alpha * burstFrac;
        _lastActiveCells = activeCells;
        _lastPredictiveCells = predictiveCells;
    }

    internal void RecordCleanup(int prunedSynapses, int removedSegments)
    {
        _totalPrunedSynapses += prunedSynapses;
        _totalRemovedSegments += removedSegments;
    }

    public override string ToString() =>
        $"TM Metrics: computes={ComputeCount}, avgMs={AvgComputeMs:F2}, " +
        $"avgAnomaly={AvgAnomaly:P1}, avgBurst={AvgBurstFraction:P1}, " +
        $"activeCells={LastActiveCells}, predictiveCells={LastPredictiveCells}, " +
        $"prunedSynapses={TotalPrunedSynapses}, removedSegments={TotalRemovedSegments}";
}


// ============================================================================
// SECTION 6: Anomaly Detection — Enhanced
// ============================================================================
// Wraps raw TM anomaly scores in a statistical model that tracks the
// distribution of recent scores and computes a tail probability.
// Enhancements: configurable distribution window, log-likelihood mode,
// anomaly score smoothing, and threshold-based alerting.
// ============================================================================

public sealed class AnomalyLikelihood
{
    private readonly int _windowSize;
    private readonly int _learningPeriod;
    private readonly int _reestimationInterval;
    private readonly Queue<float> _scoreHistory;
    private int _iteration;

    // Running statistics (Welford's online algorithm)
    private double _mean;
    private double _m2;     // Sum of squared deviations
    private double _variance;

    // Smoothed anomaly for reducing jitter
    private float _smoothedAnomaly;
    private readonly float _smoothingAlpha;

    public float LastLikelihood { get; private set; }
    public float SmoothedAnomaly => _smoothedAnomaly;

    /// <param name="reestimationInterval">
    /// How often (in iterations) to reinitialize running statistics from the
    /// score history window, preventing Welford drift over very long streams.
    /// Default: 10 * windowSize. Set to 0 to disable re-estimation.
    /// </param>
    public AnomalyLikelihood(
        int windowSize = 1000, int learningPeriod = 300,
        float smoothingAlpha = 0.1f, int reestimationInterval = 0)
    {
        _windowSize = windowSize;
        _learningPeriod = learningPeriod;
        _smoothingAlpha = smoothingAlpha;
        _reestimationInterval = reestimationInterval > 0 ? reestimationInterval : windowSize * 10;
        _scoreHistory = new Queue<float>(windowSize + 1);
    }

    /// Compute anomaly likelihood: the probability that the current score
    /// is anomalously high relative to the recent distribution.
    /// Returns [0.0, 1.0] where values near 1.0 indicate strong anomaly.
    public float Compute(float rawAnomalyScore)
    {
        _iteration++;
        _smoothedAnomaly = (1 - _smoothingAlpha) * _smoothedAnomaly
                          + _smoothingAlpha * rawAnomalyScore;

        _scoreHistory.Enqueue(rawAnomalyScore);
        if (_scoreHistory.Count > _windowSize)
        {
            float removed = _scoreHistory.Dequeue();
        }

        // Update running statistics (Welford)
        double delta = rawAnomalyScore - _mean;
        _mean += delta / Math.Min(_iteration, _windowSize);
        double delta2 = rawAnomalyScore - _mean;
        _m2 += delta * delta2;
        _variance = _iteration > 1 ? _m2 / (Math.Min(_iteration, _windowSize) - 1) : 0;

        // Periodic re-estimation: recompute mean/variance from the current score
        // history window to prevent Welford drift over very long streams.
        if (_reestimationInterval > 0
            && _iteration > _learningPeriod
            && _iteration % _reestimationInterval == 0
            && _scoreHistory.Count > 1)
        {
            ReestimateFromHistory();
        }

        // During learning period, return neutral
        if (_iteration < _learningPeriod)
            return LastLikelihood = 0.5f;

        double stdDev = Math.Sqrt(_variance);
        if (stdDev < 1e-6) return LastLikelihood = 0.5f;

        // Compute z-score and tail probability
        double z = (rawAnomalyScore - _mean) / stdDev;

        // Use complementary error function for Gaussian tail
        float likelihood = 1.0f - (float)NormalCdf(z);

        // Apply log-likelihood scaling for better discrimination at extremes
        // This maps [0, 1] → [0, 1] but stretches the high-anomaly region
        float logLikelihood = (float)(1.0 - Math.Exp(-Math.Log(1.0 / Math.Max(1e-10, 1.0 - likelihood))));

        return LastLikelihood = Math.Clamp(logLikelihood, 0f, 1f);
    }

    /// Reinitialize running statistics from the current score history window.
    /// This discards accumulated Welford state and recomputes from scratch,
    /// ensuring statistics track the recent distribution rather than lifetime history.
    private void ReestimateFromHistory()
    {
        double sum = 0;
        foreach (float score in _scoreHistory)
            sum += score;

        int n = _scoreHistory.Count;
        _mean = sum / n;

        double m2 = 0;
        foreach (float score in _scoreHistory)
        {
            double d = score - _mean;
            m2 += d * d;
        }

        _m2 = m2;
        _variance = n > 1 ? _m2 / (n - 1) : 0;
    }

    /// Standard normal CDF approximation (Abramowitz & Stegun)
    private static double NormalCdf(double z)
    {
        const double a1 = 0.254829592, a2 = -0.284496736;
        const double a3 = 1.421413741, a4 = -1.453152027;
        const double a5 = 1.061405429, p = 0.3275911;

        int sign = z < 0 ? -1 : 1;
        z = Math.Abs(z) / Math.Sqrt(2);
        double t = 1.0 / (1.0 + p * z);
        double y = 1.0 - ((((a5 * t + a4) * t + a3) * t + a2) * t + a1) * t * Math.Exp(-z * z);
        return 0.5 * (1.0 + sign * y);
    }

    /// Check if current likelihood exceeds a threshold
    public bool IsAnomaly(float threshold = 0.99f) => LastLikelihood >= threshold;
}


// ============================================================================
// SECTION 7: SDR Classifier / Predictor — Enhanced
// ============================================================================

public sealed class SdrPredictor
{
    private readonly int[] _steps;
    private readonly float _alpha;
    private readonly double _resolution;

    // Weights per step: cell → bucket → weight
    private readonly Dictionary<int, Dictionary<int, Dictionary<int, float>>> _weights = new();

    public SdrPredictor(int[] steps, float alpha = 0.001f, double resolution = 1.0)
    {
        _steps = steps;
        _alpha = alpha;
        _resolution = resolution;
        foreach (int step in steps)
            _weights[step] = new();
    }

    public void Learn(int step, HashSet<int> activeCells, double actualValue)
    {
        if (!_weights.ContainsKey(step)) return;
        int bucket = (int)(actualValue / _resolution);
        var stepWeights = _weights[step];

        foreach (int cell in activeCells)
        {
            if (!stepWeights.TryGetValue(cell, out var cellWeights))
            {
                cellWeights = new Dictionary<int, float>();
                stepWeights[cell] = cellWeights;
            }

            foreach (var b in cellWeights.Keys.ToList())
            {
                float target = b == bucket ? 1f : 0f;
                cellWeights[b] += _alpha * (target - cellWeights[b]);
            }

            if (!cellWeights.ContainsKey(bucket))
                cellWeights[bucket] = _alpha;
        }
    }

    public Dictionary<int, SdrPrediction> Infer(HashSet<int> activeCells)
    {
        var results = new Dictionary<int, SdrPrediction>();

        foreach (int step in _steps)
        {
            var distribution = new Dictionary<int, float>();
            var stepWeights = _weights[step];

            foreach (int cell in activeCells)
            {
                if (!stepWeights.TryGetValue(cell, out var cellWeights)) continue;
                foreach (var (bucket, weight) in cellWeights)
                {
                    distribution.TryGetValue(bucket, out float current);
                    distribution[bucket] = current + weight;
                }
            }

            // Normalize
            float total = distribution.Values.Sum();
            if (total > 0)
                foreach (var key in distribution.Keys.ToList())
                    distribution[key] /= total;

            double? bestValue = null;
            float bestProb = 0;
            if (distribution.Count > 0)
            {
                var best = distribution.MaxBy(kv => kv.Value);
                bestValue = best.Key * _resolution;
                bestProb = best.Value;
            }

            results[step] = new SdrPrediction
            {
                Step = step,
                BestPrediction = bestValue,
                Confidence = bestProb,
                Distribution = distribution,
            };
        }

        return results;
    }
}

public record SdrPrediction
{
    public int Step { get; init; }
    public double? BestPrediction { get; init; }
    public float Confidence { get; init; }
    public Dictionary<int, float> Distribution { get; init; }
}


// ============================================================================
// SECTION 8: Grid Cell Module
// ============================================================================
// Grid cells provide allocentric (world-centered) location reference frames.
// Each module tiles 2D space with a hexagonal grid at a specific scale and
// orientation. Multiple modules at different scales/orientations together
// provide a unique location code (like Fourier components).
//
// The grid cell representation is an SDR where the active bits encode the
// current position within the module's periodic tiling of space.
//
// Path integration: movement displaces the active bump across the grid.
// Anchoring: sensory input can reset the grid to a known position.
// ============================================================================

public sealed class GridCellModuleConfig
{
    public int ModuleSize { get; init; } = 40;          // Width of the square module
    public int CellsPerAxis => ModuleSize;
    public int TotalCells => ModuleSize * ModuleSize;
    public int ActiveCellCount { get; init; } = 10;     // Cells in the active "bump"
    public float Scale { get; init; } = 1.0f;           // Spatial period of the grid
    public float Orientation { get; init; } = 0.0f;     // Rotation of grid axes (radians)
    public float BumpSigma { get; init; } = 1.5f;       // Gaussian bump width
    public float PathIntegrationNoise { get; init; } = 0.01f;
}

public sealed class GridCellModule
{
    private readonly GridCellModuleConfig _config;
    private readonly Random _rng;

    // Current position in module-local coordinates (continuous)
    private float _posX;
    private float _posY;

    // Precomputed rotation matrix for this module's orientation
    private readonly float _cosTheta, _sinTheta;

    public int OutputSize => _config.TotalCells;

    // Learned associations: sensory feature hash → grid position
    private readonly Dictionary<int, (float X, float Y)> _anchorMemory = new();

    public GridCellModule(GridCellModuleConfig config, int seed = 42)
    {
        _config = config;
        _rng = new Random(seed);
        _cosTheta = MathF.Cos(config.Orientation);
        _sinTheta = MathF.Sin(config.Orientation);

        // Start at random position
        _posX = (float)(_rng.NextDouble() * config.ModuleSize);
        _posY = (float)(_rng.NextDouble() * config.ModuleSize);
    }

    /// Path integration: update position based on movement vector.
    /// The movement is first rotated by the module's orientation, then
    /// scaled by the module's spatial period, and wrapped toroidally.
    public void Move(float deltaX, float deltaY)
    {
        // Rotate movement into module's reference frame
        float rotX = deltaX * _cosTheta + deltaY * _sinTheta;
        float rotY = -deltaX * _sinTheta + deltaY * _cosTheta;

        // Scale by module's spatial period
        rotX /= _config.Scale;
        rotY /= _config.Scale;

        // Add path integration noise (models biological imprecision)
        rotX += (float)(_rng.NextDouble() * 2 - 1) * _config.PathIntegrationNoise;
        rotY += (float)(_rng.NextDouble() * 2 - 1) * _config.PathIntegrationNoise;

        // Update position with toroidal wrapping
        _posX = Mod(_posX + rotX, _config.ModuleSize);
        _posY = Mod(_posY + rotY, _config.ModuleSize);
    }

    /// Compute the current grid cell SDR: a Gaussian bump centered
    /// at the current position on the toroidal grid surface.
    public SDR GetCurrentLocation()
    {
        var activations = new List<(int Index, float Weight)>();
        float sigma2 = _config.BumpSigma * _config.BumpSigma;

        for (int gx = 0; gx < _config.ModuleSize; gx++)
        {
            for (int gy = 0; gy < _config.ModuleSize; gy++)
            {
                // Toroidal distance from current position to grid cell
                float dx = ToroidalDist(gx - _posX, _config.ModuleSize);
                float dy = ToroidalDist(gy - _posY, _config.ModuleSize);
                float dist2 = dx * dx + dy * dy;

                float activation = MathF.Exp(-dist2 / (2 * sigma2));
                if (activation > 0.01f) // Threshold for sparsity
                    activations.Add((gx * _config.ModuleSize + gy, activation));
            }
        }

        // Select top-k as active cells
        var active = activations
            .OrderByDescending(a => a.Weight)
            .Take(_config.ActiveCellCount)
            .Select(a => a.Index);

        return new SDR(_config.TotalCells, active);
    }

    /// Anchor the grid to a known position based on a sensory input.
    /// This corrects accumulated path integration drift.
    public void Anchor(SDR sensoryInput)
    {
        int hash = sensoryInput.GetHashCode();

        if (_anchorMemory.TryGetValue(hash, out var remembered))
        {
            // Snap to remembered position (error correction)
            _posX = remembered.X;
            _posY = remembered.Y;
        }
        else
        {
            // Learn this sensory input → position association
            _anchorMemory[hash] = (_posX, _posY);
        }
    }

    /// Reset to a specific position (for testing / initialization)
    public void SetPosition(float x, float y)
    {
        _posX = Mod(x, _config.ModuleSize);
        _posY = Mod(y, _config.ModuleSize);
    }

    public (float X, float Y) GetPosition() => (_posX, _posY);

    private static float Mod(float a, float m) => ((a % m) + m) % m;

    private static float ToroidalDist(float d, float size)
    {
        d = Mod(d, size);
        return d > size / 2 ? d - size : d;
    }
}


// ============================================================================
// SECTION 9: Displacement Cells
// ============================================================================
// Displacement cells encode the relative offset between two locations,
// enabling the system to represent object-relative (allocentric) structure.
// For example: "the handle is 5cm to the right of the cup's center."
//
// They compute the displacement vector between a "source" grid cell
// location and a "target" grid cell location, producing an SDR that
// encodes that relative position. This is key to learning object structure
// across multiple touch points / sensory features.
// ============================================================================

public sealed class DisplacementCellModule
{
    private readonly int _size;            // Module width (square grid of cells)
    private readonly int _activeCells;     // Active bump size
    private readonly float _sigma;         // Bump width

    public int OutputSize => _size * _size;

    public DisplacementCellModule(int size = 40, int activeCells = 10, float sigma = 1.5f)
    {
        _size = size;
        _activeCells = activeCells;
        _sigma = sigma;
    }

    /// Compute the displacement SDR between two grid cell locations.
    /// The displacement is the vector difference, wrapped toroidally.
    public SDR ComputeDisplacement(SDR sourceLocation, SDR targetLocation)
    {
        // Decode centroid positions from the SDR activations
        var sourceCentroid = DecodeCentroid(sourceLocation);
        var targetCentroid = DecodeCentroid(targetLocation);

        // Compute displacement vector
        float dx = ToroidalDist(targetCentroid.X - sourceCentroid.X, _size);
        float dy = ToroidalDist(targetCentroid.Y - sourceCentroid.Y, _size);

        // Encode displacement as a bump at the displacement position
        return EncodeBump(dx + _size / 2f, dy + _size / 2f);
    }

    /// Given a known displacement and a source location, predict the target location
    public SDR PredictTarget(SDR sourceLocation, SDR displacement)
    {
        var sourceCentroid = DecodeCentroid(sourceLocation);
        var dispCentroid = DecodeCentroid(displacement);

        // Displacement is encoded centered at (size/2, size/2)
        float dx = dispCentroid.X - _size / 2f;
        float dy = dispCentroid.Y - _size / 2f;

        return EncodeBump(
            Mod(sourceCentroid.X + dx, _size),
            Mod(sourceCentroid.Y + dy, _size));
    }

    /// Given a known displacement and a target location, predict the source location
    public SDR PredictSource(SDR targetLocation, SDR displacement)
    {
        var targetCentroid = DecodeCentroid(targetLocation);
        var dispCentroid = DecodeCentroid(displacement);

        float dx = dispCentroid.X - _size / 2f;
        float dy = dispCentroid.Y - _size / 2f;

        return EncodeBump(
            Mod(targetCentroid.X - dx, _size),
            Mod(targetCentroid.Y - dy, _size));
    }

    private SDR EncodeBump(float centerX, float centerY)
    {
        var activations = new List<(int Index, float Weight)>();
        float sigma2 = _sigma * _sigma;

        for (int gx = 0; gx < _size; gx++)
        {
            for (int gy = 0; gy < _size; gy++)
            {
                float dx = ToroidalDist(gx - centerX, _size);
                float dy = ToroidalDist(gy - centerY, _size);
                float activation = MathF.Exp(-(dx * dx + dy * dy) / (2 * sigma2));
                if (activation > 0.01f)
                    activations.Add((gx * _size + gy, activation));
            }
        }

        var active = activations
            .OrderByDescending(a => a.Weight)
            .Take(_activeCells)
            .Select(a => a.Index);

        return new SDR(OutputSize, active);
    }

    private (float X, float Y) DecodeCentroid(SDR sdr)
    {
        if (sdr.ActiveCount == 0) return (0, 0);

        // Circular mean to handle toroidal wraparound
        double sinSumX = 0, cosSumX = 0;
        double sinSumY = 0, cosSumY = 0;

        foreach (int bit in sdr.ActiveBits)
        {
            int gx = bit / _size;
            int gy = bit % _size;
            double angleX = 2 * Math.PI * gx / _size;
            double angleY = 2 * Math.PI * gy / _size;
            sinSumX += Math.Sin(angleX); cosSumX += Math.Cos(angleX);
            sinSumY += Math.Sin(angleY); cosSumY += Math.Cos(angleY);
        }

        float x = (float)(Math.Atan2(sinSumX, cosSumX) / (2 * Math.PI) * _size);
        float y = (float)(Math.Atan2(sinSumY, cosSumY) / (2 * Math.PI) * _size);
        return (Mod(x, _size), Mod(y, _size));
    }

    private static float Mod(float a, float m) => ((a % m) + m) % m;
    private static float ToroidalDist(float d, float size)
    {
        d = Mod(d, size);
        return d > size / 2 ? d - size : d;
    }
}


// ============================================================================
// SECTION 10: Cortical Column
// ============================================================================
// A cortical column is the fundamental processing unit of the Thousand Brains
// architecture. Each column:
//   1. Receives a sensory feature (from its patch of the sensor array)
//   2. Receives a location signal (from its associated grid cell module)
//   3. Combines feature + location to form a "feature-at-location" representation
//   4. Uses temporal memory to learn sequences of feature-at-locations
//   5. Maintains an object representation that converges through lateral voting
//
// The column learns "this feature at this location → this object" associations.
// Multiple columns viewing different parts of an object vote to reach consensus.
// ============================================================================

public sealed class CorticalColumnConfig
{
    public int InputSize { get; init; } = 512;
    public int LocationSize { get; init; } = 1600;     // Grid cell module output size
    public int ColumnCount { get; init; } = 1024;
    public int CellsPerColumn { get; init; } = 16;
    public int ObjectRepresentationSize { get; init; } = 2048;
    public int ObjectActiveBits { get; init; } = 40;
    public float LateralInfluence { get; init; } = 0.3f; // How much lateral input affects state
}

public sealed class CorticalColumn
{
    private readonly CorticalColumnConfig _config;
    private readonly SpatialPooler _featureSP;
    private readonly TemporalMemory _sequenceTM;
    private readonly Random _rng;

    // Object layer: maps (feature SDR, location SDR) → object SDR
    // This is a simplified associative memory
    private readonly Dictionary<int, HashSet<int>> _objectMemory = new();

    // Current state
    private SDR _currentObjectRepresentation;
    private SDR _lastSensoryInput;
    private SDR _lastLocationInput;
    private float _confidence;

    // Pool of known object representations
    private readonly List<(string Label, SDR Representation)> _knownObjects = new();

    public CorticalColumn(CorticalColumnConfig config, int seed = 42)
    {
        _config = config;
        _rng = new Random(seed);

        _featureSP = new SpatialPooler(new SpatialPoolerConfig
        {
            InputSize = config.InputSize + config.LocationSize, // Combined input
            ColumnCount = config.ColumnCount,
            TargetSparsity = 0.02f,
            Inhibition = InhibitionMode.Global,
        });

        _sequenceTM = new TemporalMemory(new TemporalMemoryConfig
        {
            ColumnCount = config.ColumnCount,
            CellsPerColumn = config.CellsPerColumn,
            MaxSegmentsPerCell = 64,
            MaxSynapsesPerSegment = 32,
        });

        _currentObjectRepresentation = new SDR(config.ObjectRepresentationSize);
        _lastSensoryInput = new SDR(config.InputSize);
        _lastLocationInput = new SDR(config.LocationSize);
    }

    /// Process one sensory observation with its associated location.
    public CorticalColumnOutput Compute(SDR sensoryInput, SDR locationInput, bool learn = true)
    {
        _lastSensoryInput = sensoryInput;
        _lastLocationInput = locationInput;

        // Step 1: Combine sensory feature and location into a single input
        var combinedInput = CombineFeatureAndLocation(sensoryInput, locationInput);

        // Step 2: Spatial Pooler — create sparse representation of feature-at-location
        var activeColumns = _featureSP.Compute(combinedInput, learn);

        // Step 3: Temporal Memory — add sequence context
        var tmOutput = _sequenceTM.Compute(activeColumns, learn);

        // Step 4: Generate object representation from TM cell activity
        // Hash the active cells to produce a compact object-layer representation
        var objectSDR = ProjectToObjectLayer(tmOutput.ActiveCells);

        // Step 5: Update running object representation (accumulate evidence)
        _currentObjectRepresentation = AccumulateObjectEvidence(
            _currentObjectRepresentation, objectSDR);

        // Step 6: Learn feature-at-location → object association
        if (learn)
        {
            int featureLocationHash = HashCode.Combine(
                sensoryInput.GetHashCode(), locationInput.GetHashCode());
            _objectMemory[featureLocationHash] = new HashSet<int>(
                _currentObjectRepresentation.ActiveBits.ToArray());
        }

        _confidence = 1.0f - tmOutput.Anomaly;

        return new CorticalColumnOutput
        {
            ActiveCells = tmOutput.ActiveCells,
            PredictedCells = tmOutput.PredictiveCells,
            ObjectRepresentation = _currentObjectRepresentation,
            Anomaly = tmOutput.Anomaly,
            Confidence = _confidence,
        };
    }

    /// Incorporate lateral input from other columns (voting)
    public void ReceiveLateralInput(SDR consensusRepresentation)
    {
        // Narrow the current object representation toward consensus
        // by intersecting or blending with the incoming signal
        if (consensusRepresentation.ActiveCount == 0) return;

        int overlap = _currentObjectRepresentation.Overlap(consensusRepresentation);
        float similarity = _currentObjectRepresentation.MatchScore(consensusRepresentation);

        if (similarity > 0.3f)
        {
            // Strong agreement: move toward consensus
            _currentObjectRepresentation = BlendSDRs(
                _currentObjectRepresentation, consensusRepresentation,
                _config.LateralInfluence);
        }
        else if (similarity < 0.1f && _confidence < 0.5f)
        {
            // Low confidence + low agreement: reset to consensus
            _currentObjectRepresentation = consensusRepresentation;
        }
    }

    /// Reset object evidence (e.g., when starting to explore a new object)
    public void ResetObjectRepresentation()
    {
        _currentObjectRepresentation = new SDR(_config.ObjectRepresentationSize);
        _confidence = 0;
    }

    public SDR GetObjectRepresentation() => _currentObjectRepresentation;

    // --- Internals ---

    private SDR CombineFeatureAndLocation(SDR feature, SDR location)
    {
        int totalSize = _config.InputSize + _config.LocationSize;
        var combined = new List<int>(feature.ActiveBits.ToArray());
        foreach (int bit in location.ActiveBits)
            combined.Add(bit + _config.InputSize);
        return new SDR(totalSize, combined);
    }

    /// Project TM cell activity into the object representation space
    /// using a deterministic hash-based projection
    private SDR ProjectToObjectLayer(HashSet<int> activeCells)
    {
        var objectBits = new HashSet<int>();
        foreach (int cell in activeCells)
        {
            // Hash each active cell to 2-3 bits in object space
            int hash = HashCode.Combine(cell, 0xDEADBEEF);
            objectBits.Add(((hash & 0x7FFFFFFF) % _config.ObjectRepresentationSize));
            hash = HashCode.Combine(cell, 0xCAFEBABE);
            objectBits.Add(((hash & 0x7FFFFFFF) % _config.ObjectRepresentationSize));
        }

        // Subsample to maintain target sparsity
        if (objectBits.Count > _config.ObjectActiveBits)
        {
            var sampled = objectBits.OrderBy(_ => _rng.Next()).Take(_config.ObjectActiveBits);
            return new SDR(_config.ObjectRepresentationSize, sampled);
        }

        return new SDR(_config.ObjectRepresentationSize, objectBits);
    }

    /// Accumulate object evidence: blend previous representation with new observation
    private SDR AccumulateObjectEvidence(SDR previous, SDR current)
    {
        if (previous.ActiveCount == 0) return current;

        // Keep bits that are supported by EITHER previous or current evidence,
        // with preference for bits supported by both (intersection-boosted union)
        var intersection = previous.Intersect(current);
        var union = previous.Union(current);

        if (union.ActiveCount <= _config.ObjectActiveBits)
            return union;

        // Prioritize intersection bits, fill remainder from union
        var result = new HashSet<int>(intersection.ActiveBits.ToArray());
        foreach (int bit in union.ActiveBits)
        {
            if (result.Count >= _config.ObjectActiveBits) break;
            result.Add(bit);
        }

        return new SDR(_config.ObjectRepresentationSize, result);
    }

    /// Blend two SDRs: randomly select bits from each based on blendFactor
    private SDR BlendSDRs(SDR a, SDR b, float bWeight)
    {
        var result = new HashSet<int>();
        int targetBits = Math.Max(a.ActiveCount, b.ActiveCount);

        // Take (1-bWeight) fraction from a, bWeight fraction from b
        int fromB = (int)(targetBits * bWeight);
        int fromA = targetBits - fromB;

        result.UnionWith(a.ActiveBits.ToArray().OrderBy(_ => _rng.Next()).Take(fromA));
        result.UnionWith(b.ActiveBits.ToArray().OrderBy(_ => _rng.Next()).Take(fromB));

        return new SDR(a.Size, result);
    }
}

public record CorticalColumnOutput
{
    public HashSet<int> ActiveCells { get; init; }
    public HashSet<int> PredictedCells { get; init; }
    public SDR ObjectRepresentation { get; init; }
    public float Anomaly { get; init; }
    public float Confidence { get; init; }
}


// ============================================================================
// SECTION 11: Lateral Voting — Consensus Mechanism
// ============================================================================
// In the Thousand Brains Theory, multiple cortical columns simultaneously
// process different sensory patches of the same object. They exchange
// "votes" (their current object representations) via lateral connections
// and converge on a shared object identity.
//
// The voting mechanism:
//   1. Each column proposes its current object representation SDR
//   2. Votes are aggregated: bits that appear in many columns get high scores
//   3. A threshold selects the consensus bits
//   4. Convergence is detected when columns agree sufficiently
// ============================================================================

public sealed class LateralVotingConfig
{
    public float ConvergenceThreshold { get; init; } = 0.7f;    // Min avg pairwise overlap
    public float VoteThreshold { get; init; } = 0.3f;           // Fraction of columns that must agree on a bit
    public int MaxIterations { get; init; } = 10;
    public int OutputActiveBits { get; init; } = 40;
}

public sealed class LateralVotingMechanism
{
    private readonly LateralVotingConfig _config;

    public LateralVotingMechanism(LateralVotingConfig? config = null)
    {
        _config = config ?? new LateralVotingConfig();
    }

    /// Compute consensus SDR from multiple column votes.
    /// Each column votes with its current object representation.
    /// Bits that appear in >= VoteThreshold fraction of columns survive.
    public SDR ComputeConsensus(IReadOnlyList<SDR> columnVotes)
    {
        if (columnVotes.Count == 0) return new SDR(0);
        int sdrSize = columnVotes[0].Size;
        int numColumns = columnVotes.Count;

        // Tally: count how many columns vote for each bit
        var bitCounts = new int[sdrSize];
        foreach (var vote in columnVotes)
            foreach (int bit in vote.ActiveBits)
                bitCounts[bit]++;

        // Select bits that meet the vote threshold
        float threshold = _config.VoteThreshold * numColumns;
        var consensusBits = new List<(int Bit, int Count)>();

        for (int i = 0; i < sdrSize; i++)
        {
            if (bitCounts[i] >= threshold)
                consensusBits.Add((i, bitCounts[i]));
        }

        // If too many bits survive, keep only the most-voted
        if (consensusBits.Count > _config.OutputActiveBits)
        {
            consensusBits = consensusBits
                .OrderByDescending(b => b.Count)
                .Take(_config.OutputActiveBits)
                .ToList();
        }

        return new SDR(sdrSize, consensusBits.Select(b => b.Bit));
    }

    /// Check if columns have converged to a consensus.
    /// Convergence = average pairwise overlap is above threshold.
    public bool HasConverged(IReadOnlyList<SDR> columnVotes)
    {
        if (columnVotes.Count < 2) return true;

        float totalSimilarity = 0;
        int pairs = 0;

        for (int i = 0; i < columnVotes.Count; i++)
        {
            for (int j = i + 1; j < columnVotes.Count; j++)
            {
                totalSimilarity += columnVotes[i].MatchScore(columnVotes[j]);
                pairs++;
            }
        }

        float avgSimilarity = pairs > 0 ? totalSimilarity / pairs : 0;
        return avgSimilarity >= _config.ConvergenceThreshold;
    }

    /// Run iterative voting until convergence or max iterations.
    /// Each iteration: compute consensus → feed back to columns → re-vote.
    public (SDR Consensus, int Iterations, bool Converged) RunVotingLoop(
        IReadOnlyList<CorticalColumn> columns,
        Func<CorticalColumn, SDR> getVote)
    {
        for (int iter = 0; iter < _config.MaxIterations; iter++)
        {
            var votes = columns.Select(getVote).ToList();

            if (HasConverged(votes))
                return (ComputeConsensus(votes), iter + 1, true);

            // Feed consensus back to columns
            var consensus = ComputeConsensus(votes);
            foreach (var column in columns)
                column.ReceiveLateralInput(consensus);
        }

        var finalVotes = columns.Select(getVote).ToList();
        return (ComputeConsensus(finalVotes), _config.MaxIterations, false);
    }
}


// ============================================================================
// SECTION 12: Thousand Brains Engine
// ============================================================================
// The complete Thousand Brains architecture. Orchestrates multiple cortical
// columns, each with its own grid cell module and sensory input, plus
// lateral voting for consensus-based object recognition.
//
// Object learning: explore an object, accumulate evidence, converge, label.
// Object recognition: observe features at locations, vote, converge to ID.
// ============================================================================

public sealed class ThousandBrainsConfig
{
    public int ColumnCount { get; init; } = 8;
    public CorticalColumnConfig ColumnConfig { get; init; } = new();
    public GridCellModuleConfig[] GridModuleConfigs { get; init; }
    public LateralVotingConfig VotingConfig { get; init; } = new();
    public bool UseDisplacementCells { get; init; } = true;
    public int DisplacementModuleSize { get; init; } = 40;
}

public sealed class ThousandBrainsEngine
{
    private readonly ThousandBrainsConfig _config;
    private readonly CorticalColumn[] _columns;
    private readonly GridCellModule[] _gridModules;
    private readonly DisplacementCellModule? _displacementModule;
    private readonly LateralVotingMechanism _voting;

    // Known objects for recognition
    private readonly Dictionary<string, SDR> _objectLibrary = new();
    // Learned object structure: object label → list of (displacement SDR) for predicting next location
    private readonly Dictionary<string, List<SDR>> _objectDisplacements = new();
    // Current object's displacement sequence being accumulated during learning
    private readonly List<SDR> _currentDisplacements = new();
    // Previous grid cell locations for displacement computation
    private SDR[]? _prevGridLocations;
    private int _explorationSteps;

    public ThousandBrainsEngine(ThousandBrainsConfig config)
    {
        _config = config;
        _voting = new LateralVotingMechanism(config.VotingConfig);

        // Create grid cell modules (one per column, varying scale/orientation)
        var defaultGridConfigs = Enumerable.Range(0, config.ColumnCount)
            .Select(i => new GridCellModuleConfig
            {
                Scale = 1.0f + i * 0.5f,     // Increasing scales
                Orientation = i * MathF.PI / config.ColumnCount, // Spread orientations
            }).ToArray();

        var gridConfigs = config.GridModuleConfigs ?? defaultGridConfigs;

        _gridModules = new GridCellModule[config.ColumnCount];
        _columns = new CorticalColumn[config.ColumnCount];

        for (int i = 0; i < config.ColumnCount; i++)
        {
            _gridModules[i] = new GridCellModule(
                gridConfigs[i % gridConfigs.Length], seed: 42 + i);
            _columns[i] = new CorticalColumn(config.ColumnConfig, seed: 100 + i);
        }

        if (config.UseDisplacementCells)
        {
            _displacementModule = new DisplacementCellModule(
                config.DisplacementModuleSize);
        }
    }

    /// Process one sensory observation during exploration or recognition.
    /// Each column receives its local sensory patch.
    /// Grid cells update via path integration from the movement vector.
    public ThousandBrainsOutput Process(
        SDR[] sensoryPatches, float moveDeltaX, float moveDeltaY,
        bool learn = true)
    {
        _explorationSteps++;

        // 1. Update grid cell locations via path integration
        foreach (var module in _gridModules)
            module.Move(moveDeltaX, moveDeltaY);

        // 2. Each column processes its sensory patch with location
        var columnOutputs = new CorticalColumnOutput[_config.ColumnCount];
        for (int i = 0; i < _config.ColumnCount; i++)
        {
            var locationSDR = _gridModules[i].GetCurrentLocation();
            columnOutputs[i] = _columns[i].Compute(
                sensoryPatches[i % sensoryPatches.Length],
                locationSDR,
                learn);
        }

        // 3. Lateral voting: columns exchange representations and converge
        var (consensus, iterations, converged) = _voting.RunVotingLoop(
            _columns,
            col => col.GetObjectRepresentation());

        // 4. Attempt recognition against known objects
        string? recognizedObject = null;
        float bestMatch = 0;

        foreach (var (label, objSDR) in _objectLibrary)
        {
            float match = consensus.MatchScore(objSDR);
            if (match > bestMatch && match > 0.5f)
            {
                bestMatch = match;
                recognizedObject = label;
            }
        }

        // 5. Compute displacement predictions if enabled
        SDR? predictedNextLocation = null;
        if (_displacementModule != null && _explorationSteps > 1 && _prevGridLocations != null)
        {
            var currentLoc = _gridModules[0].GetCurrentLocation();
            var prevLoc = _prevGridLocations[0];

            // Compute the displacement from previous to current location
            var displacement = _displacementModule.ComputeDisplacement(prevLoc, currentLoc);

            if (learn)
            {
                // During learning: accumulate displacements for the current object
                _currentDisplacements.Add(displacement);
            }

            if (recognizedObject != null
                && _objectDisplacements.TryGetValue(recognizedObject, out var learnedDisps)
                && learnedDisps.Count > 0)
            {
                // During recognition: use the most recently matched displacement
                // to predict the next location based on learned object structure
                int nextIdx = (_explorationSteps - 1) % learnedDisps.Count;
                predictedNextLocation = _displacementModule.PredictTarget(
                    currentLoc, learnedDisps[nextIdx]);
            }
        }

        // Save current grid locations for next step's displacement computation
        _prevGridLocations = new SDR[_config.ColumnCount];
        for (int i = 0; i < _config.ColumnCount; i++)
            _prevGridLocations[i] = _gridModules[i].GetCurrentLocation();

        return new ThousandBrainsOutput
        {
            Consensus = consensus,
            Converged = converged,
            VotingIterations = iterations,
            RecognizedObject = recognizedObject,
            RecognitionConfidence = bestMatch,
            ColumnOutputs = columnOutputs,
            AvgAnomaly = columnOutputs.Average(o => o.Anomaly),
            PredictedNextLocation = predictedNextLocation,
        };
    }

    /// Label the current accumulated representation as a known object
    public void LearnObject(string label)
    {
        var votes = _columns.Select(c => c.GetObjectRepresentation()).ToList();
        var consensus = _voting.ComputeConsensus(votes);
        _objectLibrary[label] = consensus;

        // Store learned displacement sequence for this object
        if (_displacementModule != null && _currentDisplacements.Count > 0)
            _objectDisplacements[label] = new List<SDR>(_currentDisplacements);
    }

    /// Reset all columns for exploring a new object
    public void StartNewObject()
    {
        _explorationSteps = 0;
        _prevGridLocations = null;
        _currentDisplacements.Clear();
        foreach (var column in _columns)
            column.ResetObjectRepresentation();
    }

    /// Anchor grid cells at the current position based on a landmark
    public void AnchorAtLandmark(SDR landmark)
    {
        foreach (var module in _gridModules)
            module.Anchor(landmark);
    }

    public IReadOnlyDictionary<string, SDR> GetObjectLibrary() => _objectLibrary;
}

public record ThousandBrainsOutput
{
    public SDR Consensus { get; init; }
    public bool Converged { get; init; }
    public int VotingIterations { get; init; }
    public string? RecognizedObject { get; init; }
    public float RecognitionConfidence { get; init; }
    public CorticalColumnOutput[] ColumnOutputs { get; init; }
    public float AvgAnomaly { get; init; }
    public SDR? PredictedNextLocation { get; init; }
}


// ============================================================================
// SECTION 13: NetworkAPI — Region-Based Computation Graph
// ============================================================================
// The NetworkAPI provides a higher-level abstraction for wiring HTM
// components into a directed computation graph. Each node ("region")
// wraps an algorithm (SP, TM, encoder, classifier) and exposes named
// input/output ports. Links connect output ports to input ports.
//
// This enables:
//   - Declarative pipeline construction
//   - Multi-region hierarchical architectures
//   - Easy swapping of algorithm implementations
//   - Serialization of the full topology + state
// ============================================================================

/// Named input/output port for a region
public enum PortType { SDR, Scalar, CellActivity, Anomaly }

public record RegionPort(string Name, PortType Type);

/// Base interface for all computable regions
public interface IRegion
{
    string Name { get; }
    IReadOnlyList<RegionPort> InputPorts { get; }
    IReadOnlyList<RegionPort> OutputPorts { get; }

    void SetInput(string portName, object value);
    object GetOutput(string portName);
    void Compute(bool learn = true);
    void Reset();

    // Serialization
    byte[] Serialize();
    void Deserialize(byte[] data);
}

/// Link between two region ports
public record RegionLink(
    string SourceRegion, string SourcePort,
    string TargetRegion, string TargetPort);

/// Wraps the Spatial Pooler as a NetworkAPI region
public sealed class SPRegion : IRegion
{
    public string Name { get; }
    private readonly SpatialPoolerConfig _config;
    private readonly SpatialPooler _sp;
    private SDR _input;
    private SDR _output;

    public IReadOnlyList<RegionPort> InputPorts { get; } = new[]
    {
        new RegionPort("bottomUpIn", PortType.SDR),
    };

    public IReadOnlyList<RegionPort> OutputPorts { get; } = new[]
    {
        new RegionPort("bottomUpOut", PortType.SDR),
    };

    public SPRegion(string name, SpatialPoolerConfig config)
    {
        Name = name;
        _config = config;
        _sp = new SpatialPooler(config);
    }

    public void SetInput(string portName, object value)
    {
        if (portName == "bottomUpIn") _input = (SDR)value;
    }

    public object GetOutput(string portName) => portName switch
    {
        "bottomUpOut" => _output,
        _ => throw new ArgumentException($"Unknown port: {portName}"),
    };

    public void Compute(bool learn = true)
        => _output = _sp.Compute(_input, learn);

    /// SP has no temporal state to reset. Duty cycles and boost factors are
    /// long-term learning statistics that should persist across sequences.
    public void Reset() { }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteSPConfig(bw, _config);
        _sp.SerializeState(bw);
        return ms.ToArray();
    }

    public void Deserialize(byte[] data)
    {
        if (data.Length == 0) return;
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        ReadSPConfig(br); // Skip config (already constructed with matching config)
        _sp.DeserializeState(br);
    }

    /// Reconstruct an SPRegion from a serialized blob (used by LoadNetwork)
    public static SPRegion CreateFromData(string name, byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        var config = ReadSPConfig(br);
        var region = new SPRegion(name, config);
        region._sp.DeserializeState(br);
        return region;
    }

    internal static void WriteSPConfig(BinaryWriter bw, SpatialPoolerConfig c)
    {
        bw.Write(c.InputSize);
        bw.Write(c.ColumnCount);
        bw.Write(c.TargetSparsity);
        bw.Write(c.ConnectedThreshold);
        bw.Write(c.PermanenceIncrement);
        bw.Write(c.PermanenceDecrement);
        bw.Write(c.StimulusThreshold);
        bw.Write(c.PotentialRadius);
        bw.Write(c.PotentialPct);
        bw.Write((int)c.Inhibition);
        bw.Write(c.InhibitionRadius);
        bw.Write((int)c.Boosting);
        bw.Write(c.BoostStrength);
        bw.Write(c.MinPctOverlapDutyCycles);
        bw.Write(c.DutyCyclePeriod);
        bw.Write(c.Seed);
    }

    internal static SpatialPoolerConfig ReadSPConfig(BinaryReader br) => new()
    {
        InputSize = br.ReadInt32(),
        ColumnCount = br.ReadInt32(),
        TargetSparsity = br.ReadSingle(),
        ConnectedThreshold = br.ReadSingle(),
        PermanenceIncrement = br.ReadSingle(),
        PermanenceDecrement = br.ReadSingle(),
        StimulusThreshold = br.ReadSingle(),
        PotentialRadius = br.ReadInt32(),
        PotentialPct = br.ReadSingle(),
        Inhibition = (InhibitionMode)br.ReadInt32(),
        InhibitionRadius = br.ReadInt32(),
        Boosting = (BoostingStrategy)br.ReadInt32(),
        BoostStrength = br.ReadSingle(),
        MinPctOverlapDutyCycles = br.ReadSingle(),
        DutyCyclePeriod = br.ReadInt32(),
        Seed = br.ReadInt32(),
    };
}

/// Wraps the Temporal Memory as a NetworkAPI region
public sealed class TMRegion : IRegion
{
    public string Name { get; }
    private readonly TemporalMemoryConfig _config;
    private readonly TemporalMemory _tm;
    private SDR _input;
    private TemporalMemoryOutput _lastOutput;

    public IReadOnlyList<RegionPort> InputPorts { get; } = new[]
    {
        new RegionPort("bottomUpIn", PortType.SDR),
    };

    public IReadOnlyList<RegionPort> OutputPorts { get; } = new[]
    {
        new RegionPort("activeCells", PortType.CellActivity),
        new RegionPort("predictiveCells", PortType.CellActivity),
        new RegionPort("anomaly", PortType.Anomaly),
    };

    public TMRegion(string name, TemporalMemoryConfig config)
    {
        Name = name;
        _config = config;
        _tm = new TemporalMemory(config);
    }

    public void SetInput(string portName, object value)
    {
        if (portName == "bottomUpIn") _input = (SDR)value;
    }

    public object GetOutput(string portName) => portName switch
    {
        "activeCells" => _lastOutput?.ActiveCells ?? new HashSet<int>(),
        "predictiveCells" => _lastOutput?.PredictiveCells ?? new HashSet<int>(),
        "anomaly" => _lastOutput?.Anomaly ?? 0f,
        _ => throw new ArgumentException($"Unknown port: {portName}"),
    };

    public void Compute(bool learn = true)
        => _lastOutput = _tm.Compute(_input, learn);

    /// Clear TM cell state for sequence boundaries. Preserves learned synapses.
    public void Reset()
    {
        _tm.Reset();
        _lastOutput = null;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        WriteTMConfig(bw, _config);
        _tm.SerializeState(bw);
        return ms.ToArray();
    }

    public void Deserialize(byte[] data)
    {
        if (data.Length == 0) return;
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        ReadTMConfig(br); // Skip config (already constructed with matching config)
        _tm.DeserializeState(br);
    }

    /// Reconstruct a TMRegion from a serialized blob (used by LoadNetwork)
    public static TMRegion CreateFromData(string name, byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        var config = ReadTMConfig(br);
        var region = new TMRegion(name, config);
        region._tm.DeserializeState(br);
        return region;
    }

    internal static void WriteTMConfig(BinaryWriter bw, TemporalMemoryConfig c)
    {
        bw.Write(c.ColumnCount);
        bw.Write(c.CellsPerColumn);
        bw.Write(c.ActivationThreshold);
        bw.Write(c.MinThreshold);
        bw.Write(c.MaxNewSynapseCount);
        bw.Write(c.MaxSegmentsPerCell);
        bw.Write(c.MaxSynapsesPerSegment);
        bw.Write(c.ConnectedThreshold);
        bw.Write(c.InitialPermanence);
        bw.Write(c.PermanenceIncrement);
        bw.Write(c.PermanenceDecrement);
        bw.Write(c.PredictedDecrement);
        bw.Write(c.SynapsePruneThreshold);
        bw.Write(c.SegmentCleanupInterval);
        bw.Write(c.MinSynapsesForViableSegment);
        bw.Write(c.Seed);
    }

    internal static TemporalMemoryConfig ReadTMConfig(BinaryReader br) => new()
    {
        ColumnCount = br.ReadInt32(),
        CellsPerColumn = br.ReadInt32(),
        ActivationThreshold = br.ReadInt32(),
        MinThreshold = br.ReadInt32(),
        MaxNewSynapseCount = br.ReadInt32(),
        MaxSegmentsPerCell = br.ReadInt32(),
        MaxSynapsesPerSegment = br.ReadInt32(),
        ConnectedThreshold = br.ReadSingle(),
        InitialPermanence = br.ReadSingle(),
        PermanenceIncrement = br.ReadSingle(),
        PermanenceDecrement = br.ReadSingle(),
        PredictedDecrement = br.ReadSingle(),
        SynapsePruneThreshold = br.ReadSingle(),
        SegmentCleanupInterval = br.ReadInt32(),
        MinSynapsesForViableSegment = br.ReadInt32(),
        Seed = br.ReadInt32(),
    };
}

/// The NetworkAPI computation graph: manages regions, links, and execution order
public sealed class Network
{
    private readonly Dictionary<string, IRegion> _regions = new();
    private readonly List<RegionLink> _links = new();
    private List<string>? _executionOrder;  // Topologically sorted

    public IReadOnlyDictionary<string, IRegion> Regions => _regions;
    public IReadOnlyList<RegionLink> Links => _links;

    public Network AddRegion(IRegion region)
    {
        _regions[region.Name] = region;
        _executionOrder = null; // Invalidate
        return this;
    }

    public Network Link(
        string sourceRegion, string sourcePort,
        string targetRegion, string targetPort)
    {
        _links.Add(new RegionLink(sourceRegion, sourcePort, targetRegion, targetPort));
        _executionOrder = null;
        return this;
    }

    /// Compute one timestep: propagate data through all regions in topological order
    public void Compute(bool learn = true)
    {
        _executionOrder ??= TopologicalSort();

        foreach (string regionName in _executionOrder)
        {
            var region = _regions[regionName];

            // Gather inputs from upstream regions
            foreach (var link in _links.Where(l => l.TargetRegion == regionName))
            {
                var sourceRegion = _regions[link.SourceRegion];
                var value = sourceRegion.GetOutput(link.SourcePort);
                region.SetInput(link.TargetPort, value);
            }

            region.Compute(learn);
        }
    }

    public void SetInput(string regionName, string portName, object value)
        => _regions[regionName].SetInput(portName, value);

    public object GetOutput(string regionName, string portName)
        => _regions[regionName].GetOutput(portName);

    public void Reset()
    {
        foreach (var region in _regions.Values) region.Reset();
    }

    /// Topological sort of regions based on link dependencies
    private List<string> TopologicalSort()
    {
        var inDegree = _regions.Keys.ToDictionary(k => k, _ => 0);
        var adjacency = _regions.Keys.ToDictionary(k => k, _ => new List<string>());

        foreach (var link in _links)
        {
            adjacency[link.SourceRegion].Add(link.TargetRegion);
            inDegree[link.TargetRegion]++;
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = new List<string>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);

            foreach (var neighbor in adjacency[current])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        if (sorted.Count != _regions.Count)
            throw new InvalidOperationException("Cycle detected in network graph");

        return sorted;
    }

    /// Build a standard SP → TM pipeline (convenience factory)
    public static Network CreateStandardPipeline(
        string name, int inputSize, int columnCount = 2048, int cellsPerColumn = 32)
    {
        var network = new Network();

        network.AddRegion(new SPRegion($"{name}_SP", new SpatialPoolerConfig
        {
            InputSize = inputSize,
            ColumnCount = columnCount,
        }));

        network.AddRegion(new TMRegion($"{name}_TM", new TemporalMemoryConfig
        {
            ColumnCount = columnCount,
            CellsPerColumn = cellsPerColumn,
        }));

        network.Link($"{name}_SP", "bottomUpOut", $"{name}_TM", "bottomUpIn");

        return network;
    }
}


// ============================================================================
// SECTION 14: Serialization / Persistence
// ============================================================================
// Binary serialization for HTM state — enabling checkpoint/restore of
// trained models. Uses a compact binary format with versioning.
// ============================================================================

public static class HtmSerializer
{
    private const int FormatVersion = 1;
    private const uint MagicNumber = 0x48544D31; // "HTM1"

    /// Serialize an SDR to a compact binary representation
    public static byte[] SerializeSDR(SDR sdr)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(MagicNumber);
        bw.Write((byte)FormatVersion);
        bw.Write((byte)0x01); // Type: SDR
        bw.Write(sdr.Size);
        bw.Write(sdr.ActiveCount);

        foreach (int bit in sdr.ActiveBits)
            bw.Write(bit);

        return ms.ToArray();
    }

    public static SDR DeserializeSDR(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        uint magic = br.ReadUInt32();
        if (magic != MagicNumber) throw new FormatException("Invalid HTM format");

        byte version = br.ReadByte();
        byte type = br.ReadByte();
        if (type != 0x01) throw new FormatException("Not an SDR");

        int size = br.ReadInt32();
        int activeCount = br.ReadInt32();
        var bits = new int[activeCount];
        for (int i = 0; i < activeCount; i++)
            bits[i] = br.ReadInt32();

        return new SDR(size, bits);
    }

    /// Serialize a segment's synapses
    public static void WriteSegment(BinaryWriter bw, DendriteSegment segment)
    {
        bw.Write(segment.CellIndex);
        bw.Write(segment.CreatedAtIteration);
        bw.Write(segment.LastActivatedIteration);
        bw.Write(segment.SynapseCount);

        foreach (var syn in segment.Synapses)
        {
            bw.Write(syn.PresynapticIndex);
            bw.Write(syn.Permanence);
            bw.Write(syn.CreatedAtIteration);
        }
    }

    public static DendriteSegment ReadSegment(BinaryReader br)
    {
        int cellIndex = br.ReadInt32();
        int created = br.ReadInt32();
        int lastActivated = br.ReadInt32();
        int synapseCount = br.ReadInt32();

        var segment = new DendriteSegment(cellIndex, created)
        {
            LastActivatedIteration = lastActivated,
        };

        for (int i = 0; i < synapseCount; i++)
        {
            segment.AddSynapse(new Synapse(
                br.ReadInt32(),
                br.ReadSingle(),
                br.ReadInt32()));
        }

        return segment;
    }

    // --- Region Factory for deserialization ---
    // Maps fully qualified type names to factory functions: (name, blob) → IRegion
    private static readonly Dictionary<string, Func<string, byte[], IRegion>> _regionFactory = new()
    {
        [typeof(SPRegion).FullName!] = (name, data) => SPRegion.CreateFromData(name, data),
        [typeof(TMRegion).FullName!] = (name, data) => TMRegion.CreateFromData(name, data),
    };

    /// Register a custom region type for deserialization
    public static void RegisterRegionFactory(string typeName, Func<string, byte[], IRegion> factory)
        => _regionFactory[typeName] = factory;

    /// Save a full network state to a file (with checksum)
    public static void SaveNetwork(Network network, string filePath)
    {
        // Write to memory first so we can compute the checksum
        using var buffer = new MemoryStream();
        using var bw = new BinaryWriter(buffer);

        bw.Write(MagicNumber);
        bw.Write((byte)FormatVersion);
        bw.Write((byte)0x10); // Type: Network

        // Write region count and data
        var regions = network.Regions;
        bw.Write(regions.Count);
        foreach (var (name, region) in regions)
        {
            bw.Write(name);
            bw.Write(region.GetType().FullName ?? "");
            var regionData = region.Serialize();
            bw.Write(regionData.Length);
            bw.Write(regionData);
        }

        // Write links
        bw.Write(network.Links.Count);
        foreach (var link in network.Links)
        {
            bw.Write(link.SourceRegion);
            bw.Write(link.SourcePort);
            bw.Write(link.TargetRegion);
            bw.Write(link.TargetPort);
        }

        // Append checksum
        var payload = buffer.ToArray();
        uint checksum = ComputeChecksum(payload);
        bw.Write(checksum);

        // Write to file
        File.WriteAllBytes(filePath, buffer.ToArray());
    }

    /// Load a full network state from a file
    public static Network LoadNetwork(string filePath)
    {
        var allBytes = File.ReadAllBytes(filePath);
        if (allBytes.Length < 10) // magic(4) + version(1) + type(1) + checksum(4) minimum
            throw new FormatException("File too small to be a valid HTM network");

        // Verify checksum: last 4 bytes are the checksum of everything before them
        var payloadLength = allBytes.Length - 4;
        var payload = allBytes.AsSpan(0, payloadLength).ToArray();
        uint storedChecksum = BitConverter.ToUInt32(allBytes, payloadLength);
        uint computedChecksum = ComputeChecksum(payload);
        if (storedChecksum != computedChecksum)
            throw new FormatException(
                $"Checksum mismatch: expected 0x{storedChecksum:X8}, computed 0x{computedChecksum:X8}");

        using var ms = new MemoryStream(payload);
        using var br = new BinaryReader(ms);

        // Header
        uint magic = br.ReadUInt32();
        if (magic != MagicNumber)
            throw new FormatException("Invalid HTM format: bad magic number");

        byte version = br.ReadByte();
        if (version != FormatVersion)
            throw new FormatException($"Unsupported format version: {version}");

        byte type = br.ReadByte();
        if (type != 0x10)
            throw new FormatException($"Expected Network type (0x10), got 0x{type:X2}");

        var network = new Network();

        // Read regions
        int regionCount = br.ReadInt32();
        for (int i = 0; i < regionCount; i++)
        {
            string name = br.ReadString();
            string typeName = br.ReadString();
            int dataLength = br.ReadInt32();
            byte[] regionData = br.ReadBytes(dataLength);

            if (!_regionFactory.TryGetValue(typeName, out var factory))
                throw new FormatException(
                    $"Unknown region type '{typeName}'. Register it with HtmSerializer.RegisterRegionFactory().");

            var region = factory(name, regionData);
            network.AddRegion(region);
        }

        // Read and re-wire links
        int linkCount = br.ReadInt32();
        for (int i = 0; i < linkCount; i++)
        {
            string sourceRegion = br.ReadString();
            string sourcePort = br.ReadString();
            string targetRegion = br.ReadString();
            string targetPort = br.ReadString();
            network.Link(sourceRegion, sourcePort, targetRegion, targetPort);
        }

        return network;
    }

    /// Compute a checksum for integrity verification (FNV-1a)
    public static uint ComputeChecksum(byte[] data)
    {
        uint hash = 0x811c9dc5; // FNV-1a offset basis
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 0x01000193; // FNV prime
        }
        return hash;
    }

    // --- Standalone SP/TM serialization (outside NetworkAPI) ---

    /// Save a standalone SpatialPooler (config + learned state) to a file.
    public static void SaveSpatialPooler(
        SpatialPooler sp, SpatialPoolerConfig config, string filePath)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(MagicNumber);
        bw.Write((byte)FormatVersion);
        bw.Write((byte)0x02); // Type: SpatialPooler
        SPRegion.WriteSPConfig(bw, config);
        sp.SerializeState(bw);

        File.WriteAllBytes(filePath, ms.ToArray());
    }

    /// Load a standalone SpatialPooler from a file.
    /// Returns a fully reconstructed SP with config and learned state.
    public static (SpatialPooler SP, SpatialPoolerConfig Config) LoadSpatialPooler(string filePath)
    {
        var data = File.ReadAllBytes(filePath);
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        uint magic = br.ReadUInt32();
        if (magic != MagicNumber) throw new FormatException("Invalid HTM format");

        byte version = br.ReadByte();
        byte type = br.ReadByte();
        if (type != 0x02) throw new FormatException("Not a SpatialPooler file");

        var config = SPRegion.ReadSPConfig(br);
        var sp = new SpatialPooler(config);
        sp.DeserializeState(br);

        return (sp, config);
    }

    /// Save a standalone TemporalMemory (config + learned state) to a file.
    public static void SaveTemporalMemory(
        TemporalMemory tm, TemporalMemoryConfig config, string filePath)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(MagicNumber);
        bw.Write((byte)FormatVersion);
        bw.Write((byte)0x03); // Type: TemporalMemory
        TMRegion.WriteTMConfig(bw, config);
        tm.SerializeState(bw);

        File.WriteAllBytes(filePath, ms.ToArray());
    }

    /// Load a standalone TemporalMemory from a file.
    /// Returns a fully reconstructed TM with config and learned state.
    public static (TemporalMemory TM, TemporalMemoryConfig Config) LoadTemporalMemory(string filePath)
    {
        var data = File.ReadAllBytes(filePath);
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        uint magic = br.ReadUInt32();
        if (magic != MagicNumber) throw new FormatException("Invalid HTM format");

        byte version = br.ReadByte();
        byte type = br.ReadByte();
        if (type != 0x03) throw new FormatException("Not a TemporalMemory file");

        var config = TMRegion.ReadTMConfig(br);
        var tm = new TemporalMemory(config);
        tm.DeserializeState(br);

        return (tm, config);
    }

    /// Save a full HtmEngine (SP config + state, TM config + state, iteration)
    public static void SaveHtmEngine(
        SpatialPooler sp, SpatialPoolerConfig spConfig,
        TemporalMemory tm, TemporalMemoryConfig tmConfig,
        int iteration, string filePath)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(MagicNumber);
        bw.Write((byte)FormatVersion);
        bw.Write((byte)0x20); // Type: HtmEngine
        bw.Write(iteration);

        SPRegion.WriteSPConfig(bw, spConfig);
        sp.SerializeState(bw);

        TMRegion.WriteTMConfig(bw, tmConfig);
        tm.SerializeState(bw);

        File.WriteAllBytes(filePath, ms.ToArray());
    }

    /// Load the raw components of a saved HtmEngine.
    /// Returns the reconstructed SP, TM, their configs, and the saved iteration count.
    public static (SpatialPooler SP, SpatialPoolerConfig SPConfig,
                    TemporalMemory TM, TemporalMemoryConfig TMConfig,
                    int Iteration) LoadHtmEngineComponents(string filePath)
    {
        var data = File.ReadAllBytes(filePath);
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        uint magic = br.ReadUInt32();
        if (magic != MagicNumber) throw new FormatException("Invalid HTM format");

        byte version = br.ReadByte();
        byte type = br.ReadByte();
        if (type != 0x20) throw new FormatException("Not an HtmEngine file");

        int iteration = br.ReadInt32();

        var spConfig = SPRegion.ReadSPConfig(br);
        var sp = new SpatialPooler(spConfig);
        sp.DeserializeState(br);

        var tmConfig = TMRegion.ReadTMConfig(br);
        var tm = new TemporalMemory(tmConfig);
        tm.DeserializeState(br);

        return (sp, spConfig, tm, tmConfig, iteration);
    }
}


// ============================================================================
// SECTION 15: Diagnostics & Metrics
// ============================================================================
// Comprehensive monitoring for HTM system health: SDR quality,
// algorithm convergence, memory usage, and throughput tracking.
// ============================================================================

public sealed class HtmDiagnostics
{
    // --- SDR Quality Metrics ---

    /// Analyze SDR population statistics across a batch of representations
    public static SdrQualityReport AnalyzeSDRQuality(IReadOnlyList<SDR> sdrs)
    {
        if (sdrs.Count == 0) return new SdrQualityReport();

        float avgSparsity = sdrs.Average(s => s.Sparsity);
        float sparsityStdDev = MathF.Sqrt(sdrs.Average(s =>
        {
            float diff = s.Sparsity - avgSparsity;
            return diff * diff;
        }));

        // Compute average pairwise overlap (sample for large collections)
        float avgOverlap = 0;
        int pairs = 0;
        int sampleSize = Math.Min(sdrs.Count, 100);
        var rng = new Random(42);
        var indices = Enumerable.Range(0, sdrs.Count).OrderBy(_ => rng.Next()).Take(sampleSize).ToList();

        for (int i = 0; i < indices.Count; i++)
        {
            for (int j = i + 1; j < indices.Count; j++)
            {
                avgOverlap += sdrs[indices[i]].MatchScore(sdrs[indices[j]]);
                pairs++;
            }
        }
        avgOverlap = pairs > 0 ? avgOverlap / pairs : 0;

        // Bit entropy: how uniformly are bits used across all SDRs?
        var bitUseCounts = new int[sdrs[0].Size];
        foreach (var sdr in sdrs)
            foreach (int bit in sdr.ActiveBits)
                bitUseCounts[bit]++;

        int activeBitsUsed = bitUseCounts.Count(c => c > 0);
        float entropy = ComputeEntropy(bitUseCounts, sdrs.Count);

        return new SdrQualityReport
        {
            Count = sdrs.Count,
            AvgSparsity = avgSparsity,
            SparsityStdDev = sparsityStdDev,
            AvgPairwiseOverlap = avgOverlap,
            UniqueBitsUsed = activeBitsUsed,
            BitEntropy = entropy,
        };
    }

    private static float ComputeEntropy(int[] counts, int total)
    {
        float entropy = 0;
        foreach (int count in counts)
        {
            if (count == 0) continue;
            float p = (float)count / total;
            entropy -= p * MathF.Log2(p);
        }
        return entropy;
    }

    /// Snapshot of current system resource usage
    public static SystemHealthReport GetSystemHealth(
        SpatialPooler? sp = null, TemporalMemory? tm = null)
    {
        return new SystemHealthReport
        {
            TotalSegments = tm?.TotalSegmentCount ?? 0,
            TotalSynapses = tm?.TotalSynapseCount ?? 0,
            ProcessMemoryMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            SPMetrics = sp?.Metrics,
            TMMetrics = tm?.Metrics,
        };
    }
}

public record SdrQualityReport
{
    public int Count { get; init; }
    public float AvgSparsity { get; init; }
    public float SparsityStdDev { get; init; }
    public float AvgPairwiseOverlap { get; init; }
    public int UniqueBitsUsed { get; init; }
    public float BitEntropy { get; init; }

    public override string ToString() =>
        $"SDR Quality: n={Count}, sparsity={AvgSparsity:P2}±{SparsityStdDev:P2}, " +
        $"avgOverlap={AvgPairwiseOverlap:P2}, uniqueBits={UniqueBitsUsed}, " +
        $"entropy={BitEntropy:F2}";
}

public record SystemHealthReport
{
    public int TotalSegments { get; init; }
    public int TotalSynapses { get; init; }
    public double ProcessMemoryMB { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public SpatialPoolerMetrics? SPMetrics { get; init; }
    public TemporalMemoryMetrics? TMMetrics { get; init; }

    public override string ToString() =>
        $"Health: segments={TotalSegments:N0}, synapses={TotalSynapses:N0}, " +
        $"mem={ProcessMemoryMB:F1}MB, GC=[{Gen0Collections}/{Gen1Collections}/{Gen2Collections}]" +
        $"\n  {SPMetrics}\n  {TMMetrics}";
}


// ============================================================================
// SECTION 16: Multi-Stream Concurrent Processing
// ============================================================================
// Enables processing multiple independent data streams in parallel,
// each with its own HTM pipeline. Uses System.Threading.Channels for
// backpressure-aware async ingestion and a dedicated thread per stream.
//
// Use cases:
//   - Multi-sensor anomaly detection (e.g., IoT fleet)
//   - Parallel time-series analysis
//   - A/B testing different HTM configurations
// ============================================================================

public record StreamConfig
{
    public string StreamId { get; init; }
    public CompositeEncoder Encoder { get; init; }
    public SpatialPoolerConfig SPConfig { get; init; }
    public TemporalMemoryConfig TMConfig { get; init; }
    public int[] PredictionSteps { get; init; } = new[] { 1, 5 };
    public double PredictorResolution { get; init; } = 1.0;
}

public record StreamDataPoint
{
    public string StreamId { get; init; }
    public Dictionary<string, object> Data { get; init; }
    public DateTime Timestamp { get; init; }
}

public record StreamResult
{
    public string StreamId { get; init; }
    public DateTime Timestamp { get; init; }
    public float RawAnomaly { get; init; }
    public float AnomalyLikelihood { get; init; }
    public Dictionary<int, SdrPrediction> Predictions { get; init; }
    public int ActiveCellCount { get; init; }
    public int PredictiveCellCount { get; init; }
}

/// Individual HTM pipeline for a single stream
public sealed class StreamPipeline
{
    public string StreamId { get; }
    private readonly CompositeEncoder _encoder;
    private readonly SpatialPooler _sp;
    private readonly TemporalMemory _tm;
    private readonly SdrPredictor _predictor;
    private readonly AnomalyLikelihood _anomalyLikelihood;
    private long _recordsProcessed;

    public long RecordsProcessed => _recordsProcessed;

    public StreamPipeline(StreamConfig config)
    {
        StreamId = config.StreamId;
        _encoder = config.Encoder;

        _sp = new SpatialPooler(config.SPConfig with
        {
            InputSize = _encoder.TotalSize,
        });

        _tm = new TemporalMemory(config.TMConfig);
        _predictor = new SdrPredictor(config.PredictionSteps, resolution: config.PredictorResolution);
        _anomalyLikelihood = new AnomalyLikelihood();
    }

    public StreamResult Process(Dictionary<string, object> data, DateTime timestamp)
    {
        Interlocked.Increment(ref _recordsProcessed);

        SDR encoded = _encoder.Encode(data);
        SDR activeColumns = _sp.Compute(encoded);
        var tmOutput = _tm.Compute(activeColumns);

        float anomalyLikelihood = _anomalyLikelihood.Compute(tmOutput.Anomaly);
        var predictions = _predictor.Infer(tmOutput.ActiveCells);

        // Train predictor if value is present
        if (data.TryGetValue("value", out var valueObj) && valueObj is double value)
        {
            foreach (int step in predictions.Keys)
                _predictor.Learn(step, tmOutput.ActiveCells, value);
        }

        return new StreamResult
        {
            StreamId = StreamId,
            Timestamp = timestamp,
            RawAnomaly = tmOutput.Anomaly,
            AnomalyLikelihood = anomalyLikelihood,
            Predictions = predictions,
            ActiveCellCount = tmOutput.ActiveCells.Count,
            PredictiveCellCount = tmOutput.PredictiveCells.Count,
        };
    }
}

/// Multi-stream processor: manages concurrent HTM pipelines with backpressure
public sealed class MultiStreamProcessor : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, StreamPipeline> _pipelines = new();
    private readonly Channel<StreamDataPoint> _inputChannel;
    private readonly Channel<StreamResult> _outputChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workerTasks;
    private readonly int _workerCount;

    public MultiStreamProcessor(int workerCount = 4, int inputBufferSize = 10_000)
    {
        _workerCount = workerCount;

        _inputChannel = Channel.CreateBounded<StreamDataPoint>(
            new BoundedChannelOptions(inputBufferSize)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });

        _outputChannel = Channel.CreateUnbounded<StreamResult>();

        _workerTasks = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => WorkerLoop(_cts.Token)))
            .ToArray();
    }

    /// Register a new stream with its own HTM pipeline
    public void AddStream(StreamConfig config)
    {
        var pipeline = new StreamPipeline(config);
        if (!_pipelines.TryAdd(config.StreamId, pipeline))
            throw new InvalidOperationException($"Stream '{config.StreamId}' already exists");
    }

    /// Submit a data point for processing (async, backpressure-aware)
    public ValueTask SubmitAsync(StreamDataPoint dataPoint,
        CancellationToken ct = default)
        => _inputChannel.Writer.WriteAsync(dataPoint, ct);

    /// Submit synchronously (blocks if buffer is full)
    public void Submit(StreamDataPoint dataPoint)
        => _inputChannel.Writer.TryWrite(dataPoint);

    /// Read results as an async stream
    public IAsyncEnumerable<StreamResult> ReadResultsAsync(
        CancellationToken ct = default)
        => _outputChannel.Reader.ReadAllAsync(ct);

    /// Try to read a result without blocking
    public bool TryReadResult(out StreamResult? result)
    {
        if (_outputChannel.Reader.TryRead(out var r))
        {
            result = r;
            return true;
        }
        result = null;
        return false;
    }

    /// Worker loop: reads from input channel, routes to correct pipeline, writes results
    private async Task WorkerLoop(CancellationToken ct)
    {
        await foreach (var dataPoint in _inputChannel.Reader.ReadAllAsync(ct))
        {
            if (!_pipelines.TryGetValue(dataPoint.StreamId, out var pipeline))
                continue; // Unknown stream, skip

            try
            {
                var result = pipeline.Process(dataPoint.Data, dataPoint.Timestamp);
                await _outputChannel.Writer.WriteAsync(result, ct);
            }
            catch (Exception ex)
            {
                // Log and continue — don't crash the worker
                Debug.WriteLine($"Error processing {dataPoint.StreamId}: {ex.Message}");
            }
        }
    }

    /// Get aggregate statistics across all streams
    public MultiStreamStats GetStats()
    {
        var pipelines = _pipelines.Values.ToList();
        return new MultiStreamStats
        {
            StreamCount = pipelines.Count,
            TotalRecordsProcessed = pipelines.Sum(p => p.RecordsProcessed),
            WorkerCount = _workerCount,
            InputBufferPending = _inputChannel.Reader.CanCount
                ? _inputChannel.Reader.Count : -1,
        };
    }

    public async ValueTask DisposeAsync()
    {
        _inputChannel.Writer.Complete();
        _cts.Cancel();

        try { await Task.WhenAll(_workerTasks); }
        catch (OperationCanceledException) { }

        _outputChannel.Writer.Complete();
        _cts.Dispose();
    }
}

public record MultiStreamStats
{
    public int StreamCount { get; init; }
    public long TotalRecordsProcessed { get; init; }
    public int WorkerCount { get; init; }
    public int InputBufferPending { get; init; }

    public override string ToString() =>
        $"MultiStream: {StreamCount} streams, {TotalRecordsProcessed:N0} records, " +
        $"{WorkerCount} workers, {InputBufferPending} pending";
}


// ============================================================================
// SECTION 17: Full HTM Engine — Single-Stream Orchestrator
// ============================================================================
// Convenience wrapper that wires Encoder → SP → TM → Predictor → Anomaly
// into a single call. For multi-stream, use MultiStreamProcessor instead.
// ============================================================================

public sealed class HtmEngineConfig
{
    public CompositeEncoder Encoder { get; init; }
    public int ColumnCount { get; init; } = 2048;
    public int CellsPerColumn { get; init; } = 32;
    public float Sparsity { get; init; } = 0.02f;
    public int[] PredictionSteps { get; init; } = new[] { 1, 5 };
    public double PredictorResolution { get; init; } = 1.0;
    public InhibitionMode Inhibition { get; init; } = InhibitionMode.Global;
    public int InhibitionRadius { get; init; } = 50;
    public int MaxSegmentsPerCell { get; init; } = 128;
    public int MaxSynapsesPerSegment { get; init; } = 64;
}

public record HtmResult
{
    public SDR EncodedInput { get; init; }
    public SDR ActiveColumns { get; init; }
    public TemporalMemoryOutput TmOutput { get; init; }
    public float AnomalyLikelihood { get; init; }
    public Dictionary<int, SdrPrediction> Predictions { get; init; }
    public int Iteration { get; init; }
}

public sealed class HtmEngine
{
    private readonly HtmEngineConfig _config;
    private readonly CompositeEncoder _encoder;
    private readonly SpatialPoolerConfig _spConfig;
    private readonly TemporalMemoryConfig _tmConfig;
    private SpatialPooler _sp;
    private TemporalMemory _tm;
    private readonly SdrPredictor _predictor;
    private readonly AnomalyLikelihood _anomalyLikelihood;
    private int _iteration;

    public SpatialPooler SP => _sp;
    public TemporalMemory TM => _tm;

    public HtmEngine(HtmEngineConfig config)
    {
        _config = config;
        _encoder = config.Encoder;

        _spConfig = new SpatialPoolerConfig
        {
            InputSize = _encoder.TotalSize,
            ColumnCount = config.ColumnCount,
            TargetSparsity = config.Sparsity,
            Inhibition = config.Inhibition,
            InhibitionRadius = config.InhibitionRadius,
        };

        _tmConfig = new TemporalMemoryConfig
        {
            ColumnCount = config.ColumnCount,
            CellsPerColumn = config.CellsPerColumn,
            MaxSegmentsPerCell = config.MaxSegmentsPerCell,
            MaxSynapsesPerSegment = config.MaxSynapsesPerSegment,
        };

        _sp = new SpatialPooler(_spConfig);
        _tm = new TemporalMemory(_tmConfig);
        _predictor = new SdrPredictor(config.PredictionSteps, resolution: config.PredictorResolution);
        _anomalyLikelihood = new AnomalyLikelihood();
    }

    /// Process one timestep through the full pipeline
    public HtmResult Compute(Dictionary<string, object> inputData, bool learn = true)
    {
        _iteration++;

        // Encode → SP → TM
        SDR encoded = _encoder.Encode(inputData);
        SDR activeColumns = _sp.Compute(encoded, learn);
        var tmOutput = _tm.Compute(activeColumns, learn);

        // Anomaly likelihood
        float anomalyLikelihood = _anomalyLikelihood.Compute(tmOutput.Anomaly);

        // Predictions
        var predictions = _predictor.Infer(tmOutput.ActiveCells);

        // Train predictor
        if (learn && inputData.TryGetValue("value", out var valueObj) && valueObj is double value)
        {
            foreach (int step in predictions.Keys)
                _predictor.Learn(step, tmOutput.ActiveCells, value);
        }

        return new HtmResult
        {
            EncodedInput = encoded,
            ActiveColumns = activeColumns,
            TmOutput = tmOutput,
            AnomalyLikelihood = anomalyLikelihood,
            Predictions = predictions,
            Iteration = _iteration,
        };
    }

    /// Get comprehensive system health report
    public SystemHealthReport GetHealth()
        => HtmDiagnostics.GetSystemHealth(_sp, _tm);

    /// Save SP and TM learned state to a file. The predictor and anomaly
    /// likelihood are not serialized — they re-adapt quickly on resumed input.
    public void Save(string filePath)
        => HtmSerializer.SaveHtmEngine(_sp, _spConfig, _tm, _tmConfig, _iteration, filePath);

    /// Load SP and TM learned state from a file, replacing current state.
    /// The encoder, predictor, and anomaly likelihood are preserved from this instance.
    public void Load(string filePath)
    {
        var (sp, _, tm, _, iteration) = HtmSerializer.LoadHtmEngineComponents(filePath);
        _sp = sp;
        _tm = tm;
        _iteration = iteration;
    }
}


// ============================================================================
// SECTION 18: Usage Examples
// ============================================================================

public static class HtmExamples
{
    /// Example 1: Single-stream anomaly detection (Hot Gym pattern)
    public static void RunSingleStreamDemo()
    {
        // --- Configure Encoders ---
        var encoder = new CompositeEncoder()
            .AddEncoder("value", new RandomDistributedScalarEncoder(
                size: 700, activeBits: 21, resolution: 0.88))
            .AddEncoder("timestamp", new DateTimeEncoder());

        // --- Build Engine ---
        var engine = new HtmEngine(new HtmEngineConfig
        {
            Encoder = encoder,
            ColumnCount = 2048,
            CellsPerColumn = 32,
            Sparsity = 0.02f,
            Inhibition = InhibitionMode.Global,
            PredictionSteps = new[] { 1, 5 },
            PredictorResolution = 0.88,
            MaxSegmentsPerCell = 128,
            MaxSynapsesPerSegment = 64,
        });

        // --- Process Stream ---
        var rng = new Random(42);
        for (int i = 0; i < 5000; i++)
        {
            var timestamp = DateTime.Now.AddHours(i);
            double consumption = 50 + 20 * Math.Sin(2 * Math.PI * i / 24)  // Daily cycle
                               + 10 * Math.Sin(2 * Math.PI * i / 168)       // Weekly cycle
                               + rng.NextDouble() * 5;                       // Noise

            // Inject anomaly at step 2000
            if (i == 2000) consumption += 100;

            var result = engine.Compute(new Dictionary<string, object>
            {
                ["timestamp"] = timestamp,
                ["value"] = consumption,
            });

            if (i % 500 == 0 || result.AnomalyLikelihood > 0.99f)
            {
                Console.WriteLine(
                    $"[{i,5}] {timestamp:HH:mm} | " +
                    $"val={consumption,6:F1} | " +
                    $"anomaly={result.TmOutput.Anomaly:P0} | " +
                    $"likelihood={result.AnomalyLikelihood:P1} | " +
                    $"pred1={result.Predictions.GetValueOrDefault(1)?.BestPrediction:F1} | " +
                    $"burst={result.TmOutput.BurstingColumnCount}");
            }
        }

        Console.WriteLine(engine.GetHealth());
    }

    /// Example 2: Multi-stream concurrent processing
    public static async Task RunMultiStreamDemo()
    {
        await using var processor = new MultiStreamProcessor(workerCount: 4);

        // Register multiple sensor streams
        for (int s = 0; s < 10; s++)
        {
            var encoder = new CompositeEncoder()
                .AddEncoder("value", new RandomDistributedScalarEncoder(
                    size: 400, activeBits: 21, resolution: 1.0))
                .AddEncoder("timestamp", new DateTimeEncoder(hourBits: 48, dowBits: 48));

            processor.AddStream(new StreamConfig
            {
                StreamId = $"sensor_{s}",
                Encoder = encoder,
                SPConfig = new SpatialPoolerConfig { ColumnCount = 1024 },
                TMConfig = new TemporalMemoryConfig { ColumnCount = 1024, CellsPerColumn = 16 },
            });
        }

        // Start result consumer
        var consumer = Task.Run(async () =>
        {
            int count = 0;
            await foreach (var result in processor.ReadResultsAsync())
            {
                if (result.AnomalyLikelihood > 0.95f)
                    Console.WriteLine($"  ⚠ {result.StreamId} anomaly at {result.Timestamp:HH:mm}");
                if (++count >= 50_000) break;
            }
        });

        // Produce data
        var rng = new Random(42);
        for (int i = 0; i < 5000; i++)
        {
            for (int s = 0; s < 10; s++)
            {
                await processor.SubmitAsync(new StreamDataPoint
                {
                    StreamId = $"sensor_{s}",
                    Timestamp = DateTime.Now.AddMinutes(i),
                    Data = new Dictionary<string, object>
                    {
                        ["timestamp"] = DateTime.Now.AddMinutes(i),
                        ["value"] = (double)(50 + 20 * Math.Sin(2 * Math.PI * i / 100 + s)
                                    + rng.NextDouble() * 3),
                    },
                });
            }
        }

        Console.WriteLine(processor.GetStats());
        await consumer;
    }

    /// Example 3: NetworkAPI — declarative pipeline construction
    public static void RunNetworkApiDemo()
    {
        // Build a two-level hierarchy: SP1 → TM1 → SP2 → TM2
        var network = new Network();

        network.AddRegion(new SPRegion("L1_SP", new SpatialPoolerConfig
        {
            InputSize = 400, ColumnCount = 1024,
        }));
        network.AddRegion(new TMRegion("L1_TM", new TemporalMemoryConfig
        {
            ColumnCount = 1024, CellsPerColumn = 16,
        }));

        network.Link("L1_SP", "bottomUpOut", "L1_TM", "bottomUpIn");

        // Run
        var encoder = new ScalarEncoder(400, 21, 0, 100);
        for (int i = 0; i < 1000; i++)
        {
            double value = 50 + 30 * Math.Sin(2 * Math.PI * i / 50);
            SDR encoded = encoder.Encode(value);

            network.SetInput("L1_SP", "bottomUpIn", encoded);
            network.Compute();

            var anomaly = (float)network.GetOutput("L1_TM", "anomaly");
            if (i % 100 == 0)
                Console.WriteLine($"[{i}] anomaly={anomaly:P1}");
        }
    }

    /// Example 4: Thousand Brains object recognition
    public static void RunThousandBrainsDemo()
    {
        var engine = new ThousandBrainsEngine(new ThousandBrainsConfig
        {
            ColumnCount = 4,
            ColumnConfig = new CorticalColumnConfig
            {
                InputSize = 256,
                LocationSize = 1600,
                ColumnCount = 512,
                CellsPerColumn = 8,
            },
            VotingConfig = new LateralVotingConfig
            {
                ConvergenceThreshold = 0.6f,
                MaxIterations = 5,
            },
        });

        var featureEncoder = new CategoryEncoder(256, 15);

        // --- Learn Object: "coffee mug" ---
        Console.WriteLine("Learning: coffee mug");
        engine.StartNewObject();

        var mugFeatures = new[] { "ceramic", "handle", "rim", "base", "logo" };
        for (int touch = 0; touch < mugFeatures.Length; touch++)
        {
            var feature = featureEncoder.Encode(mugFeatures[touch]);
            var patches = Enumerable.Repeat(feature, 4).ToArray();
            engine.Process(patches, moveDeltaX: 1.0f, moveDeltaY: 0.5f, learn: true);
        }
        engine.LearnObject("coffee_mug");

        // --- Learn Object: "water bottle" ---
        Console.WriteLine("Learning: water bottle");
        engine.StartNewObject();

        var bottleFeatures = new[] { "plastic", "cap", "label", "base", "grip" };
        for (int touch = 0; touch < bottleFeatures.Length; touch++)
        {
            var feature = featureEncoder.Encode(bottleFeatures[touch]);
            var patches = Enumerable.Repeat(feature, 4).ToArray();
            engine.Process(patches, moveDeltaX: 0.5f, moveDeltaY: 1.0f, learn: true);
        }
        engine.LearnObject("water_bottle");

        // --- Recognition Test ---
        Console.WriteLine("\nRecognition test:");
        engine.StartNewObject();

        var testFeatures = new[] { "ceramic", "handle", "rim" };
        for (int touch = 0; touch < testFeatures.Length; touch++)
        {
            var feature = featureEncoder.Encode(testFeatures[touch]);
            var patches = Enumerable.Repeat(feature, 4).ToArray();
            var result = engine.Process(patches, 1.0f, 0.5f, learn: false);

            Console.WriteLine($"  Touch {touch}: features='{testFeatures[touch]}' → " +
                              $"recognized='{result.RecognizedObject ?? "unknown"}' " +
                              $"(conf={result.RecognitionConfidence:P0}, " +
                              $"converged={result.Converged}, " +
                              $"iters={result.VotingIterations})");
        }
    }
}
