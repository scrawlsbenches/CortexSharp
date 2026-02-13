// ============================================================================
// CortexSharp — Input Layer (Layer 4)
// ============================================================================
// Layer 4 is the primary feedforward input layer of a cortical column.
// It receives sensory data from the thalamus and produces a sparse
// representation of which minicolumns are active.
//
// Biological function:
//   1. Spatial Pooler (proximal dendrites): competitive inhibition selects
//      the top ~2% of minicolumns whose proximal synapses best match the
//      input. This produces stable, sparse column-level representations.
//
//   2. Temporal Memory (distal dendrites): within each active minicolumn,
//      specific cells are activated based on temporal context. If a cell
//      was predicted (depolarized by distal input from the previous
//      timestep), only that cell fires. If no cell was predicted, ALL
//      cells fire (burst) — signaling surprise and triggering learning.
//
// Thousand Brains extension:
//   In the Thousand Brains model, L4 also receives a LOCATION signal
//   from L6 grid cells via a basal dendritic pathway. This means L4's
//   cell-level output encodes "feature AT location" rather than just
//   "feature." This is critical — it's what allows L2/3 to learn
//   feature-location associations (object models).
//
//   Without basal location input, L4 can only say "I see an edge."
//   With it, L4 says "I see an edge at position (3,7) on the object."
//
// Data flow:
//   sensoryInput → SpatialPooler → activeColumns
//   activeColumns + locationContext(basal) + previousContext(distal) → TemporalMemory → activeCells
//
// Reference: Cui, Ahmad & Hawkins (2017), "The HTM Spatial Pooler"
//            Hawkins & Ahmad (2016), "Why Neurons Have Thousands of Synapses"
// ============================================================================

using CortexSharp.Core;

namespace CortexSharp.Layers;

/// <summary>
/// Layer 4 — feedforward input processing.
/// Combines spatial pooling (which columns) with temporal memory (which cells).
/// </summary>
public interface IInputLayer
{
    // =========================================================================
    // Spatial Pooler — "which minicolumns are active?"
    // =========================================================================
    // Maps the input SDR to a fixed-sparsity output SDR via competitive
    // inhibition on proximal dendrites. Similar inputs produce similar
    // column activations. The SP learns to form stable representations
    // through Hebbian permanence adjustment + homeostatic boosting.
    // =========================================================================

    /// <summary>
    /// Compute which minicolumns are active for this input.
    /// This is a pure feedforward computation — no temporal context.
    /// </summary>
    /// <param name="sensoryInput">Encoded sensory SDR from the thalamus.</param>
    /// <param name="learn">If true, adjust proximal permanences and boost factors.</param>
    /// <returns>SDR of active minicolumn indices.</returns>
    SDR ComputeActiveColumns(SDR sensoryInput, bool learn);

    // =========================================================================
    // Temporal Memory — "which cells within active columns?"
    // =========================================================================
    // Given active columns (from SP), determines which specific cells
    // fire based on:
    //   - Distal context: lateral connections from previously active cells
    //     (sequence memory — "what came before")
    //   - Basal context: location signal from L6 grid cells
    //     ("where on the object am I?")
    //   - Apical feedback: top-down signal from L2/3 object layer
    //     ("what object do I think this is?")
    //
    // Cell activation priority (categorical tiers):
    //   Tier 1: Predicted by distal + apical + basal (strongest context)
    //   Tier 2: Predicted by distal + basal (sequence + location)
    //   Tier 3: Predicted by distal only (sequence context)
    //   Tier 4: Burst — no cell predicted (surprise, triggers learning)
    //
    // Learning always looks BACKWARD (synapses to previous timestep).
    // Prediction always looks FORWARD (depolarization for next timestep).
    // =========================================================================

    /// <summary>
    /// Compute cell-level activations within the active columns.
    /// </summary>
    /// <param name="activeColumns">Active minicolumns from <see cref="ComputeActiveColumns"/>.</param>
    /// <param name="basalInput">
    /// Location signal from L6 grid cells. Null if no location context available.
    /// This is the Thousand Brains extension — allows cells to encode
    /// "feature at location" rather than just "feature."
    /// </param>
    /// <param name="apicalInput">
    /// Top-down feedback from L2/3 or higher regions. Null if unavailable.
    /// Modulatory — biases cell selection without driving activation.
    /// </param>
    /// <param name="learn">If true, reinforce/grow/punish distal and basal segments.</param>
    /// <returns>Detailed output including active cells, predictions, and anomaly.</returns>
    InputLayerOutput ComputeActiveCells(
        SDR activeColumns,
        SDR? basalInput,
        SDR? apicalInput,
        bool learn);

    // =========================================================================
    // State accessors
    // =========================================================================

    /// <summary>
    /// Cell-level SDR of currently active cells. This is the primary output
    /// that feeds forward to L2/3. Each active cell represents a specific
    /// (feature, context) combination.
    /// </summary>
    SDR ActiveCells { get; }

    /// <summary>
    /// Cells that are currently depolarized (predicted to become active
    /// if their column receives feedforward input next timestep).
    /// </summary>
    SDR PredictedCells { get; }

    /// <summary>
    /// Fraction of active columns that were NOT predicted. Range [0, 1].
    /// 0.0 = fully predicted (expected input).
    /// 1.0 = completely novel (total surprise).
    /// Falls naturally from the prediction mechanism — not a separate detector.
    /// </summary>
    float Anomaly { get; }

    /// <summary>
    /// Winner cells — one per active column, selected for learning.
    /// In predicted columns: the predicted cell.
    /// In bursting columns: the cell with the best matching segment,
    /// or the least recently used cell if no segments match.
    /// These are passed to L2/3 as feedforwardGrowthCandidates.
    /// </summary>
    SDR WinnerCells { get; }

    /// <summary>
    /// Reset temporal context for a new object. Clears previous active/winner
    /// cells so the next input is processed without sequence context from the
    /// previous object. Does NOT reset learned synapses — only ephemeral state.
    /// </summary>
    void Reset();
}

/// <summary>
/// Output from a single L4 compute step.
/// </summary>
public record InputLayerOutput
{
    /// <summary>Active minicolumns (from Spatial Pooler).</summary>
    public required SDR ActiveColumns { get; init; }

    /// <summary>Active cells within those columns (from Temporal Memory).</summary>
    public required SDR ActiveCells { get; init; }

    /// <summary>Winner cells selected for learning.</summary>
    public required SDR WinnerCells { get; init; }

    /// <summary>Cells predicted for next timestep.</summary>
    public required SDR PredictedCells { get; init; }

    /// <summary>Fraction of columns that burst (anomaly signal).</summary>
    public float Anomaly { get; init; }

    /// <summary>Number of columns that burst (no cell was predicted).</summary>
    public int BurstingColumnCount { get; init; }

    /// <summary>Number of columns where a predicted cell was confirmed.</summary>
    public int PredictedActiveColumnCount { get; init; }
}
