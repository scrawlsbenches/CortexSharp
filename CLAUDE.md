# CLAUDE.md — CortexSharp

## What This Project Is

CortexSharp is a C# implementation of Hierarchical Temporal Memory (HTM) and the Thousand Brains Theory of intelligence. It models how the neocortex learns, predicts, and recognizes the world.

This is not a neural network. It is a computational model of biological cortical circuits.

## Installing .NET

This project targets **.NET 8** (C# 12). You need the .NET 8 SDK installed.

### Ubuntu 24.04

The .NET 8 SDK is available in Ubuntu 24.04's default `noble-updates/main` repository. No additional package sources are needed.

```bash
# Install the .NET 8 SDK
apt-get install -y dotnet-sdk-8.0

# Verify installation
dotnet --version
```

### Building

```bash
dotnet build
dotnet run    # if an executable entry point is added
```

## The Neocortex

The neocortex is the wrinkled outer layer of the mammalian brain. It is responsible for perception, language, planning, and motor control. Despite handling wildly different modalities, it has a remarkably uniform structure everywhere — the same circuit repeated roughly 150,000 times across the cortical sheet.

### Structure

The neocortex is organized into six layers (L1–L6), stacked vertically:

- **L1** — Mostly axons and dendrites, very few cell bodies. Carries top-down feedback from higher regions. Apical dendrites of pyramidal neurons from deeper layers reach up here.
- **L2/3** — Pyramidal neurons that send output laterally to other cortical columns and to higher cortical regions. This is where object representations form in the Thousand Brains model.
- **L4** — Primary input layer. Receives feedforward sensory input from the thalamus. The Spatial Pooler models this layer's function.
- **L5** — Output to subcortical structures (motor commands, basal ganglia). Large pyramidal neurons.
- **L6** — Feedback to the thalamus. Modulates incoming sensory signals.

These layers are grouped into vertical **columns**:

- **Minicolumns** (~80–120 neurons, ~50 micrometers wide) — the smallest functional unit. Neurons in a minicolumn share feedforward input and tend to fire together.
- **Macrocolumns** (~300–600 micrometers, containing 50–100 minicolumns) — correspond roughly to a "cortical column" in HTM. Each macrocolumn processes one sensory patch.

### Sparsity

At any moment, roughly **2%** of neurons in a cortical region are active. This extreme sparsity is not a limitation — it is the mechanism. Sparse activity means that two random patterns almost never collide, giving the cortex an enormous representational capacity with very low error rates.

With 2048 minicolumns and 40 active at a time (2% sparsity), there are approximately 10^84 possible activation patterns — more than the number of atoms in the observable universe.

### The Pyramidal Neuron

The primary computational unit is the **pyramidal neuron**, which has three functionally distinct dendritic zones:

- **Proximal dendrites** — Close to the cell body. Receive feedforward input (what is happening right now). A strong enough proximal input causes the neuron to fire. This is what the Spatial Pooler models.

- **Distal (basal) dendrites** — Further from the cell body. Receive lateral input from nearby cells. A distal input does not cause firing on its own — it **depolarizes** the cell, putting it in a predictive state. If a depolarized cell then receives proximal input, it fires slightly before its neighbors. This is prediction. This is what Temporal Memory models.

- **Apical dendrites** — Extend up to L1. Receive top-down feedback from higher regions. Provide contextual modulation — "attention" in biological terms.

### How Synapses Learn

Synapses strengthen when presynaptic and postsynaptic neurons are active together (Hebbian learning: "neurons that fire together wire together"). They weaken when activity is uncorrelated.

HTM models this with a **permanence** value per synapse — a scalar between 0.0 and 1.0. The synapse is functionally connected only when permanence exceeds a threshold (typically 0.5). Learning increments permanence on correlated synapses and decrements it on uncorrelated ones. This is a direct abstraction of long-term potentiation (LTP) and long-term depression (LTD).

### Prediction at the Cellular Level

When a distal dendrite receives enough active synaptic input (exceeding a threshold, typically ~13 synapses out of ~20–40 on a segment), it generates a dendritic NMDA spike. This spike doesn't cause the cell to fire, but it **depolarizes** the cell — raising its membrane potential closer to the firing threshold.

A depolarized cell is a **predicting cell**. When the next feedforward input arrives, predicting cells fire faster than non-predicting cells in the same minicolumn. The minicolumn recognizes a predicted input by activating only the predicting cell(s) rather than all cells (bursting).

This is how the cortex predicts: specific cells within a column represent specific temporal contexts. The same sensory feature activates different cells depending on what came before.

## HTM Theory

### Sparse Distributed Representations (SDRs)

An SDR is a binary vector — mostly zeros with a small number of ones — that represents information the way the cortex does. The critical properties:

**High dimensionality + extreme sparsity = reliable computation.** With n=2048 bits and w=40 active, two random SDRs share zero bits with overwhelming probability. The chance of a false match (random overlap exceeding a threshold of 20 bits) is approximately 10^-43. This is not approximate — it is a mathematical property of high-dimensional sparse spaces.

**Similarity = overlap.** Two SDRs representing similar things share many active bits. Two SDRs representing different things share few or no bits. The overlap count is the primary similarity metric. No learned distance function is needed — similarity is structural.

**Union property.** The OR of multiple SDRs produces a new SDR that can be matched against any of its constituents. This lets a single dendritic segment recognize multiple patterns by storing synapses from all of them — which is exactly what biological dendrites do.

**Noise robustness.** An SDR can tolerate significant bit flips and still be recognized, because the match threshold (theta) can be set well below the total active count (w). With w=40 and theta=20, you can corrupt half the bits and still match correctly.

**Subsampling.** You don't need all w bits to recognize a pattern. A random subset of 15–25 bits is sufficient for astronomically low false positive rates. This is why biological synapses are unreliable and it doesn't matter.

### The Core Pipeline

```
Raw data --> Encoder --> Spatial Pooler --> Temporal Memory --> Prediction / Anomaly
```

**Encoders** convert raw data (numbers, dates, categories, GPS coordinates) into SDRs. The fundamental contract: semantically similar inputs must produce SDRs with high bit overlap.

**Spatial Pooler** (models L4) takes an input SDR and produces a fixed-sparsity output SDR representing which minicolumns are active. It learns stable representations through competitive Hebbian learning:
1. Each column computes overlap with its proximal synapses against the input
2. Columns compete via inhibition — only the most active survive (top ~2%)
3. Winning columns strengthen synapses to active input bits, weaken synapses to inactive bits
4. Boosting ensures all columns participate over time (no dead columns)

**Temporal Memory** (models L2/3/4 interactions) learns sequences by forming predictions on distal dendrites:
1. Active columns are determined by the Spatial Pooler
2. Within each active column, if any cell was predicted (depolarized), only that cell fires — the column was **predicted**
3. If no cell was predicted, all cells fire — the column **bursts** (unexpected input)
4. Active cells grow distal synapses to previously active cells, forming sequence memories
5. After learning, cells predict which column will be active next by recognizing the current context on their distal segments

**Anomaly** is simply the fraction of active columns that were not predicted. A fully predicted input has anomaly 0.0. A completely novel input has anomaly 1.0. No separate anomaly detector is needed — it falls out of the prediction mechanism.

### The Temporal Memory State Machine

TM maintains a two-timestep window. The compute cycle:

1. Save current state as previous (`prevActiveCells`, `prevWinnerCells`)
2. Build segment activation caches against **previous** active cells
3. Activate cells: predicted cells in active columns, or burst entire column
4. Compute anomaly from prediction accuracy
5. Learn: reinforce correct predictions, grow segments on bursting columns, punish wrong predictions
6. Compute next predictions against **newly** active cells

**Learning always looks backward (previous timestep). Prediction always looks forward (current timestep).** This distinction is the single most common source of implementation bugs.

## Thousand Brains Theory

Classical neuroscience assumed the cortex builds representations hierarchically — simple features in early regions combine into complex features in later regions, converging to a single representation at the top. The Thousand Brains Theory (Hawkins et al., 2019) proposes something fundamentally different.

### Every Column Learns Complete Models

Each cortical column doesn't just detect a feature — it learns a **complete model** of every object it encounters. A column processing a coffee cup's handle also knows about the cup's rim, body, and base. It builds this model by integrating features with locations over time as the sensor moves across the object.

This means the cortex maintains thousands of simultaneous models of the same object (one per column), not a single hierarchical representation. Recognition is **consensus**: columns that agree on what object they're sensing converge to a shared representation through lateral connections.

### Grid Cells Provide Location

The key missing piece in earlier HTM theory was **location**. How does a column know *where* on an object a feature is?

Grid cells, discovered in the entorhinal cortex (Nobel Prize 2014, Moser & Moser), fire in regular hexagonal patterns as an animal moves through space. They perform **path integration** — tracking position by integrating velocity over time.

The Thousand Brains Theory proposes that grid cell-like mechanisms exist throughout the neocortex, not just in navigation circuits. Each cortical column has its own grid cell modules that maintain an **object-centric reference frame** — a location within the object being sensed, not in the room.

When you move your finger from a cup's handle to its rim, grid cells update the location signal through path integration. The column then associates "handle features at location A" and "rim features at location B" as parts of the same object.

### How Recognition Works

1. A column receives a sensory feature and a location from its grid cells
2. It activates all object representations consistent with that feature-at-location
3. It sends this set of candidates laterally to other columns
4. Other columns doing the same thing vote on which object is consistent with *all* columns' observations
5. Over several sensory samples (saccades, finger movements), the set of candidates narrows
6. **Convergence**: all columns agree on a single object

This explains why recognition improves with more sensory contact — each touch/glance eliminates candidates until only one remains.

### Displacement Cells

Objects have structure — the handle is always in a consistent spatial relationship to the rim. **Displacement cells** encode these relative offsets between features. During learning, they record the grid cell displacement between consecutive sensory locations. During recognition, they predict where the next feature should be, further constraining the candidate set.

### How This Differs From Deep Learning

| Aspect | Deep Learning | Thousand Brains |
|--------|--------------|-----------------|
| Representation | Single hierarchy, one model | Thousands of parallel models |
| Location | Not explicit (learned implicitly) | Explicit grid cell reference frames |
| Learning | Backpropagation, many epochs | Local Hebbian learning, few exposures |
| Recognition | Single forward pass | Iterative convergence through voting |
| Rotation/viewpoint | Requires data augmentation | Handled by reference frame transforms |
| Sparsity | Dense activations | ~2% activity (biologically plausible) |

## References

- Hawkins, J., Lewis, M., Klukas, M., Purdy, S., & Ahmad, S. (2019). "A Framework for Intelligence and Cortical Function Based on Grid Cells in the Neocortex." *Frontiers in Neural Circuits*, 12, 121.
- Hawkins, J. & Ahmad, S. (2016). "Why Neurons Have Thousands of Synapses, a Theory of Sequence Memory in Neocortex." *Frontiers in Neural Circuits*, 10, 23.
- Hawkins, J., Ahmad, S., & Cui, Y. (2017). "A Theory of How Columns in the Neocortex Enable Learning the Structure of the World." *Frontiers in Neural Circuits*, 11, 81.
- Ahmad, S. & Hawkins, J. (2016). "How Do Neurons Operate on Sparse Distributed Representations? A Mathematical Theory of Sparsity, Neurons and Active Dendrites." *arXiv:1601.00720*.
- Cui, Y., Ahmad, S., & Hawkins, J. (2017). "The HTM Spatial Pooler — A Neocortical Algorithm for Online Sparse Distributed Coding." *Frontiers in Computational Neuroscience*, 11, 111.
- BAMI: Biological and Machine Intelligence. https://numenta.com/resources/biological-and-machine-intelligence/
