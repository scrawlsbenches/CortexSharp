// ============================================================================
// CortexSharp — Sensory Input
// ============================================================================
// Bundles a sensory observation with its motor displacement context.
// This is the unit of input to a cortical column: "I sensed THIS after
// moving THAT MUCH from the previous position."
//
// Why bundle displacement with sensory data?
//   In the Thousand Brains model, every sensory sample is paired with the
//   motor command that preceded it. The displacement drives L6 grid cell
//   path integration (updating the location), and the sensory data drives
//   L4 feature detection. They must arrive together because the column
//   needs both to compute "feature at location."
//
//   At the region/neocortex level, each column may have a DIFFERENT
//   displacement if columns correspond to different sensors (e.g.,
//   different fingers). The SensoryInput record captures this per-column
//   pairing cleanly.
// ============================================================================

namespace CortexSharp.Core;

/// <summary>
/// A single sensory observation: what was sensed + how the sensor moved.
/// One SensoryInput per column per timestep.
/// </summary>
public record SensoryInput
{
    /// <summary>Encoded sensory feature SDR.</summary>
    public required SDR FeatureSDR { get; init; }

    /// <summary>Sensor displacement in X since the previous sample.</summary>
    public float DeltaX { get; init; }

    /// <summary>Sensor displacement in Y since the previous sample.</summary>
    public float DeltaY { get; init; }
}
