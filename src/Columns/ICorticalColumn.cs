// ============================================================================
// CortexSharp — Cortical Column Interface
// ============================================================================
// A cortical column is the fundamental processing unit of the neocortex.
// In the Thousand Brains model, each column is a COMPLETE sensory-motor
// unit that learns complete models of every object it encounters.
//
// Structure:
//   A column is composed of cortical layers, each with a distinct function:
//
//   ┌─────────────────────────────────────────────────────────┐
//   │  L1 — Apical target zone                               │
//   │       Top-down feedback from higher regions arrives here│
//   │       Apical dendrites of L2/3 and L5 cells extend up  │
//   ├─────────────────────────────────────────────────────────┤
//   │  L2/3 — Object Layer (ColumnPooler)                    │
//   │         Learns/stores object models as stable SDRs      │
//   │         Receives: feedforward from L4, lateral from     │
//   │         peer columns, apical from higher regions        │
//   │         Sends: lateral votes, feedforward to higher     │
//   ├─────────────────────────────────────────────────────────┤
//   │  L4 — Input Layer (SpatialPooler + TemporalMemory)     │
//   │       Receives: sensory input (proximal), location      │
//   │       from L6 (basal), feedback from L2/3 (apical)     │
//   │       Produces: active cells encoding feature-at-loc    │
//   ├─────────────────────────────────────────────────────────┤
//   │  L5 — Output Layer (future)                             │
//   │       Motor commands to subcortical structures          │
//   ├─────────────────────────────────────────────────────────┤
//   │  L6 — Location Layer (Grid Cell Modules)                │
//   │       Maintains object-centric reference frame          │
//   │       Path integration + landmark anchoring             │
//   └─────────────────────────────────────────────────────────┘
//
// Data flow within a column (per sensory sample):
//
//   1. L6: Integrate motor displacement → update location SDR
//   2. L6: Anchor to sensory input if landmark recognized
//   3. L4 SP: Compute active minicolumns from sensory input
//   4. L4 TM: Compute active cells (with L6 location as basal input,
//             L2/3 feedback as apical input)
//   5. L2/3: Compute object representation from L4 output
//            (with lateral consensus + apical feedback from higher region)
//   6. Column emits its L2/3 representation for lateral voting
//
// Each column processes ONE sensory patch (one fingertip, one foveal
// fixation). Multiple columns sensing different patches of the same
// object vote laterally to converge on the object's identity.
//
// Reference: Hawkins et al. (2017), "A Theory of How Columns..."
//            Hawkins et al. (2019), "A Framework for Intelligence..."
// ============================================================================

using CortexSharp.Core;
using CortexSharp.Layers;

namespace CortexSharp.Columns;

/// <summary>
/// A cortical column — the fundamental processing unit.
/// Composed of L4 (input), L2/3 (object), and L6 (location).
/// </summary>
public interface ICorticalColumn
{
    // =========================================================================
    // Core computation
    // =========================================================================

    /// <summary>
    /// Process one sensory sample: a feature at a displacement from the
    /// previous position. This runs the full L6 → L4 → L2/3 pipeline.
    /// </summary>
    /// <param name="sensoryInput">
    /// Encoded sensory SDR — what the sensor is detecting at this moment
    /// (e.g., curvature, texture, color at one point on the object).
    /// </param>
    /// <param name="deltaX">Sensor displacement in X since last sample.</param>
    /// <param name="deltaY">Sensor displacement in Y since last sample.</param>
    /// <param name="learn">If true, learn at all layers.</param>
    /// <returns>Output from all layers of the column.</returns>
    ColumnOutput Compute(SDR sensoryInput, float deltaX, float deltaY, bool learn);

    // =========================================================================
    // Lateral communication (between peer columns in the same region)
    // =========================================================================

    /// <summary>
    /// Receive the consensus SDR from lateral voting. The column intersects
    /// this with its own L2/3 representation to narrow candidates.
    /// </summary>
    /// <param name="consensus">Consensus object representation from voting.</param>
    void ReceiveLateralInput(SDR consensus);

    // =========================================================================
    // Hierarchical communication (between regions)
    // =========================================================================

