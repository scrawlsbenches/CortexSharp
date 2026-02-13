// ============================================================================
// CortexSharp — Cortical Column Implementation
// ============================================================================
// Composes L4 (input), L2/3 (object), and L6 (location) into the
// complete column processing pipeline.
//
// Computation order per sensory sample:
//   1. L6 integrates motor displacement → new location
//   2. L6 anchors to sensory landmark if recognized
//   3. L4 SP computes active minicolumns from sensory input
//   4. L4 TM computes active cells with:
//        - distal: previous active cells (sequence context)
//        - basal: L6 location SDR (spatial context)
//        - apical: L2/3 feedback (object expectation)
//   5. L2/3 ColumnPooler computes object representation from:
//        - feedforward: L4 active cells
//        - lateral: peer column consensus (if available)
//        - apical: higher region feedback (if available)
//   6. Column exposes L2/3 representation for lateral voting
// ============================================================================

using CortexSharp.Core;
using CortexSharp.Layers;

namespace CortexSharp.Columns;

/// <summary>
/// Standard cortical column implementation.
/// </summary>
public class CorticalColumn : ICorticalColumn
{
    private readonly CorticalColumnConfig _config;

    // --- Layer implementations (injected or constructed) ---
    private readonly IInputLayer _inputLayer;       // L4
    private readonly IObjectLayer _objectLayer;     // L2/3
    private readonly ILocationLayer _locationLayer;  // L6

    // --- Inter-layer communication state ---
    private SDR? _lateralConsensus;
    private SDR? _apicalFeedback;
    private SDR? _prevL23Representation;

    public CorticalColumn(
        CorticalColumnConfig config,
        IInputLayer inputLayer,
        IObjectLayer objectLayer,
        ILocationLayer locationLayer)
    {
        _config = config;
        _inputLayer = inputLayer;
        _objectLayer = objectLayer;
        _locationLayer = locationLayer;
    }

    // TODO: Add convenience constructor that builds default layer
    // implementations from config. This constructor takes pre-built
    // layers for testability (dependency injection).

    public IInputLayer InputLayer => _inputLayer;
    public IObjectLayer ObjectLayer => _objectLayer;
    public ILocationLayer LocationLayer => _locationLayer;

    public SDR ObjectRepresentation =>
        _objectLayer.Representation;

    public ColumnOutput Compute(SDR sensoryInput, float deltaX, float deltaY, bool learn)
    {
        // =================================================================
        // Step 1: L6 — Update location via path integration
        // =================================================================
        // The motor displacement (deltaX, deltaY) is integrated by each
        // grid cell module independently. This gives us the new location
        // in object-centric coordinates.
        _locationLayer.Move(deltaX, deltaY);

        // =================================================================
        // Step 2: L6 — Anchor if landmark recognized
        // =================================================================
        // Compare sensory input against stored anchors. If a match is
        // found, correct path integration drift by snapping to the
        // remembered position.
        _locationLayer.Anchor(sensoryInput);

        var locationSDR = _locationLayer.LocationSDR;

        // =================================================================
        // Step 3: L4 SP — Compute active minicolumns
        // =================================================================
        // Pure feedforward: which minicolumns best match the sensory input
        // via their proximal synapses? Top ~2% survive inhibition.
        var activeColumns = _inputLayer.ComputeActiveColumns(sensoryInput, learn);

        // =================================================================
        // Step 4: L4 TM — Compute active cells
        // =================================================================
        // Within active columns, determine which specific cells fire.
        // Three context signals:
        //   - distal: handled internally (previous active cells)
        //   - basal: location from L6 (WHERE on the object)
        //   - apical: previous L2/3 representation (WHAT object expectation)
        var tmOutput = _inputLayer.ComputeActiveCells(
            activeColumns,
            basalInput: locationSDR,
            apicalInput: _prevL23Representation,
            learn: learn);

        // =================================================================
        // Step 5: L2/3 — Compute object representation
        // =================================================================
        // The ColumnPooler takes L4's cell-level output (encoding
        // "feature at location") and produces a stable object
        // representation, influenced by lateral consensus and
        // top-down feedback.
        var objectOutput = _objectLayer.Compute(
            feedforwardInput: tmOutput.ActiveCells,
            lateralInput: _lateralConsensus,
            apicalInput: _apicalFeedback,
            learn: learn);

        // Save L2/3 representation for L4 apical feedback next timestep
        _prevL23Representation = objectOutput.Representation;

        // Clear single-use lateral/apical inputs after consumption
        _lateralConsensus = null;
        _apicalFeedback = null;

        return new ColumnOutput
        {
            ActiveColumns = activeColumns,
            ActiveCells = tmOutput.ActiveCells,
            Anomaly = tmOutput.Anomaly,
            ObjectRepresentation = objectOutput.Representation,
            RepresentationStability = objectOutput.OverlapWithPrevious,
            LocationSDR = locationSDR,
        };
    }

    public void ReceiveLateralInput(SDR consensus)
    {
        // Stored until the next Compute() call, where it's passed
        // to L2/3 as lateral input for candidate narrowing.
        _lateralConsensus = consensus;
    }

    public void ReceiveApicalInput(SDR feedback)
    {
        // Stored until the next Compute() call, where it's passed
        // to L2/3 as apical input for top-down modulation.
        _apicalFeedback = feedback;
    }

    public void Reset()
    {
        // Reset all layers for a new object.
        // L6: position returns to origin (0,0)
        // L2/3: object representation cleared (no inertia)
        // L4: temporal context cleared (no sequence history)
        // Inter-layer state: cleared
        _locationLayer.Reset();
        _objectLayer.Reset();
        _lateralConsensus = null;
        _apicalFeedback = null;
        _prevL23Representation = null;

        // TODO: L4 TM reset — the IInputLayer interface needs a
        // Reset() method, or TM should detect the new object context
        // via the L2/3 representation change.
    }
}
