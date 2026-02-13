// ============================================================================
// CortexSharp — Temporal Memory Interface
// ============================================================================
// The Temporal Memory models sequence learning in the neocortex. It operates
// on the cell level WITHIN minicolumns determined by the Spatial Pooler.
// All cells in a minicolumn share the same feedforward receptive field,
// but each cell has its own distal dendrite segments, so each cell represents
// the same feature in a DIFFERENT temporal/spatial context.
//
// Dendritic pathways:
//   The TM in the Thousand Brains model has three input pathways:
//
//   DISTAL (sequence context):
//     Lateral connections from previously active cells in the same region.
//     "What came before?" — encodes temporal context / sequence position.
//     A cell with active distal input is PREDICTED (depolarized).
//
//   BASAL (location context):
//     External input from L6 grid cells carrying the location signal.
//     "Where on the object am I?" — encodes spatial context.
//     This is the Thousand Brains extension that allows L4 cells to
//     represent "feature at location" rather than just "feature."
//
//   APICAL (top-down feedback):
//     Input from L2/3 or higher regions carrying object-level context.
//     "What object do I think this is?" — provides expectation/attention.
//     Modulatory — biases cell selection without driving activation.
//
// The two-timestep state machine:
//   1. SAVE: Copy current state to previous (prevActiveCells, prevWinnerCells)
//   2. CACHE: Evaluate segments against PREVIOUS active cells → which cells
//      are currently predicted/depolarized
//   3. ACTIVATE: For each active column:
//      - If any cell was predicted → activate only that cell (CONFIRMED)
//      - If no cell predicted → activate ALL cells (BURST = surprise)
//   4. ANOMALY: Count bursting columns / total active columns
//   5. LEARN: Reinforce correct predictions, grow segments on bursting
//      columns, punish incorrect predictions. All learning references
//      PREVIOUS state (backward-looking).
//   6. PREDICT: Evaluate segments against NEWLY active cells → which cells
//      are now predicted for the NEXT timestep (forward-looking).
//
// CRITICAL: Learning looks BACKWARD. Prediction looks FORWARD.
// This is the most common source of implementation bugs.
//
// Reference: Hawkins & Ahmad (2016), "Why Neurons Have Thousands of Synapses,
//            a Theory of Sequence Memory in Neocortex"
// ============================================================================

using CortexSharp.Core;

namespace CortexSharp.Algorithms;

/// <summary>
/// Temporal Memory — sequence learning via distal, basal, and apical dendrites.
/// </summary>
public interface ITemporalMemory
{
    /// <summary>
    /// Run one compute cycle of the temporal memory.
    /// </summary>
    /// <param name="activeColumns">
    /// Active minicolumns from the Spatial Pooler. Determines WHICH columns
    /// participate; the TM determines which CELLS within those columns fire.
    /// </param>
    /// <param name="basalInput">
    /// Location signal from L6 grid cells. Null if location context is
    /// unavailable. When provided, cells can form segments that recognize
    /// specific locations, enabling "feature at location" representations.
    /// </param>
    /// <param name="apicalInput">
    /// Top-down feedback from L2/3 or higher regions. Null if unavailable.
    /// Modulatory — biases cell selection priority without driving activation.
    /// </param>
    /// <param name="learn">
    /// If true, perform Hebbian learning:
    ///   - Reinforce segments that correctly predicted active cells
    ///   - Grow new segments on winner cells of bursting columns
    ///   - Punish segments that predicted cells in non-active columns
    /// </param>
    /// <returns>Output including active cells, predictions, and anomaly.</returns>
    TemporalMemoryOutput Compute(
        SDR activeColumns,
        SDR? basalInput,
        SDR? apicalInput,
        bool learn);

    // =========================================================================
    // State accessors
    // =========================================================================

    /// <summary>Currently active cells (this timestep).</summary>
    SDR ActiveCells { get; }

    /// <summary>
    /// Winner cells — one per active column, selected for learning.
    /// In predicted columns: the predicted cell.
    /// In bursting columns: the cell with the best matching segment,
    /// or the least recently used cell if no segments match.
    /// </summary>
    SDR WinnerCells { get; }

    /// <summary>
    /// Cells predicted for the NEXT timestep. These cells are depolarized
    /// by their distal/basal segments recognizing the current active cells.
    /// </summary>
    SDR PredictedCells { get; }

    /// <summary>Total number of minicolumns.</summary>
    int ColumnCount { get; }

    /// <summary>Cells per minicolumn (typically 32).</summary>
    int CellsPerColumn { get; }

    /// <summary>Total distal dendrite segments across all cells.</summary>
    int TotalSegmentCount { get; }

    /// <summary>Total synapses across all segments.</summary>
    int TotalSynapseCount { get; }
}

/// <summary>
/// Output from a single TM compute step.
/// </summary>
public record TemporalMemoryOutput
{
    /// <summary>Active cells this timestep.</summary>
    public required SDR ActiveCells { get; init; }

    /// <summary>Winner cells selected for learning.</summary>
    public required SDR WinnerCells { get; init; }

    /// <summary>Cells predicted for next timestep.</summary>
    public required SDR PredictedCells { get; init; }

    /// <summary>Anomaly score [0, 1]. Fraction of columns that burst.</summary>
    public float Anomaly { get; init; }

    /// <summary>Number of columns that burst.</summary>
    public int BurstingColumnCount { get; init; }

    /// <summary>Number of columns where prediction was confirmed.</summary>
    public int PredictedActiveColumnCount { get; init; }
}
