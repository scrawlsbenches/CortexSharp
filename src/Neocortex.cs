// ============================================================================
// CortexSharp — Neocortex
// ============================================================================
// The top-level entry point for the CortexSharp system. The Neocortex class
// manages one or more cortical regions in a hierarchy and provides the
// primary consumer API for object learning and recognition.
//
// Architecture:
//   The neocortex is organized as a hierarchy of regions:
//
//     Level 0 (Sensory):  Processes raw sensory input
//                         Each column sees one sensory patch
//                         Lateral voting within the region
//
//     Level 1 (Abstract): Receives Level 0's consensus as input
//                         Forms higher-level representations
//                         Feeds back to Level 0 as apical input
//
//     Level N:            Further abstraction levels as needed
//
//   Within each level, recognition works through lateral consensus.
//   Between levels, information flows both feedforward (up) and
//   feedback (down). The settling loop iterates until both levels
//   converge or max iterations are reached.
//
// Settling loop (hierarchical):
//   1. Level 0 processes sensory input → produces consensus (ONCE)
//   2. Level 1 processes Level 0 consensus → produces consensus (ONCE)
//   3. Level 1 consensus fed back to Level 0 as apical input
//   4. Level 0 SETTLES (re-votes without re-running L4/L6)
//   5. Level 1 SETTLES with updated Level 0 consensus
//   6. Check convergence at all levels
//   7. Repeat 3-6 until settled or max iterations
//
//   CRITICAL: The settling loop uses Settle(), NOT Process().
//   Process() re-runs L4 and L6, which would corrupt temporal state
//   and path integration. Settle() only re-runs L2/3 lateral voting
//   with updated top-down context — no feedforward re-processing.
//
// Consumer API:
//   The Neocortex provides high-level methods for:
//     - Processing a sequence of sensory inputs (learning or recognition)
//     - Resetting for a new object
//     - Querying the current recognized object
//     - Accessing individual regions and columns for inspection
//
// Reference: Hawkins et al. (2019), "A Framework for Intelligence..."
// ============================================================================

using CortexSharp.Columns;
using CortexSharp.Core;
using CortexSharp.Regions;

namespace CortexSharp;

/// <summary>
/// The Neocortex — top-level orchestrator of cortical regions.
/// </summary>
public class Neocortex
{
    private readonly NeocortexConfig _config;
    private readonly ICorticalRegion[] _regions;

    public Neocortex(NeocortexConfig config, ICorticalRegion[] regions)
    {
        if (regions.Length == 0)
            throw new ArgumentException("At least one cortical region is required.");
        if (regions.Length != config.RegionConfigs.Length)
            throw new ArgumentException(
                $"Expected {config.RegionConfigs.Length} regions, got {regions.Length}");

        _config = config;
        _regions = regions;
    }

    // =========================================================================
    // Core processing
    // =========================================================================

