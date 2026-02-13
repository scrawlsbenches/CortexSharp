// ============================================================================
// CortexSharp — Displacement Module Interface
// ============================================================================
// Displacement cells encode the spatial relationships BETWEEN features on
// an object. They learn and predict the relative vector (displacement)
// between two locations, enabling structure-based prediction.
//
// Biological basis:
//   Objects have internal structure — the handle of a coffee cup is always
//   in a consistent spatial relationship to the rim. Displacement cells
//   record these relationships during learning and use them for prediction
//   during recognition.
//
// Key mechanism:
//   During LEARNING:
//     At each pair of consecutive touches (locationA → locationB), compute
//     the displacement vector: displacement = locationB - locationA
//     Store the triple: (sourceLocation, displacement, targetLocation)
//
//   During RECOGNITION:
//     Given the current location and the known object structure, predict
//     WHERE the next feature should be. This constrains the candidate set
//     beyond what feature-at-location matching alone provides.
//
// Critical property: STRUCTURE-BASED, not SEQUENCE-BASED.
//   Predictions must be conditioned on the current (feature, location)
//   pair — NOT on the exploration order. If you learned a cup by touching
//   handle → rim → base, you should still predict correctly when touching
//   base → handle → rim. The spatial structure is the same regardless of
//   exploration path.
//
//   This means displacement memory is stored as:
//     (currentLocation) → [(displacement, expectedTargetLocation), ...]
//   NOT as an ordered sequence of displacements.
//
// Geometry:
//   Should use hexagonal coordinates (axial q, r) consistent with the
//   grid cell modules. Wraps toroidally at grid boundaries.
//
// Reference: Hawkins et al. (2019), "A Framework for Intelligence..."
// ============================================================================

using CortexSharp.Core;

namespace CortexSharp.Location;

/// <summary>
/// Displacement module — learns and predicts spatial structure within objects.
/// </summary>
public interface IDisplacementModule
{
    /// <summary>
    /// Learn a displacement between two consecutive sensory locations.
    /// Records the (source, displacement, target) triple.
    /// </summary>
    /// <param name="sourceLocation">Location SDR where the displacement starts.</param>
    /// <param name="targetLocation">Location SDR where the displacement ends.</param>
    void Learn(SDR sourceLocation, SDR targetLocation);

    /// <summary>
    /// Predict possible target locations given the current location and
    /// learned object structure. Returns all target locations reachable
    /// from the current location via learned displacements.
    /// </summary>
    /// <param name="currentLocation">Current location SDR.</param>
    /// <returns>
    /// Predicted target locations with their associated displacements.
    /// Multiple predictions are possible (one for each learned connection
    /// from the current location).
    /// </returns>
    DisplacementPrediction[] PredictTargets(SDR currentLocation);

    /// <summary>
    /// Clear all learned displacements (new object).
    /// </summary>
    void Reset();
}

/// <summary>
/// A single displacement prediction — a possible next location.
/// </summary>
public record DisplacementPrediction
{
    /// <summary>Predicted target location SDR.</summary>
    public required SDR TargetLocation { get; init; }

    /// <summary>The displacement vector (as SDR) from current to target.</summary>
    public required SDR Displacement { get; init; }

    /// <summary>
    /// Confidence based on SDR overlap between the query location and
    /// the stored source location. Higher = better match.
    /// </summary>
    public float Confidence { get; init; }
}
