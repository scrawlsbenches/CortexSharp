// ============================================================================
// CortexSharp — Spatial Pooler Interface
// ============================================================================
// The Spatial Pooler models the proximal dendrite computation in Layer 4.
// It takes an input SDR (from an encoder or a lower region) and produces
// a fixed-sparsity output SDR representing which minicolumns are active.
//
// Algorithm:
//   1. OVERLAP — Each column computes how many of its connected proximal
//      synapses match active input bits. This overlap score, modified by
//      boost factor, determines how strongly the column responds.
//
//   2. INHIBITION — Columns compete within a neighborhood (local) or
//      globally. Only the top ~2% survive. This enforces sparsity and
//      ensures that similar inputs activate similar columns.
//
//   3. LEARNING — Winning columns adjust their proximal synapses:
//      - Increment permanence for synapses to active input bits (LTP)
//      - Decrement permanence for synapses to inactive input bits (LTD)
//      This is direct Hebbian learning: strengthen what correlates,
//      weaken what doesn't.
//
//   4. BOOSTING — Homeostatic mechanism ensuring all columns participate
//      over time. Columns that rarely win get boosted overlap scores.
//      Columns that never win get their permanences bumped. This prevents
//      dead columns and maximizes representational capacity.
//
// Key invariant: output sparsity is fixed at ~2% regardless of input.
// This is what makes SDR-based computation work — downstream consumers
// can rely on consistent sparsity for their mathematical guarantees.
//
// Reference: Cui, Ahmad & Hawkins (2017), "The HTM Spatial Pooler —
//            A Neocortical Algorithm for Online Sparse Distributed Coding"
// ============================================================================

using CortexSharp.Core;

namespace CortexSharp.Algorithms;

/// <summary>
/// Spatial Pooler — competitive inhibition on proximal dendrites.
/// Converts variable input to a fixed-sparsity column activation.
/// </summary>
public interface ISpatialPooler
{
    /// <summary>
    /// Compute which columns are active for this input.
    /// </summary>
    /// <param name="input">Input SDR (from encoder or lower region).</param>
    /// <param name="learn">
    /// If true, adjust proximal permanences on winning columns and
    /// update boost factors / duty cycles.
    /// </param>
    /// <returns>SDR of active column indices at target sparsity.</returns>
    SDR Compute(SDR input, bool learn);

    /// <summary>Total number of minicolumns.</summary>
    int ColumnCount { get; }

    /// <summary>Target fraction of columns active per timestep (~0.02).</summary>
    float TargetSparsity { get; }

    /// <summary>Size of the input SDR this SP expects.</summary>
    int InputSize { get; }

    /// <summary>Number of compute iterations performed.</summary>
    int Iteration { get; }
}
