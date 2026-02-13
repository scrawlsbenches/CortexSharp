// ============================================================================
// CortexSharp — Cortical Region Implementation
// ============================================================================
// Orchestrates multiple cortical columns + lateral voting.
//
// Processing pipeline per sensory sample:
//   Phase 1 — Independent column computation (ONCE):
//     For each column:
//       1. L6 path integration (motor displacement)
//       2. L6 anchoring (landmark recognition)
//       3. L4 SP (active minicolumns from sensory input)
//       4. L4 TM (active cells with location + context)
//       5. L2/3 ColumnPooler (initial object representation)
//
//   Phase 2 — Lateral voting loop (REPEATED):
//     Repeat until convergence or max iterations:
//       1. Collect L2/3 representations from all columns
//       2. Compute consensus (bits supported by enough columns)
//       3. Feed consensus back to each column via ApplyLateralNarrowing
//          (this ONLY updates L2/3 — does NOT re-run L4 or L6)
//       4. Check convergence (average pairwise similarity > threshold)
//
//   Phase 3 — Report result:
//     Return consensus + convergence status + per-column outputs
//
// Critical: the voting loop uses ApplyLateralNarrowing(), NOT Compute().
// Re-running Compute() would corrupt L4 temporal state and L6 position.
// The voting loop is pure L2/3 candidate elimination.
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
    private ColumnOutput[] _lastColumnOutputs;

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
        _lastColumnOutputs = Array.Empty<ColumnOutput>();
    }

    public SDR Consensus => _consensus;
    public bool HasConverged => _hasConverged;
    public int ColumnCount => _columns.Length;

    public ICorticalColumn GetColumn(int index) => _columns[index];

    public RegionOutput Process(SensoryInput[] inputs, bool learn)
    {
        // =================================================================
        // Validate sensory input
        // =================================================================
        if (inputs.Length != _columns.Length && inputs.Length != 1)
            throw new ArgumentException(
                $"Expected {_columns.Length} sensory inputs (one per column) " +
                $"or 1 (broadcast), got {inputs.Length}");

        // =================================================================
        // Phase 1: Independent column computation (ONCE)
        // =================================================================
        // Each column runs its full L6 → L4 → L2/3 pipeline independently.
        // This is embarrassingly parallel in biology — each column operates
        // on its own sensory patch without waiting for neighbors.

        // Distribute hierarchical feedback to all columns before computing
        if (_hierarchicalFeedback != null)
        {
            for (int i = 0; i < _columns.Length; i++)
                _columns[i].ReceiveApicalInput(_hierarchicalFeedback);
            _hierarchicalFeedback = null;
        }

        var columnOutputs = new ColumnOutput[_columns.Length];
        for (int i = 0; i < _columns.Length; i++)
        {
            var input = inputs.Length == 1 ? inputs[0] : inputs[i];
            columnOutputs[i] = _columns[i].Compute(input, learn);
        }

        _lastColumnOutputs = columnOutputs;

        // =================================================================
        // Phase 2: Lateral voting loop
        // =================================================================
        var votingResult = RunVotingLoop();

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

    public RegionOutput Settle()
    {
        // Re-run the voting loop WITHOUT processing new sensory input.
        // Used during hierarchical settling when top-down feedback has changed.

        // If hierarchical feedback arrived, distribute to columns first
        if (_hierarchicalFeedback != null)
        {
            for (int i = 0; i < _columns.Length; i++)
                _columns[i].ReceiveApicalInput(_hierarchicalFeedback);
            _hierarchicalFeedback = null;
        }

        var votingResult = RunVotingLoop();

        float avgAnomaly = 0f;
        for (int i = 0; i < _lastColumnOutputs.Length; i++)
            avgAnomaly += _lastColumnOutputs[i].Anomaly;
        if (_lastColumnOutputs.Length > 0)
            avgAnomaly /= _lastColumnOutputs.Length;

        return new RegionOutput
        {
            Consensus = _consensus,
            Converged = _hasConverged,
            AgreementScore = votingResult.AgreementScore,
            VotingIterations = votingResult.Iterations,
            ColumnOutputs = _lastColumnOutputs,
            AverageAnomaly = avgAnomaly,
        };
    }

    /// <summary>
    /// Run the lateral voting loop using ApplyLateralNarrowing.
    /// Collects column representations, computes consensus, feeds it back
    /// via L2/3 narrowing (NOT full Compute), and repeats until convergence.
    /// </summary>
    private VotingResult RunVotingLoop()
    {
        var initialVotes = new SDR[_columns.Length];
        for (int i = 0; i < _columns.Length; i++)
            initialVotes[i] = _columns[i].ObjectRepresentation;

        var votingResult = _voting.RunVotingLoop(
            initialVotes,
            consensus =>
            {
                // Build peer representation arrays for each column.
                // Each column needs representations from ALL OTHER columns.
                // For now, we pass all columns' representations to each
                // column and let ApplyLateralNarrowing handle it.
                var allRepresentations = new SDR[_columns.Length];
                for (int i = 0; i < _columns.Length; i++)
                    allRepresentations[i] = _columns[i].ObjectRepresentation;

                // Apply lateral narrowing to each column
                for (int i = 0; i < _columns.Length; i++)
                {
                    // Build peer representations (all columns except this one)
                    var peers = new SDR[_columns.Length - 1];
                    int peerIdx = 0;
                    for (int j = 0; j < _columns.Length; j++)
                    {
                        if (j != i)
                            peers[peerIdx++] = allRepresentations[j];
                    }
                    _columns[i].ApplyLateralNarrowing(peers);
                }

                // Collect updated representations after narrowing
                var updated = new SDR[_columns.Length];
                for (int i = 0; i < _columns.Length; i++)
                    updated[i] = _columns[i].ObjectRepresentation;
                return updated;
            });

        _consensus = votingResult.Consensus;
        _hasConverged = votingResult.Converged;
        return votingResult;
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
        _lastColumnOutputs = Array.Empty<ColumnOutput>();
    }
}
