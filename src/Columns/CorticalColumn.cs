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
//        - feedforwardGrowthCandidates: L4 winner cells
//        - lateral: peer column representations (if available)
//        - apical: higher region feedback (if available)
//   6. Column exposes L2/3 representation for lateral voting
//
// Two-phase design:
//   Compute() runs the full pipeline once per sensory sample.
//   ApplyLateralNarrowing() runs only L2/3 candidate elimination
//   during iterative voting, without touching L4 or L6.
//
//   This separation prevents:
//   - Re-running path integration (would corrupt L6 position)
//   - Re-running TM (would corrupt temporal sequence state)
//   - Double-learning (would over-strengthen synapses)
// ============================================================================

using CortexSharp.Core;
using CortexSharp.Layers;
using CortexSharp.Location;

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
    private readonly IDisplacementModule? _displacementModule;

    // --- Inter-layer communication state ---
    private SDR? _apicalFeedback;
    private SDR? _prevL23Representation;
    private SDR? _previousLocationSDR;  // For displacement learning

    public CorticalColumn(
        CorticalColumnConfig config,
        IInputLayer inputLayer,
        IObjectLayer objectLayer,
        ILocationLayer locationLayer,
        IDisplacementModule? displacementModule = null)
    {
        _config = config;
        _inputLayer = inputLayer;
        _objectLayer = objectLayer;
        _locationLayer = locationLayer;
        _displacementModule = displacementModule;
    }

    public IInputLayer InputLayer => _inputLayer;
    public IObjectLayer ObjectLayer => _objectLayer;
    public ILocationLayer LocationLayer => _locationLayer;
    public IDisplacementModule? DisplacementModule => _displacementModule;

    public SDR ObjectRepresentation =>
        _objectLayer.Representation;

    public ColumnOutput Compute(SensoryInput input, bool learn)
    {
        // =================================================================
        // Step 1: L6 — Update location via path integration
        // =================================================================
        // The motor displacement is integrated by each grid cell module
        // independently. This gives us the new location in object-centric
        // coordinates.
        _locationLayer.Move(input.DeltaX, input.DeltaY);

        // =================================================================
        // Step 2: L6 — Anchor if landmark recognized
        // =================================================================
        // Compare sensory input against stored anchors. If a match is
        // found, correct path integration drift by snapping to the
        // remembered position.
        bool anchored = _locationLayer.Anchor(input.FeatureSDR);

        var locationSDR = _locationLayer.LocationSDR;

        // =================================================================
        // Step 2b: Displacement learning
        // =================================================================
        // If we have a displacement module and a previous location, learn
        // the structural relationship between consecutive touch locations.
        if (learn && _displacementModule != null && _previousLocationSDR != null)
        {
            _displacementModule.Learn(_previousLocationSDR, locationSDR);
        }
        _previousLocationSDR = locationSDR;

        // =================================================================
        // Step 3: L4 SP — Compute active minicolumns
        // =================================================================
        // Pure feedforward: which minicolumns best match the sensory input
        // via their proximal synapses? Top ~2% survive inhibition.
        var activeColumns = _inputLayer.ComputeActiveColumns(input.FeatureSDR, learn);

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
        // representation. Winner cells from L4 serve as growth candidates
        // for new feedforward synapses (born connected).
        //
        // Lateral inputs are NOT provided during Compute() — they arrive
        // later via ApplyLateralNarrowing() during the voting loop.
        var objectOutput = _objectLayer.Compute(
            feedforwardInput: tmOutput.ActiveCells,
            feedforwardGrowthCandidates: tmOutput.WinnerCells,
            lateralInputs: null,  // Will be provided via ApplyLateralNarrowing
            apicalInput: _apicalFeedback,
            learn: learn);

        // Save L2/3 representation for L4 apical feedback next timestep
        _prevL23Representation = objectOutput.Representation;

        // Clear single-use apical input after consumption
        _apicalFeedback = null;

        return new ColumnOutput
        {
            ActiveColumns = activeColumns,
            ActiveCells = tmOutput.ActiveCells,
            WinnerCells = tmOutput.WinnerCells,
            PredictedCells = tmOutput.PredictedCells,
            Anomaly = tmOutput.Anomaly,
            BurstingColumnCount = tmOutput.BurstingColumnCount,
            ObjectRepresentation = objectOutput.Representation,
            RepresentationStability = objectOutput.OverlapWithPrevious,
            LocationSDR = locationSDR,
            Anchored = anchored,
        };
    }

    public void ApplyLateralNarrowing(SDR[] peerRepresentations)
    {
        // Delegate directly to L2/3. This does NOT re-run L4 or L6.
        // It only narrows the L2/3 representation by intersecting with
        // laterally-supported cells.
        _objectLayer.ApplyLateralNarrowing(peerRepresentations);
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
        // L4: temporal context cleared (no sequence history)
        // L2/3: object representation cleared (no inertia)
        // Displacement: learned structure retained, but tracking state cleared
        // Inter-layer state: cleared
        _locationLayer.Reset();
        _inputLayer.Reset();
        _objectLayer.Reset();
        _displacementModule?.Reset();
        _apicalFeedback = null;
        _prevL23Representation = null;
        _previousLocationSDR = null;
    }
}
