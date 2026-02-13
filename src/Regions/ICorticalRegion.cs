// ============================================================================
// CortexSharp — Cortical Region Interface
// ============================================================================
// A cortical region is a collection of cortical columns that communicate
// laterally to reach consensus on object identity. Each region corresponds
// to a functional area of the neocortex (e.g., V1 for primary vision,
// S1 for primary somatosensory).
//
// The region's primary responsibility is ORCHESTRATION:
//   1. Route sensory inputs to their respective columns
//   2. Run intra-column computation (L6 → L4 → L2/3) — ONCE per sample
//   3. Collect L2/3 representations from all columns
//   4. Run lateral voting loop (narrowing only, no L4/L6 re-processing)
//   5. Report recognition result
//
// Two-phase design:
//   Process() runs column computation + voting for a sensory sample.
//   Settle() runs ONLY the voting loop without column re-computation.
//
//   Process() is called once per sensory input. Settle() can be called
//   during hierarchical settling where higher-region feedback has changed
//   the context but no new sensory input has arrived.
//
// Lateral voting:
//   After each column computes independently, their L2/3 representations
//   are compared. Bits supported by enough columns form the consensus.
//   This consensus is fed back to each column's L2/3 via
//   ApplyLateralNarrowing() (NOT by re-running the full pipeline).
//   Over iterations, all columns converge to the same representation —
//   this IS object recognition.
//
// Multi-patch sensing:
//   Each column processes ONE sensory patch. The number of inputs must
//   match the number of columns, OR be 1 for broadcast mode.
//
// Hierarchical connectivity:
//   A region can send its consensus to a HIGHER region as feedforward
//   input, and receive feedback from that higher region as apical input.
//   This enables multi-level abstraction (sensory → conceptual).
//
// Reference: Hawkins et al. (2017), "A Theory of How Columns..."
//            Hawkins et al. (2019), "A Framework for Intelligence..."
// ============================================================================

using CortexSharp.Columns;
using CortexSharp.Core;

namespace CortexSharp.Regions;

/// <summary>
/// A cortical region — columns + lateral voting + consensus.
/// </summary>
public interface ICorticalRegion
{
    // =========================================================================
    // Core computation — called ONCE per sensory sample
    // =========================================================================

    /// <summary>
    /// Process one sensory sample across all columns.
    /// Runs the full pipeline: column computation → lateral voting → consensus.
    /// Column computation (L6 → L4 → L2/3) happens ONCE. The voting loop
    /// only runs L2/3 lateral narrowing, not the full pipeline.
    /// </summary>
    /// <param name="inputs">
    /// One SensoryInput per column (each bundling feature SDR + displacement),
    /// or a single SensoryInput broadcast to all columns.
    /// Length must equal <see cref="ColumnCount"/> or 1.
    /// </param>
    /// <param name="learn">If true, learn at all layers of all columns.</param>
    /// <returns>Region-level output including consensus and convergence status.</returns>
    RegionOutput Process(SensoryInput[] inputs, bool learn);

    // =========================================================================
    // Settling — re-run voting WITHOUT new sensory processing
    // =========================================================================

    /// <summary>
    /// Re-run lateral voting without processing new sensory input.
    /// Used during hierarchical settling when top-down feedback has changed
    /// but no new sensory sample has arrived. This only runs L2/3 lateral
    /// narrowing — L4 and L6 are untouched.
    /// </summary>
    /// <returns>Updated region output after re-voting.</returns>
    RegionOutput Settle();

    // =========================================================================
    // Hierarchical communication
    // =========================================================================

    /// <summary>
    /// Receive top-down feedback from a higher cortical region.
    /// Distributed to all columns as apical input.
    /// </summary>
    /// <param name="feedback">Apical SDR from the higher region's consensus.</param>
    void ReceiveHierarchicalFeedback(SDR feedback);

    // =========================================================================
    // State
    // =========================================================================

    /// <summary>
    /// Current consensus SDR after lateral voting. This is the region's
    /// best guess at the object being sensed. After convergence, this
    /// IS the recognized object representation.
    /// </summary>
    SDR Consensus { get; }

    /// <summary>True if lateral voting has converged (all columns agree).</summary>
    bool HasConverged { get; }

    /// <summary>Number of columns in this region.</summary>
    int ColumnCount { get; }

    /// <summary>Access a specific column by index.</summary>
    ICorticalColumn GetColumn(int index);

    /// <summary>
    /// Reset all columns for a new object. Clears all representations,
    /// locations, and temporal context across the entire region.
    /// </summary>
    void Reset();
}

/// <summary>
/// Output from a single region processing step.
/// </summary>
public record RegionOutput
{
    /// <summary>
    /// Consensus SDR from lateral voting. After convergence, this
    /// represents the recognized object.
    /// </summary>
    public required SDR Consensus { get; init; }

    /// <summary>True if all columns converged to the same representation.</summary>
    public bool Converged { get; init; }

    /// <summary>Average pairwise agreement between columns [0, 1].</summary>
    public float AgreementScore { get; init; }

    /// <summary>Number of lateral voting iterations performed.</summary>
    public int VotingIterations { get; init; }

    /// <summary>Per-column outputs.</summary>
    public required ColumnOutput[] ColumnOutputs { get; init; }

    /// <summary>Average anomaly across all columns.</summary>
    public float AverageAnomaly { get; init; }
}

/// <summary>
/// Configuration for a cortical region.
/// </summary>
public record CorticalRegionConfig
{
    /// <summary>Number of cortical columns in this region.</summary>
    public int ColumnCount { get; init; } = 10;

    /// <summary>Configuration applied to each column in the region.</summary>
    public required CorticalColumnConfig ColumnConfig { get; init; }

    // --- Lateral voting ---

    /// <summary>
    /// Fraction of columns that must support a bit for it to enter consensus.
    /// Range (0, 1]. Lower = more permissive. Default 0.3 (30% of columns).
    /// </summary>
    public float VoteThreshold { get; init; } = 0.3f;

    /// <summary>
    /// Average pairwise similarity required for convergence. [0, 1].
    /// Default 0.7 — columns must agree on 70%+ of their representations.
    /// </summary>
    public float ConvergenceThreshold { get; init; } = 0.7f;

    /// <summary>Maximum lateral voting iterations before stopping.</summary>
    public int MaxVotingIterations { get; init; } = 10;

    // --- Displacement cells ---

    /// <summary>Enable displacement modules for structural prediction.</summary>
    public bool EnableDisplacementCells { get; init; } = true;
}
