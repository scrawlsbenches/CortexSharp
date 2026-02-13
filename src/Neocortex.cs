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
//   1. Level 0 processes sensory input → produces consensus
//   2. Level 1 processes Level 0 consensus → produces consensus
//   3. Level 1 consensus fed back to Level 0 as apical input
//   4. Level 0 recomputes with top-down modulation
//   5. Check convergence at all levels
//   6. Repeat until settled or max iterations
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

    // TODO: Add convenience constructor that builds regions from config.
    // The current constructor takes pre-built regions for testability.
    // A factory method or builder pattern would be appropriate here.

    // =========================================================================
    // Core processing
    // =========================================================================

    /// <summary>
    /// Process one sensory sample through the full hierarchy.
    /// Runs the settling loop: feedforward up, feedback down, repeat.
    /// </summary>
    /// <param name="sensoryPatches">
    /// Sensory input for Level 0. One SDR per column, or one SDR
    /// broadcast to all columns.
    /// </param>
    /// <param name="deltaX">Sensor displacement in X.</param>
    /// <param name="deltaY">Sensor displacement in Y.</param>
    /// <param name="learn">If true, learn at all levels.</param>
    /// <returns>Output from all levels including recognition result.</returns>
    public NeocortexOutput Process(
        SDR[] sensoryPatches,
        float deltaX,
        float deltaY,
        bool learn)
    {
        var regionOutputs = new RegionOutput[_regions.Length];

        // =================================================================
        // Phase 1: Bottom-up feedforward pass
        // =================================================================
        // Level 0 processes sensory input directly.
        // Higher levels process the consensus of the level below.

        regionOutputs[0] = _regions[0].Process(sensoryPatches, deltaX, deltaY, learn);

        for (int level = 1; level < _regions.Length; level++)
        {
            // Higher regions receive the lower region's consensus as input.
            // The consensus is wrapped as a single-element array (broadcast
            // to all columns in the higher region).
            var lowerConsensus = regionOutputs[level - 1].Consensus;

            // TODO: Implement projection if lower consensus SDR size doesn't
            // match higher region's expected input size. This may require a
            // learned mapping or proportional bit remapping.
            regionOutputs[level] = _regions[level].Process(
                new[] { lowerConsensus },
                deltaX, deltaY,
                learn);
        }

        // =================================================================
        // Phase 2: Settling loop (top-down feedback)
        // =================================================================
        // Higher regions feed their consensus back to lower regions.
        // Lower regions recompute with this top-down modulation.
        // Repeat until all levels converge or max iterations reached.

        bool allConverged = regionOutputs.All(r => r.Converged);

        for (int iter = 0; iter < _config.MaxSettlingIterations && !allConverged; iter++)
        {
            // Feed consensus down: each level receives feedback from above
            for (int level = _regions.Length - 2; level >= 0; level--)
            {
                _regions[level].ReceiveHierarchicalFeedback(
                    regionOutputs[level + 1].Consensus);
            }

            // Recompute bottom-up with top-down context
            regionOutputs[0] = _regions[0].Process(sensoryPatches, deltaX, deltaY, learn);

            for (int level = 1; level < _regions.Length; level++)
            {
                var lowerConsensus = regionOutputs[level - 1].Consensus;
                regionOutputs[level] = _regions[level].Process(
                    new[] { lowerConsensus },
                    deltaX, deltaY,
                    learn);
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
    /// Each iteration: feedback flows down, then recompute flows up.
    /// </summary>
    public int MaxSettlingIterations { get; init; } = 5;
}