    /// <summary>
    /// Process one sensory sample through the full hierarchy.
    /// Runs feedforward processing ONCE, then the settling loop uses Settle()
    /// (not Process) to avoid corrupting temporal state.
    /// </summary>
    /// <param name="inputs">
    /// Sensory input for Level 0. One SensoryInput per column, or one
    /// SensoryInput broadcast to all columns.
    /// </param>
    /// <param name="learn">If true, learn at all levels.</param>
    /// <returns>Output from all levels including recognition result.</returns>
    public NeocortexOutput Process(SensoryInput[] inputs, bool learn)
    {
        var regionOutputs = new RegionOutput[_regions.Length];

        // =================================================================
        // Phase 1: Bottom-up feedforward pass (ONCE)
        // =================================================================
        // Level 0 processes sensory input directly.
        // Higher levels process the consensus of the level below.
        // Each level runs its full column computation + voting.

        regionOutputs[0] = _regions[0].Process(inputs, learn);

        for (int level = 1; level < _regions.Length; level++)
        {
            // Higher regions receive the lower region's consensus as input.
            // Wrapped as a single SensoryInput with zero displacement
            // (the concept of "movement" is a Level 0 concern — higher
            // levels receive abstract representations, not sensory data
            // with motor displacement).
            var lowerConsensus = regionOutputs[level - 1].Consensus;

            // TODO: Implement projection if lower consensus SDR size doesn't
            // match higher region's expected input size. This may require a
            // learned mapping or proportional bit remapping.
            var higherInput = new SensoryInput
            {
                FeatureSDR = lowerConsensus,
                DeltaX = 0f,
                DeltaY = 0f,
            };

            regionOutputs[level] = _regions[level].Process(
                new[] { higherInput },
                learn);
        }

        // =================================================================
        // Phase 2: Settling loop (top-down feedback)
        // =================================================================
        // Higher regions feed their consensus back to lower regions.
        // Lower regions SETTLE (re-vote) with this top-down modulation.
        // CRITICAL: Uses Settle(), NOT Process(). Settle() only re-runs
        // L2/3 lateral voting — it does NOT re-run L4 or L6. Re-running
        // Process() would corrupt temporal state and path integration.

        bool allConverged = regionOutputs.All(r => r.Converged);

        for (int iter = 0; iter < _config.MaxSettlingIterations && !allConverged; iter++)
        {
            // Feed consensus down: each level receives feedback from above
            for (int level = _regions.Length - 2; level >= 0; level--)
            {
                _regions[level].ReceiveHierarchicalFeedback(
                    regionOutputs[level + 1].Consensus);
            }

            // Settle bottom-up: re-vote with top-down context (no L4/L6 re-processing)
            regionOutputs[0] = _regions[0].Settle();

            for (int level = 1; level < _regions.Length; level++)
            {
                // Feed the updated lower consensus to the higher region
                // as hierarchical feedback, then settle
                _regions[level].ReceiveHierarchicalFeedback(
                    regionOutputs[level - 1].Consensus);
                regionOutputs[level] = _regions[level].Settle();
            }

            allConverged = regionOutputs.All(r => r.Converged);
        }

        // =================================================================
        // Phase 3: Assemble output
        // =================================================================

        return new NeocortexOutput
        {
            RegionOutputs = regionOutputs,
            Converged = allConverged,
            Confidence = regionOutputs.Last().AgreementScore,
            TopLevelConsensus = regionOutputs.Last().Consensus,
        };
    }

    /// <summary>
    /// Reset all regions for a new object. Clears all representations,
    /// locations, and temporal context across the entire hierarchy.
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < _regions.Length; i++)
            _regions[i].Reset();
    }

    // =========================================================================
    // Accessors
    // =========================================================================

    /// <summary>Number of hierarchical levels (regions).</summary>
    public int RegionCount => _regions.Length;

    /// <summary>Access a specific region by level index.</summary>
    public ICorticalRegion GetRegion(int level) => _regions[level];

    /// <summary>
    /// Convenience: the top-level consensus from the highest region.
    /// After convergence, this IS the recognized object.
    /// </summary>
    public SDR TopLevelConsensus => _regions.Last().Consensus;

    /// <summary>True if all regions have converged.</summary>
    public bool HasConverged => _regions.All(r => r.HasConverged);
}

/// <summary>
/// Output from a full neocortex processing step.
/// </summary>
public record NeocortexOutput
{
    /// <summary>Output from each hierarchical region, bottom to top.</summary>
    public required RegionOutput[] RegionOutputs { get; init; }

    /// <summary>True if all levels converged.</summary>
    public bool Converged { get; init; }

    /// <summary>
    /// Confidence in the recognition result. Based on the top-level
    /// region's agreement score [0, 1].
    /// </summary>
    public float Confidence { get; init; }

    /// <summary>
    /// Consensus SDR from the highest region. After convergence,
    /// this IS the recognized object representation.
    /// </summary>
    public required SDR TopLevelConsensus { get; init; }
}

/// <summary>
/// Configuration for the full neocortex.
/// </summary>
public record NeocortexConfig
{
    /// <summary>Configuration for each hierarchical region (bottom to top).</summary>
    public required CorticalRegionConfig[] RegionConfigs { get; init; }

    /// <summary>
    /// Maximum settling iterations for the hierarchical feedback loop.
    /// Each iteration: feedback flows down, then Settle flows up.
    /// </summary>
    public int MaxSettlingIterations { get; init; } = 5;
}
