// ============================================================================
// CortexSharp — Location Layer (Layer 6)
// ============================================================================
// Layer 6 provides the location signal for a cortical column. In the
// Thousand Brains model, this is where grid cell-like mechanisms maintain
// an OBJECT-CENTRIC reference frame — tracking position relative to the
// object being sensed, not position in the room.
//
// Biological basis:
//   Grid cells were discovered in the entorhinal cortex (Hafting et al.,
//   2005; Nobel Prize 2014). They fire in regular hexagonal lattice
//   patterns as an animal moves through space. They perform PATH
//   INTEGRATION — tracking position by integrating velocity/displacement
//   over time, without needing external landmarks.
//
//   The Thousand Brains Theory proposes that grid cell-like mechanisms
//   exist throughout the neocortex, not just in navigational circuits.
//   Each cortical column has its own set of grid cell modules.
//
// Multi-module architecture:
//   A single grid module provides a periodic spatial code that repeats.
//   Multiple modules at DIFFERENT SCALES and ORIENTATIONS combine to
//   produce a unique location code — analogous to Fourier components.
//   The combination of 3-5 modules with different periodicities gives
//   enormously higher resolution than any single module.
//
//   Example: Module 1 (scale 1.0), Module 2 (scale 1.7), Module 3 (scale 2.4)
//   Each fires in its own hexagonal pattern. The conjunction of all three
//   produces a location SDR that is unique within the object's extent.
//
// Anchoring:
//   Path integration accumulates error over time. Anchoring corrects this
//   drift by associating sensory patterns with known positions. When a
//   familiar sensory input is encountered, the grid cells snap to the
//   remembered position for that landmark. This uses SDR overlap matching
//   (not hash lookup) to be noise-tolerant.
//
// Object-centric, not allocentric:
//   When a new object is encountered, the location system RESETS. The
//   first touch becomes the origin (0,0) of the object's reference frame.
//   All subsequent locations are relative to this origin. This is what
//   makes recognition viewpoint-invariant — the handle is always at the
//   same object-relative location regardless of how you hold the cup.
//
// Reference: Hawkins et al. (2019), "A Framework for Intelligence and
//            Cortical Function Based on Grid Cells in the Neocortex"
// ============================================================================

using CortexSharp.Core;

namespace CortexSharp.Layers;

/// <summary>
/// Layer 6 — location tracking via grid cell modules.
/// Maintains an object-centric reference frame through path integration.
/// </summary>
public interface ILocationLayer
{
    // =========================================================================
    // Path integration — update location from motor commands
    // =========================================================================
    // When the sensor moves (finger slides, eyes saccade), the motor
    // command is translated into a displacement vector. Each grid cell
    // module integrates this displacement independently at its own
    // scale and orientation:
    //
    //   1. Rotate displacement by module's orientation
    //   2. Scale by module's spatial period
    //   3. Convert to axial hex coordinates
    //   4. Update position with toroidal wrapping
    //   5. Add path integration noise (biological realism)
    //
    // The result: each module's activation bump moves to a new position
    // on its hexagonal grid, and the combined output SDR changes to
    // represent the new location.
    // =========================================================================

    /// <summary>
    /// Update the location by integrating a displacement vector.
    /// Each grid module integrates independently at its own scale.
    /// </summary>
    /// <param name="deltaX">Displacement in X (sensor movement).</param>
    /// <param name="deltaY">Displacement in Y (sensor movement).</param>
    void Move(float deltaX, float deltaY);

    // =========================================================================
    // Anchoring — correct path integration drift
    // =========================================================================
    // Path integration accumulates noise. Anchoring corrects this by
    // recognizing a sensory pattern and snapping to the remembered
    // position. Uses SDR overlap matching (noise-tolerant):
    //
    //   1. Compare current sensory input against stored anchors
    //   2. If overlap exceeds threshold → snap to remembered position
    //   3. If no match → store current (sensory, position) as new anchor
    //
    // This mirrors biological landmark-based correction of grid cell
    // drift via visual/sensory cues.
    // =========================================================================

    /// <summary>
    /// Attempt to anchor the current position to a sensory landmark.
    /// If the sensory input matches a known anchor (via SDR overlap),
    /// the grid cells snap to the remembered position. If no match,
    /// the current position is learned as a new anchor.
    /// </summary>
    /// <param name="sensoryInput">Current sensory SDR for landmark matching.</param>
    void Anchor(SDR sensoryInput);

    // =========================================================================
    // State
    // =========================================================================

    /// <summary>
    /// Combined location SDR from all grid cell modules. This is the
    /// concatenation of each module's activation pattern, producing a
    /// unique location code for the current position in object-centric
    /// coordinates. Fed to L4 as basal input.
    /// </summary>
    SDR LocationSDR { get; }

    /// <summary>
    /// Number of grid cell modules in this location system.
    /// More modules = higher resolution = more unique locations.
    /// </summary>
    int ModuleCount { get; }

    /// <summary>
    /// Reset the location system for a new object. Sets position to
    /// origin (0,0) in all modules. The first touch on the new object
    /// becomes the reference point for all subsequent locations.
    /// </summary>
    void Reset();
}
