// ============================================================================
// CortexSharp — Cortical Region Implementation
// ============================================================================
// Orchestrates multiple cortical columns + lateral voting.
//
// Processing pipeline per sensory sample:
//   Phase 1 — Independent column computation:
//     For each column in parallel:
//       1. L6 path integration (motor displacement)
//       2. L6 anchoring (landmark recognition)
//       3. L4 SP (active minicolumns from sensory input)
//       4. L4 TM (active cells with location + context)
//       5. L2/3 ColumnPooler (object representation)
//
//   Phase 2 — Lateral voting loop:
//     Repeat until convergence or max iterations:
//       1. Collect L2/3 representations from all columns
//       2. Compute consensus (bits supported by enough columns)
//       3. Feed consensus back to each column
//       4. Each column intersects consensus with its representation
//       5. Check convergence (average pairwise similarity > threshold)
//
//   Phase 3 — Report result:
//     Return consensus + convergence status + per-column outputs
// ============================================================================

using CortexSharp.Algorithms;
using CortexSharp.Columns;
using CortexSharp.Core;

namespace CortexSharp.Regions;

/// <summary>
/// Standard cortical region implementation.
/// </summary>
public class CorticalRegion : ICorticalRegion
{
    private readonly CorticalRegionConfig _config;
    private readonly ICorticalColumn[] _columns;
    private readonly ILateralVoting _voting;

    private SDR _consensus;
    private bool _hasConverged;
    private SDR? _hierarchicalFeedback;

    public CorticalRegion(
        CorticalRegionConfig config,
        ICorticalColumn[] columns,
        ILateralVoting voting)
    {
        if (columns.Length != config.ColumnCount)
            throw new ArgumentException(
                $"Expected {config.ColumnCount} columns, got {columns.Length}");

        _config = config;
        _columns = columns;
        _voting = voting;
        _consensus = new SDR(config.ColumnConfig.L23CellCount);
    }

    // TODO: Add convenience constructor that builds columns + voting
    // from config alone. This constructor takes pre-built components
    // for testability.

    public SDR Consensus => _consensus;
    public bool HasConverged => _hasConverged;
    public int ColumnCount => _columns.Length;

    public ICorticalColumn GetColumn(int index) => _columns[index];

    public RegionOutput Process(
        SDR[] sensoryPatches,
        float deltaX,
        float deltaY,
        bool learn)
    {
        // =================================================================
        // Validate sensory input
        // =================================================================
        // Each column processes its OWN sensory patch. The number of
        // patches must match the number of columns exactly, or be 1
        // (broadcast mode for single-sensor setups).
        if (sensoryPatches.Length != _columns.Length && sensoryPatches.Length != 1)
            throw new ArgumentException(
                $"Expected {_columns.Length} sensory patches (one per column) " +
                $"or 1 (broadcast), got {sensoryPatches.Length}");

        // =================================================================
        // Phase 1: Independent column computation
        // =================================================================
        // Each column runs its full L6 → L4 → L2/3 pipeline independently.
        // This is embarrassingly parallel in biology — each column operates
        // on its own sensory patch without waiting for neighbors.

        // TODO: Consider Parallel.For for multi-column parallelism.
        // The columns are independent at this phase.

        var columnOutputs = new ColumnOutput[_columns.Length];
        for (int i = 0; i < _columns.Length; i++)
        {
            // Distribute hierarchical feedback to all columns
            if (_hierarchicalFeedback != null)
                _columns[i].ReceiveApicalInput(_hierarchicalFeedback);

            var patch = sensoryPatches.Length == 1
                ? sensoryPatches[0]
                : sensoryPatches[i];

            columnOutputs[i] = _columns[i].Compute(patch, deltaX, deltaY, learn);
        }

        // Clear single-use hierarchical feedback
        _hierarchicalFeedback = null;

        // =================================================================
        // Phase 2: Lateral voting loop
        // =================================================================
        // Columns exchange their L2/3 representations and iteratively
        // narrow candidates until consensus emerges.

        var initialVotes = new SDR[_columns.Length];
        for (int i = 0; i < _columns.Length; i++)
            initialVotes[i] = _columns[i].ObjectRepresentation;

        var votingResult = _voting.RunVotingLoop(
            initialVotes,
            consensus =>
            {
                // Feed consensus back to each column
                for (int i = 0; i < _columns.Length; i++)
                    _columns[i].ReceiveLateralInput(consensus);

                // Collect updated representations
                // NOTE: columns update their representation in-place
                // when they receive lateral input, so we just re-read.
                var updated = new SDR[_columns.Length];
                for (int i = 0; i < _columns.Length; i++)
                    updated[i] = _columns[i].ObjectRepresentation;
                return updated;
            });

        _consensus = votingResult.Consensus;
        _hasConverged = votingResult.Converged;

        // =================================================================
        // Phase 3: Compute aggregate metrics and return
        // =================================================================

        float avgAnomaly = 0f;
        for (int i = 0; i < columnOutputs.Length; i++)
            avgAnomaly += columnOutputs[i].Anomaly;
        avgAnomaly /= columnOutputs.Length;

        return new RegionOutput
        {
            Consensus = _consensus,
            Converged = _hasConverged,
            AgreementScore = votingResult.AgreementScore,
            VotingIterations = votingResult.Iterations,
            ColumnOutputs = columnOutputs,
            AverageAnomaly = avgAnomaly,
        };
    }

    public void ReceiveHierarchicalFeedback(SDR feedback)
    {
        _hierarchicalFeedback = feedback;
    }

    public void Reset()
    {
        for (int i = 0; i < _columns.Length; i++)
            _columns[i].Reset();

        _consensus = new SDR(_config.ColumnConfig.L23CellCount);
        _hasConverged = false;
        _hierarchicalFeedback = null;
    }
}
