// ============================================================================
// CortexSharp — Core Types
// ============================================================================
// The fundamental building blocks of cortical computation. These types model
// the lowest-level biological structures: synapses, dendritic segments, and
// cell states. Everything above — cells, minicolumns, layers, columns,
// regions — is composed from these primitives.
//
// Biological basis:
//   Synapse          → A connection between two neurons, gated by permanence
//   DendriteSegment  → A branch of dendrite carrying multiple synapses;
//                      acts as a pattern detector (coincidence detector)
//   DendriteType     → The four functionally distinct dendritic pathways of
//                      a pyramidal neuron: proximal, distal, basal, apical
//   CellState        → The activation state of a pyramidal neuron
//
// Reference: Hawkins & Ahmad (2016), "Why Neurons Have Thousands of Synapses"
// ============================================================================

namespace CortexSharp.Core;

// ============================================================================
// Synapse — the fundamental connection
// ============================================================================
// A synapse connects a presynaptic cell to a postsynaptic dendrite segment.
// The permanence value is a scalar [0.0, 1.0] that models synaptic strength.
// The synapse is functionally "connected" only when permanence >= threshold
// (typically 0.5). Learning adjusts permanence via small increments/decrements,
// modeling long-term potentiation (LTP) and long-term depression (LTD).
//
// Key property: permanence is continuous but connectivity is binary.
// This means a synapse can be "almost connected" (permanence 0.49) and
// a single learning event can cross the threshold. This enables rapid
// learning from few exposures — a hallmark of biological cortical learning.
//
// MUTABLE: Synapse permanence must change during learning. This struct is
// stored in collections on DendriteSegment and mutated in-place by the
// learning algorithms. Using a readonly record struct here would force
// copy-on-write semantics that complicate every learning path.
// ============================================================================

/// <summary>
/// A synapse — the fundamental connection between two neurons.
/// Mutable because learning adjusts permanence in-place.
/// </summary>
public struct Synapse
{
    /// <summary>
    /// Index of the presynaptic cell (the source of the signal).
    /// </summary>
    public int PresynapticIndex;

    /// <summary>
    /// Synaptic strength [0.0, 1.0]. The synapse is functionally connected
    /// when permanence >= the connected threshold (typically 0.5).
    /// Hebbian learning increments permanence when pre- and post-synaptic
    /// cells are co-active, decrements when uncorrelated.
    /// </summary>
    public float Permanence;

    /// <summary>
    /// Iteration at which this synapse was created. Used for age-based
    /// lifecycle management (e.g., protecting young synapses from pruning).
    /// </summary>
    public int CreatedAtIteration;

    public Synapse(int presynapticIndex, float permanence, int createdAtIteration = 0)
    {
        PresynapticIndex = presynapticIndex;
        Permanence = permanence;
        CreatedAtIteration = createdAtIteration;
    }
}

// ============================================================================
// DendriteSegment — a branch of dendrite acting as a pattern detector
// ============================================================================
// Each dendrite segment carries ~20-40 synapses and acts as a coincidence
// detector: it activates when enough of its synapses (above a threshold,
// typically ~13) are simultaneously active. This is an NMDA spike in biology.
//
// A single neuron has many dendrite segments (potentially thousands across
// all dendritic zones). Each segment independently detects a different
// pattern. This means a single neuron can recognize MANY different contexts
// — it's not limited to a single learned pattern.
//
// Segments are the primary unit of learning in HTM. Learning creates new
// segments, grows synapses on existing segments, and strengthens/weakens
// synapses based on Hebbian correlation.
//
// Reference: Hawkins & Ahmad (2016), "Why Neurons Have Thousands of Synapses"
// ============================================================================

/// <summary>
/// A dendrite segment — a branch of dendrite carrying synapses that acts
/// as a coincidence detector (pattern recognizer).
/// </summary>
public class DendriteSegment
{
    /// <summary>Which dendritic zone this segment belongs to.</summary>
    public DendriteType Type { get; }

    /// <summary>Index of the cell that owns this segment.</summary>
    public int CellIndex { get; }

    /// <summary>The synapses on this segment (mutable for learning).</summary>
    public List<Synapse> Synapses { get; } = new();

    /// <summary>Last iteration this segment was active (for age-based management).</summary>
    public int LastActiveIteration { get; set; }

    /// <summary>Number of times this segment has been active (for statistics).</summary>
    public int ActivationCount { get; set; }

    public DendriteSegment(DendriteType type, int cellIndex)
    {
        Type = type;
        CellIndex = cellIndex;
    }

    /// <summary>
    /// Count active connected synapses: synapses whose presynaptic cell
    /// is in the active set AND whose permanence >= threshold.
    /// This is the primary activation computation for a segment.
    /// </summary>
    /// <param name="activeCells">Set of currently active presynaptic cells.</param>
    /// <param name="connectedThreshold">Permanence threshold for connectivity.</param>
    /// <returns>Number of active connected synapses.</returns>
    public int ComputeActivity(SDR activeCells, float connectedThreshold)
    {
        // TODO: Implement — count synapses where:
        //   Permanence >= connectedThreshold AND PresynapticIndex is in activeCells
        throw new NotImplementedException();
    }

