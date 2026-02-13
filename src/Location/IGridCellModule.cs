// ============================================================================
// CortexSharp — Grid Cell Module Interface
// ============================================================================
// A Grid Cell Module is a single spatial periodic encoder. It maintains a
// position on a hexagonal lattice using axial coordinates (q, r) and
// activates cells in a Gaussian bump around the current position.
//
// Biological basis:
//   Grid cells in the entorhinal cortex fire in regular hexagonal patterns
//   as an animal moves through space (Hafting et al., 2005). Each module
//   has a characteristic SCALE (spatial period) and ORIENTATION (rotation
//   of the hexagonal grid). Multiple modules at different scales/orientations
//   combine to produce unique location codes.
//
// Hexagonal geometry:
//   Hexagonal tiling is not an aesthetic choice — it provides optimal
//   packing, uniform neighbor distances, and unique multi-scale encoding
//   properties that square grids lack. The axial coordinate system (q, r)
//   maps naturally to hexagonal grids:
//
//     Hex distance: d² = 3 * (dq² + dq*dr + dr²)
//     Cartesian → Axial: q = x / sqrt(3),  r = y - x / sqrt(3) / 2
//
//   The grid wraps toroidally in both dimensions (q and r mod ModuleSize),
//   creating a bounded periodic surface — matching the periodic firing
//   patterns of biological grid cells.
//
// Path integration:
//   When the sensor moves, the movement vector is:
//     1. Rotated by the module's orientation
//     2. Scaled by the module's spatial period
//     3. Converted from Cartesian to axial coordinates
//     4. Added to the current position (with toroidal wrap)
//     5. Corrupted by small noise (biological path integration is noisy)
//
//   This noise accumulates over time, which is why ANCHORING is needed.
//
// Reference: Hafting, Fyhn, Molden, Moser & Moser (2005),
//            "Microstructure of a spatial map in the entorhinal cortex"
//            Hawkins et al. (2019), "A Framework for Intelligence..."
// ============================================================================

using CortexSharp.Core;

namespace CortexSharp.Location;

/// <summary>
/// A single grid cell module — one periodic spatial encoder on a
/// hexagonal lattice. Multiple modules combine for unique locations.
/// </summary>
public interface IGridCellModule
{
    /// <summary>
    /// Integrate a displacement vector (path integration).
    /// Updates the internal position on the hexagonal grid.
    /// </summary>
    /// <param name="deltaX">X displacement in Cartesian coordinates.</param>
    /// <param name="deltaY">Y displacement in Cartesian coordinates.</param>
    void Move(float deltaX, float deltaY);

    /// <summary>
    /// Attempt to anchor to a sensory landmark. If the sensory input
    /// matches a stored anchor (SDR overlap >= threshold), snap position
    /// to the remembered location. Otherwise, learn this position as
    /// a new anchor.
    /// </summary>
    /// <param name="sensoryInput">Current sensory SDR for landmark matching.</param>
    /// <returns>True if an existing anchor was found and position was corrected.</returns>
    bool Anchor(SDR sensoryInput);

    /// <summary>
    /// Current activation pattern — a sparse SDR with a Gaussian bump
    /// of activity centered on the current position.
    /// </summary>
    SDR Activation { get; }

    /// <summary>Grid dimension (ModuleSize x ModuleSize cells).</summary>
    int ModuleSize { get; }

    /// <summary>Total cells in this module (ModuleSize²).</summary>
    int TotalCells { get; }

    /// <summary>Spatial scale of this module's hexagonal grid.</summary>
    float Scale { get; }

    /// <summary>Rotational orientation of this module's grid (radians).</summary>
    float Orientation { get; }

    /// <summary>Reset position to origin (0, 0) for a new object.</summary>
    void Reset();
}
