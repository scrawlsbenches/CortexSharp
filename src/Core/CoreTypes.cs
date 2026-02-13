// ============================================================================
// CortexSharp — Core Types
// ============================================================================
// The fundamental building blocks of cortical computation. These types model
// the lowest-level biological structures: synapses, dendritic segments, and
// cell states. Everything above — cells, minicolumns, layers, columns,
// regions — is composed from these primitives.
//
// Biological basis:
//   Synapse        → A connection between two neurons, gated by permanence
//   DendriteSegment → A branch of dendrite carrying multiple synapses;
//                     acts as a pattern detector (coincidence detector)
//   DendriteType   → The three functionally distinct dendritic zones of
//                     a pyramidal neuron: proximal, distal/basal, apical
//   CellState      → The activation state of a pyramidal neuron
//
// Reference: Hawkins & Ahmad (2016), "Why Neurons Have Thousands of Synapses"
// ============================================================================

using System;
using System.Collections.Generic;

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
// ============================================================================

public readonly record struct Synapse
{
    /// <summary>
    /// Index of the presynaptic cell (the source of the signal).
    /// </summary>
    public required int PresynapticIndex { get; init; }

    /// <summary>
    /// Synaptic strength [0.0, 1.0]. The synapse is functionally connected
    /// when permanence >= the connected threshold (typically 0.5).
    /// Hebbian learning increments permanence when pre- and post-synaptic
    /// cells are co-active, decrements when uncorrelated.
    /// </summary>
    public required float Permanence { get; init; }

    /// <summary>
    /// Iteration at which this synapse was created. Used for age-based
    /// lifecycle management (e.g., protecting young synapses from pruning).
    /// </summary>
    public int CreatedAtIteration { get; init; }
}

// ============================================================================
// DendriteType — the three functional zones of a pyramidal neuron
// ============================================================================
// Each pyramidal neuron has three dendritic zones with distinct functions:
//
//   Proximal  — Close to the cell body. Receives feedforward input.
//               Strong proximal input causes the cell to fire.
//               This is what the Spatial Pooler models.
//
//   Distal    — Also called "basal." Further from the cell body.
//               Receives lateral/contextual input from other cells.
//               Does NOT cause firing on its own — instead, it
//               DEPOLARIZES the cell (predictive state). A depolarized
//               cell fires faster when it next receives proximal input.
//               This is what Temporal Memory models.
//
//   Apical    — Extends to Layer 1. Receives top-down feedback from
//               higher cortical regions. Provides contextual modulation
//               ("attention" / "expectation"). Like distal, apical input
//               is modulatory — it biases, but does not drive, activation.
//
//   Basal     — In the Thousand Brains model, L4 cells receive an
//               additional input: the location signal from L6 grid cells.
//               This arrives on a separate basal pathway, distinct from
//               the distal context signal. It allows L4 cells to encode
//               "feature at location" rather than just "feature."
//
// The distinction between these zones is NOT optional — collapsing them
// loses the theory. Proximal drives. Distal predicts. Apical attends.
// Basal locates.
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
    /// Modeled by Temporal Memory's distal segments.
    /// </summary>
    Distal,

    /// <summary>
    /// Top-down feedback from higher cortical regions via Layer 1.
    /// Modulatory — biases cell selection without driving activation.
    /// </summary>
    Apical,

    /// <summary>
    /// Location signal from grid cells (L6). Used in the Thousand Brains
    /// model to provide object-centric spatial context to L4 cells.
    /// Distinct from Distal: basal carries WHERE, distal carries WHEN.
    /// </summary>
    Basal,
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