    /// <summary>
    /// Count active potential synapses: synapses whose presynaptic cell
    /// is in the active set regardless of permanence. Used when searching
    /// for the "best matching" segment (the one with the most potential
    /// for learning to recognize the current pattern).
    /// </summary>
    /// <param name="activeCells">Set of currently active presynaptic cells.</param>
    /// <returns>Number of active potential synapses.</returns>
    public int ComputePotentialActivity(SDR activeCells)
    {
        // TODO: Implement — count synapses where:
        //   PresynapticIndex is in activeCells (ignore permanence)
        throw new NotImplementedException();
    }

    /// <summary>
    /// Adapt synapse permanences based on Hebbian learning:
    ///   - Increment permanence for synapses to active presynaptic cells (LTP)
    ///   - Decrement permanence for synapses to inactive presynaptic cells (LTD)
    ///   - Clamp to [0.0, 1.0]
    /// </summary>
    /// <param name="activeCells">Set of currently active presynaptic cells.</param>
    /// <param name="increment">Amount to increase correlated synapses.</param>
    /// <param name="decrement">Amount to decrease uncorrelated synapses.</param>
    public void AdaptSynapses(SDR activeCells, float increment, float decrement)
    {
        // TODO: Implement — for each synapse:
        //   if PresynapticIndex in activeCells: permanence += increment
        //   else: permanence -= decrement
        //   clamp to [0.0, 1.0]
        throw new NotImplementedException();
    }

    /// <summary>
    /// Grow new synapses to a subset of active cells that don't already
    /// have a synapse on this segment. Used when learning new patterns.
    /// </summary>
    /// <param name="candidates">Potential presynaptic cells to connect to.</param>
    /// <param name="initialPermanence">Starting permanence for new synapses.</param>
    /// <param name="maxNewSynapses">Maximum synapses to add.</param>
    /// <param name="iteration">Current learning iteration (for age tracking).</param>
    public void GrowSynapses(
        SDR candidates,
        float initialPermanence,
        int maxNewSynapses,
        int iteration)
    {
        // TODO: Implement — sample up to maxNewSynapses from candidates
        //   that don't already have a synapse on this segment.
        //   Initialize with the given permanence and iteration.
        throw new NotImplementedException();
    }
}

// ============================================================================
// DendriteType — the four dendritic pathways of a pyramidal neuron
// ============================================================================
// Each pyramidal neuron has four functionally distinct dendritic pathways:
//
//   Proximal  — Close to the cell body. Receives feedforward input.
//               Strong proximal input causes the cell to fire.
//               This is what the Spatial Pooler models.
//
//   Distal    — Further from the cell body. Receives lateral/contextual
//               input from other cells in the same or nearby regions.
//               Does NOT cause firing on its own — instead, it
//               DEPOLARIZES the cell (predictive state). A depolarized
//               cell fires faster when it next receives proximal input.
//               This is what Temporal Memory models for sequence context.
//
//   Basal     — In the Thousand Brains model, L4 cells receive an
//               additional input: the location signal from L6 grid cells.
//               This arrives on a separate basal pathway, distinct from
//               the distal context signal. It allows L4 cells to encode
//               "feature at location" rather than just "feature."
//
//   Apical    — Extends to Layer 1. Receives top-down feedback from
//               higher cortical regions. Provides contextual modulation
//               ("attention" / "expectation"). Like distal, apical input
//               is modulatory — it biases, but does not drive, activation.
//
// The distinction between these pathways is NOT optional — collapsing them
// loses the theory. Proximal drives. Distal predicts (sequence). Basal
// locates (space). Apical attends (hierarchy).
// ============================================================================

public enum DendriteType
{
    /// <summary>
    /// Feedforward input. Strong activation causes the cell to fire.
    /// Modeled by the Spatial Pooler's proximal connections.
    /// </summary>
    Proximal,

    /// <summary>
    /// Lateral/contextual input from other cells in the same region.
    /// Depolarizes (predicts) the cell without causing it to fire.
    /// Modeled by Temporal Memory's distal segments. Encodes sequence context.
    /// </summary>
    Distal,

    /// <summary>
    /// Location signal from grid cells (L6). Used in the Thousand Brains
    /// model to provide object-centric spatial context to L4 cells.
    /// Distinct from Distal: basal carries WHERE, distal carries WHEN.
    /// </summary>
    Basal,

    /// <summary>
    /// Top-down feedback from higher cortical regions via Layer 1.
    /// Modulatory — biases cell selection without driving activation.
    /// </summary>
    Apical,
}

// ============================================================================
// CellState — the activation states of a pyramidal neuron
// ============================================================================
// A cell's state determines how it participates in computation:
//
//   Inactive     → Not participating. No proximal or distal activation.
//   Predictive   → Depolarized by distal/apical input. Will fire faster
//                  if feedforward input arrives. This IS prediction.
//   Active       → Firing. Either predicted + feedforward confirmed, or
//                  bursting (unpredicted feedforward input).
//   Bursting     → Active because no cell in the minicolumn was predicted.
//                  All cells in the column fire. This signals SURPRISE
//                  and triggers new learning.
//
// The state machine per timestep:
//   1. Previous Predictive + Current Feedforward → Active (predicted)
//   2. No Predictive + Current Feedforward → Bursting (surprise)
//   3. Distal/Apical activation alone → Predictive (for next timestep)
//   4. No activation → Inactive
// ============================================================================

[Flags]
public enum CellState
{
    Inactive    = 0,
    Active      = 1 << 0,
    Predictive  = 1 << 1,
    Bursting    = 1 << 2,
    Winner      = 1 << 3,  // Selected as the learning representative for its column
}
