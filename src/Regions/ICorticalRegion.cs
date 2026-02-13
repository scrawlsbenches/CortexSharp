// ============================================================================
// CortexSharp — Cortical Region Interface
// ============================================================================
// A cortical region is a collection of cortical columns that communicate
// laterally to reach consensus on object identity. Each region corresponds
// to a functional area of the neocortex (e.g., V1 for primary vision,
// S1 for primary somatosensory).
//
// The region's primary responsibility is ORCHESTRATION:
//   1. Route sensory patches to their respective columns
//   2. Run intra-column computation (L6 → L4 → L2/3)
//   3. Collect L2/3 representations from all columns
//   4. Run lateral voting to compute consensus
//   5. Feed consensus back to columns for candidate narrowing
//   6. Repeat voting until convergence or max iterations
//   7. Report recognition result
//
// Lateral voting:
//   After each column computes independently, their L2/3 representations
//   are compared. Bits supported by enough columns form the consensus.
//   This consensus is fed back to each column, which intersects it with
//   its own representation. Over iterations, all columns converge to
//   the same representation — this IS object recognition.
//
// Multi-patch sensing:
//   Each column processes ONE sensory patch. In a somatosensory region,
//   each column corresponds to one fingertip location. In a visual region,
//   each column corresponds to one foveal fixation. The number of
//   sensory patches MUST match the number of columns (no wrapping).
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
    // Core computation
    // =========================================================================

    /// <summary>
    /// Process one sensory sample across all columns.
    /// Runs the full pipeline: column computation → lateral voting → consensus.
    /// </summary>
    /// <param name="sensoryPatches">
    /// One SDR per column, or a single SDR broadcast to all columns.
    /// Each patch is the encoded sensory input for that column's receptive field.
    /// Length must equal <see cref="ColumnCount"/> or 1.
    /// </param>
    /// <param name="deltaX">Sensor displacement in X (shared across columns).</param>
    /// <param name="deltaY">Sensor displacement in Y (shared across columns).</param>
    /// <param name="learn">If true, learn at all layers of all columns.</param>
    /// <returns>Region-level output including consensus and convergence status.</returns>
    RegionOutput Process(
        SDR[] sensoryPatches,
        float deltaX,
        float deltaY,
        bool learn);

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