    /// <summary>
    /// Receive top-down feedback from a higher cortical region.
    /// This feeds into L2/3 (apical) and optionally L4 (apical).
    /// Modulatory — biases activation without driving it.
    /// </summary>
    /// <param name="feedback">Apical SDR from the higher region.</param>
    void ReceiveApicalInput(SDR feedback);

    // =========================================================================
    // State accessors
    // =========================================================================

    /// <summary>
    /// The L2/3 object representation — this column's current best guess
    /// at what object is being sensed. This is what gets broadcast
    /// laterally for voting.
    /// </summary>
    SDR ObjectRepresentation { get; }

    /// <summary>Access the Input Layer (L4) directly.</summary>
    IInputLayer InputLayer { get; }

    /// <summary>Access the Object Layer (L2/3) directly.</summary>
    IObjectLayer ObjectLayer { get; }

    /// <summary>Access the Location Layer (L6) directly.</summary>
    ILocationLayer LocationLayer { get; }

    /// <summary>
    /// Reset the column for a new object. Clears L2/3 representation,
    /// resets L6 location to origin, resets L4 temporal context.
    /// </summary>
    void Reset();
}

/// <summary>
/// Output from a single column compute step.
/// </summary>
public record ColumnOutput
{
    // --- L4 outputs ---

    /// <summary>Active minicolumns from L4 Spatial Pooler.</summary>
    public required SDR ActiveColumns { get; init; }

    /// <summary>Active cells from L4 Temporal Memory.</summary>
    public required SDR ActiveCells { get; init; }

    /// <summary>Anomaly score from L4 (fraction of bursting columns).</summary>
    public float Anomaly { get; init; }

    // --- L2/3 outputs ---

    /// <summary>Object representation from L2/3 ColumnPooler.</summary>
    public required SDR ObjectRepresentation { get; init; }

    /// <summary>Overlap of L2/3 representation with previous timestep.</summary>
    public int RepresentationStability { get; init; }

    // --- L6 outputs ---

    /// <summary>Current location SDR from L6 grid cells.</summary>
    public required SDR LocationSDR { get; init; }
}

/// <summary>
/// Configuration for a cortical column.
/// </summary>
public record CorticalColumnConfig
{
    // --- L4 Input Layer ---

    /// <summary>Size of the sensory input SDR.</summary>
    public int InputSize { get; init; } = 2048;

    /// <summary>Number of minicolumns in L4 Spatial Pooler.</summary>
    public int L4ColumnCount { get; init; } = 2048;

    /// <summary>Cells per minicolumn in L4 Temporal Memory.</summary>
    public int CellsPerColumn { get; init; } = 32;

    /// <summary>Target sparsity for L4 SP output (~2%).</summary>
    public float L4TargetSparsity { get; init; } = 0.02f;

    // --- L2/3 Object Layer ---

    /// <summary>Number of cells in L2/3 ColumnPooler.</summary>
    public int L23CellCount { get; init; } = 4096;

    /// <summary>Target active cells in L2/3 representation.</summary>
    public int L23ActiveCells { get; init; } = 40;

    /// <summary>
    /// Initial permanence for new feedforward synapses in L2/3.
    /// Must be ABOVE connected threshold (born connected).
    /// </summary>
    public float L23InitialPermanence { get; init; } = 0.6f;

    // --- L6 Location Layer ---

    /// <summary>Number of grid cell modules per column.</summary>
    public int GridModuleCount { get; init; } = 3;

    /// <summary>Grid dimension per module (ModuleSize x ModuleSize).</summary>
    public int GridModuleSize { get; init; } = 10;

    // --- Dendritic thresholds ---

    /// <summary>Min active synapses to activate a distal segment (TM).</summary>
    public int DistalActivationThreshold { get; init; } = 13;

    /// <summary>Min active synapses to activate an apical segment.</summary>
    public int ApicalActivationThreshold { get; init; } = 6;

    /// <summary>Min active synapses to activate a basal segment (location).</summary>
    public int BasalActivationThreshold { get; init; } = 10;

    /// <summary>Permanence threshold for a synapse to be "connected."</summary>
    public float ConnectedThreshold { get; init; } = 0.5f;
}
