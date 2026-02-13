// ============================================================================
// CortexSharp — Object Layer (Layer 2/3)
// ============================================================================
// Layer 2/3 is where object representations form. In classical HTM, this
// layer sends output laterally to other columns and to higher regions.
// In the Thousand Brains model, L2/3 is the seat of the ColumnPooler
// algorithm — the mechanism by which each column learns COMPLETE models
// of every object it encounters.
//
// Biological function:
//   L2/3 pyramidal neurons receive three distinct inputs:
//
//   1. Feedforward from L4 (proximal):
//      The cell-level output of L4, encoding "feature at location."
//      This drives activation — L2/3 cells that match the current
//      feedforward input become candidates for activation.
//
//   2. Lateral from peer columns (distal):
//      Long-range horizontal connections carry object representations
//      from other columns in the same region. This is how columns VOTE
//      on which object is being sensed. The intersection of lateral
//      candidates narrows the representation until consensus emerges.
//
//   3. Top-down from higher regions (apical):
//      Feedback from regions higher in the hierarchy provides contextual
//      modulation — "I think this is a coffee cup based on the scene."
//      Apical input biases but does not drive activation.
//
// The ColumnPooler algorithm:
//   Unlike the Temporal Memory (which learns sequences), the ColumnPooler
//   learns STABLE object representations. When you're touching a coffee
//   cup, the L2/3 representation should remain stable even as your finger
//   moves across different features. New feedforward input (different
//   feature at different location) should reinforce or narrow the existing
//   representation, not replace it.
//
//   Key mechanism: INERTIA. L2/3 cells that are already active have an
//   advantage over inactive cells (via recurrent distal connections or
//   persistent depolarization). This means the representation persists
//   across multiple sensory samples — it only changes when evidence
//   overwhelmingly contradicts it.
//
//   On a new object (after reset): L2/3 activates a random sparse set
//   of cells. As more (feature, location) pairs are observed, the
//   representation stabilizes. Across all columns, lateral voting drives
//   convergence to a single consistent representation.
//
// Convergence:
//   Recognition is not a threshold check against a library. Recognition
//   IS convergence — when all columns agree on the same representation,
//   the object is recognized. No separate recognition step is needed.
//
// Reference: Hawkins et al. (2017), "A Theory of How Columns in the
//            Neocortex Enable Learning the Structure of the World"
//            Hawkins et al. (2019), "A Framework for Intelligence and
//            Cortical Function Based on Grid Cells in the Neocortex"
// ============================================================================

using CortexSharp.Core;

namespace CortexSharp.Layers;

/// <summary>
/// Layer 2/3 — object representation via the ColumnPooler algorithm.
/// Each column's L2/3 learns complete models of objects as sets of
/// (feature, location) associations.
/// </summary>
public interface IObjectLayer
{
    // =========================================================================
    // Core computation
    // =========================================================================
    // The ColumnPooler computation per timestep:
    //
    //   1. Evaluate feedforward input (from L4) against existing
    //      representations. Which stored objects are consistent with
    //      this (feature, location)?
    //
    //   2. Apply inertia: cells that were active last timestep are
    //      preferred (via distal self-reinforcement). The representation
    //      persists unless contradicted by evidence.
    //
    //   3. Integrate lateral input: intersect current candidates with
    //      the consensus from peer columns. Eliminate candidates that
    //      other columns don't support.
    //
    //   4. Integrate apical input: bias toward representations that
    //      higher regions expect. Modulatory, not driving.
    //
    //   5. Select active cells: choose the sparse set that best
    //      satisfies all constraints (feedforward match + inertia +
    //      lateral agreement + apical bias).
    //
    //   6. Learn: on active cells, strengthen feedforward synapses to
    //      the current L4 input. This associates the current
    //      (feature, location) with this object representation.
    //
    // After multiple sensory samples + lateral voting iterations,
    // the representation converges to a single object.
    // =========================================================================

    /// <summary>
    /// Compute the L2/3 object representation for this timestep.
    /// </summary>
    /// <param name="feedforwardInput">
    /// Cell-level output from L4 (active cells encoding "feature at location").
    /// </param>
    /// <param name="lateralInput">
    /// Consensus SDR from lateral voting with peer columns. Null on first
    /// iteration or in single-column mode.
    /// </param>
    /// <param name="apicalInput">
    /// Top-down feedback from a higher cortical region. Null if no
    /// hierarchical feedback available.
    /// </param>
    /// <param name="learn">
    /// If true, grow/reinforce feedforward synapses on active cells,
    /// associating the current L4 input with this object representation.
    /// </param>
    /// <returns>Output including the active object representation.</returns>
    ObjectLayerOutput Compute(
        SDR feedforwardInput,
        SDR? lateralInput,
        SDR? apicalInput,
        bool learn);

    // =========================================================================
    // State
    // =========================================================================

    /// <summary>
    /// Current object representation — the sparse set of L2/3 cells
    /// representing the object being sensed. Stable across multiple
    /// sensory samples of the same object. This is what gets sent
    /// laterally to peer columns for voting.
    /// </summary>
    SDR Representation { get; }

    /// <summary>
    /// Reset the object layer for a new object. Clears the current
    /// representation and internal state so the next sensory input
    /// starts fresh (no inertia from the previous object).
    /// </summary>
    void Reset();
}

/// <summary>
/// Output from a single L2/3 compute step.
/// </summary>
public record ObjectLayerOutput
{
    /// <summary>
    /// Active L2/3 cells — the current object representation.
    /// This SDR is sent laterally to peer columns for voting.
    /// </summary>
    public required SDR Representation { get; init; }

    /// <summary>
    /// Number of cells retained from previous timestep (inertia).
    /// High overlap = representation is stable (same object).
    /// Low overlap = representation is shifting (new features or new object).
    /// </summary>
    public int OverlapWithPrevious { get; init; }

    /// <summary>
    /// True if this was the first activation (no prior representation).
    /// On first activation, a random sparse set is chosen.
    /// </summary>
    public bool IsInitialActivation { get; init; }
}
