// ============================================================================
// CortexSharp — Column Pooler Interface
// ============================================================================
// The Column Pooler is the L2/3 algorithm in the Thousand Brains model.
// It learns stable object representations by associating feedforward input
// (from L4) with the column's current representation, reinforced by lateral
// consensus from peer columns and top-down feedback from higher regions.
//
// How it differs from Temporal Memory:
//   TM learns SEQUENCES — each timestep transitions to a new representation.
//   ColumnPooler learns OBJECTS — the representation PERSISTS across multiple
//   sensory samples of the same object. This stability is achieved through
//   INERTIA: cells that are already active are preferred over new candidates.
//
// Key design principles (from Numenta's reference):
//
//   1. BORN CONNECTED: New synapses are initialized with permanence ABOVE
//      the connected threshold (e.g., 0.6 when threshold is 0.5). This means
//      a single learning event creates a functional connection immediately.
//      Unlike TM where synapses start below threshold and must be reinforced
//      multiple times, ColumnPooler synapses work from the first exposure.
//      This is critical for one-shot object learning.
//
//   2. RANDOM INITIAL ACTIVATION: When no feedforward input matches any
//      existing representation, a random sparse set of L2/3 cells is activated.
//      This becomes the seed of the new object's representation. The specific
//      cells chosen don't matter — what matters is that the same cells are
//      consistently reactivated when the same object is encountered again.
//
//   3. INERTIA VIA DISTAL SELF-REINFORCEMENT: Active cells maintain their
//      activation through distal connections to themselves (from previous
//      timestep). This self-reinforcement means the representation persists
//      across multiple sensory samples. A new feedforward input that's
//      consistent with the current representation reinforces it; an input
//      that contradicts it weakens it.
//
//   4. LATERAL NARROWING: When lateral input arrives from peer columns,
//      only cells supported by BOTH the local representation AND the
//      consensus survive. This implements candidate elimination — the
//      mechanism by which recognition converges across columns.
//
//   5. CONVERGENCE IS RECOGNITION: The system has recognized an object
//      when all columns converge to the same representation. No separate
//      matching step against a library is needed. The converged
//      representation IS the recognized object.
//
// Relationship to IObjectLayer:
//   IColumnPooler extends IObjectLayer — it IS the algorithm behind L2/3.
//   IObjectLayer defines the layer-level contract (what a column needs).
//   IColumnPooler adds algorithm-specific accessors (cell count, target
//   active cells, diagnostics). Any IColumnPooler can be used as an
//   IObjectLayer, which is how it's injected into CorticalColumn.
//
// Reference: Numenta ColumnPooler implementation (column_pooler.py)
//            Hawkins et al. (2017), "A Theory of How Columns in the
//            Neocortex Enable Learning the Structure of the World"
// ============================================================================

using CortexSharp.Core;
using CortexSharp.Layers;

namespace CortexSharp.Algorithms;

/// <summary>
/// Column Pooler — stable object representation learning for L2/3.
/// Extends <see cref="IObjectLayer"/> with algorithm-specific diagnostics.
/// </summary>
public interface IColumnPooler : IObjectLayer
{
    // =========================================================================
    // Algorithm-specific accessors
    // =========================================================================
    // These go beyond what the layer contract requires. They expose internals
    // useful for testing, tuning, and monitoring the learning process.
    // =========================================================================

    /// <summary>
    /// Number of L2/3 cells in this pooler.
    /// </summary>
    int CellCount { get; }

    /// <summary>
    /// Target number of active cells in the representation (~40).
    /// </summary>
    int TargetActiveCells { get; }
}

/// <summary>
/// Output from a single ColumnPooler compute step.
/// Extends ObjectLayerOutput with algorithm-specific diagnostics.
/// </summary>
public record ColumnPoolerOutput
{
    /// <summary>Active L2/3 cells — the object representation.</summary>
    public required SDR Representation { get; init; }

    /// <summary>Overlap between current and previous representation (inertia measure).</summary>
    public int OverlapWithPrevious { get; init; }

    /// <summary>Number of cells activated by feedforward match.</summary>
    public int FeedforwardActivatedCount { get; init; }

    /// <summary>Number of cells retained via inertia (self-reinforcement).</summary>
    public int InertiaRetainedCount { get; init; }

    /// <summary>True if representation was seeded randomly (no feedforward match = novel input).</summary>
    public bool IsNovelActivation { get; init; }
}
