# Hierarchical Temporal Memory & The Thousand Brains Theory

These are two related but distinct theoretical contributions from Jeff Hawkins and collaborators at Numenta. HTM came first (2004–2016) and models sequence learning in the neocortex. The Thousand Brains Theory (2017–2019) extends it with explicit location signals and a radically different view of cortical organization.

---

## Part 1: The Biological Foundation

Everything in both theories derives from the structure of the neocortex — the 2–4mm thick outer sheet of the mammalian brain. The key biological observations:

### Uniform circuitry

The neocortex has the same six-layer structure everywhere — visual cortex, auditory cortex, motor cortex, language areas. The same circuit is repeated ~150,000 times across the cortical sheet. This implies a **single general algorithm** that works on any sensory modality, not specialized circuits for vision vs. hearing vs. touch.

### Columnar organization

Neurons are organized into vertical **minicolumns** (~80–120 neurons, ~50μm wide) that share feedforward input and tend to co-activate. Groups of 50–100 minicolumns form **macrocolumns** (~300–600μm). Each macrocolumn processes one patch of sensory input.

### The pyramidal neuron

The primary computational unit has three functionally distinct dendritic integration zones:

- **Proximal dendrites** — close to the cell body, receive feedforward input (current sensory data). Strong proximal input causes the cell to fire. This is the "what is happening now" signal.

- **Distal (basal) dendrites** — further from the cell body, receive lateral connections from nearby cells. Distal input alone does **not** cause firing. Instead, it **depolarizes** the cell — raises its membrane potential closer to the firing threshold, putting it in a **predictive state**. If a depolarized cell subsequently receives proximal input, it fires slightly before its non-depolarized neighbors. This is the mechanism of prediction.

- **Apical dendrites** — extend up to Layer 1, receive top-down feedback from higher cortical regions. These provide contextual modulation — what the theory calls "attention" or "expectation."

### Sparse activity

At any moment, roughly **2%** of neurons in a region are active. This extreme sparsity is not a limitation — it is the computational mechanism. With 2048 minicolumns and 40 active (2% sparsity), there are ~10^84 possible patterns. Two random patterns at this sparsity have virtually zero chance of colliding.

### Synaptic learning

Synapses strengthen when pre- and post-synaptic neurons are co-active (Hebbian learning / LTP) and weaken when activity is uncorrelated (LTD). HTM models this with a scalar **permanence** value per synapse (0.0 to 1.0). The synapse is functionally connected only when permanence exceeds a threshold (typically 0.5). Learning adjusts permanence up or down by small increments.

---

## Part 2: Sparse Distributed Representations (SDRs)

SDRs are the data format of the entire theory — the way the cortex represents information. An SDR is a large binary vector (typically 2048 bits) with a small fixed number of ones (typically 40, i.e., ~2% sparsity).

### Key mathematical properties

**Enormous capacity.** C(2048, 40) ≈ 10^84 possible patterns. You will never run out of unique representations.

**Similarity = overlap.** Two SDRs representing similar inputs share many active bits. Two unrelated SDRs share essentially zero bits. Overlap count is the native similarity metric — no learned distance function needed.

**Noise robustness.** A match threshold (theta) can be set well below the active count (w). With w=40 and theta=20, you can corrupt half the bits and still match correctly. The false positive rate at theta=20 with random SDRs is ~10^-43.

**Union property.** The bitwise OR of multiple SDRs produces a new SDR that matches any of its constituents. This is how a single dendritic segment can recognize multiple patterns — it stores synapses from all of them.

**Subsampling.** You don't need all w bits to identify a pattern. A random subset of 15–25 bits is sufficient for astronomically low false positive rates. This is why biological synapses are unreliable and it doesn't matter.

---

## Part 3: HTM — The Core Pipeline

```
Raw data → Encoder → Spatial Pooler → Temporal Memory → Prediction / Anomaly
```

### Encoders

Convert raw values (scalars, dates, categories, coordinates) into SDRs. The fundamental contract: **semantically similar inputs must produce SDRs with high bit overlap**. A scalar encoder maps nearby numbers to overlapping bit ranges. A categorical encoder maps each category to a random non-overlapping block.

### Spatial Pooler (models Layer 4)

The Spatial Pooler takes an input SDR and produces a fixed-sparsity output SDR representing which minicolumns are active. It learns stable column-level representations through competitive Hebbian learning:

1. **Overlap computation** — Each column computes overlap: how many of its proximal synapses connect to currently active input bits.
2. **Inhibition** — Columns compete within a local or global inhibition neighborhood. Only the top ~2% survive (the most active columns).
3. **Learning** — Winning columns strengthen synapses to active input bits (increment permanence) and weaken synapses to inactive input bits (decrement permanence).
4. **Boosting** — Columns that rarely win get their overlap scores boosted, ensuring all columns participate over time. No dead columns are allowed — every column must learn something.

The result: the SP maps a variable input space to a stable, fixed-sparsity representation where similar inputs map to similar column sets.

### Temporal Memory (models L2/3/4 interactions)

This is the core sequence learning algorithm. It learns temporal transitions by forming predictions on distal dendrites.

**The compute cycle:**

1. **Save state.** Copy current active/winner cells to `prevActiveCells` and `prevWinnerCells`.
2. **Build caches.** Compute segment activation counts against the **previous** active cells. This determines which cells are currently predicted (depolarized from the prior timestep).
3. **Activate cells.** For each active column (determined by the SP):
   - If any cell in the column was **predicted** (had a depolarized distal segment), activate only that cell. The column was expected.
   - If **no cell** was predicted, **burst** the entire column — activate all cells. This signals surprise.
4. **Compute anomaly.** Anomaly = fraction of active columns that burst (were not predicted). Fully predicted input → anomaly 0.0. Completely novel → anomaly 1.0.
5. **Learn.**
   - **Reinforce** correct predictions: strengthen synapses on segments that correctly predicted active cells.
   - **Grow** new segments on bursting columns: pick a "winner" cell (least-used cell), create a new distal segment with synapses to `prevWinnerCells`. This is how new sequence transitions are learned.
   - **Punish** incorrect predictions: slightly weaken synapses on segments that predicted cells that did not become active.
6. **Predict.** Compute which cells are now depolarized by evaluating distal segments against the **newly** active cells. These are predictions for the next timestep.

**Critical distinction:** Learning always looks **backward** (to the previous timestep — what caused this activation). Prediction always looks **forward** (from current activation — what comes next). Confusing these two directions is the single most common implementation error.

### Why cells, not just columns

This is perhaps the most important insight in HTM. A minicolumn has ~32 cells (in typical implementations). All cells in a column share the same feedforward receptive field — they respond to the same input feature. But each cell has its own set of distal dendrites, which means each cell represents that feature **in a different temporal context**.

Example: the letter "A" in "BAT" activates a different cell than "A" in "CAR," even though both activate the same column. The specific cell captures the context (what came before). This is how HTM represents the same input differently depending on sequence history — high-order sequence memory, not just first-order transitions.

---

## Part 4: The Thousand Brains Theory

Published in 2019 (Hawkins, Lewis, Klukas, Purdy, Ahmad), this theory extends HTM in a fundamental way. It answers: **how does the cortex learn the structure of objects, not just sequences?**

### The problem with hierarchy

The classical neuroscience model assumed a convergent hierarchy: V1 detects edges, V2 detects corners, V4 detects shapes, IT detects objects. Each level combines features from the level below, producing a single object representation at the top.

Problems with this:
- It requires enormous amounts of labeled training data.
- It can't handle novel viewpoints without data augmentation.
- It doesn't explain how you recognize a coffee cup by touch alone.
- It doesn't explain why damage to early sensory areas doesn't destroy the concept of an object, just the ability to perceive it through that modality.

### The radical proposal: every column learns complete models

The Thousand Brains Theory proposes that each cortical column doesn't just detect a local feature — it learns a **complete model** of every object it encounters. A column processing a coffee cup's handle also knows about the cup's rim, body, and base.

This means the cortex maintains **thousands of parallel models** of every known object, one per column. Recognition is not hierarchical convergence — it is **voting and consensus** among columns.

### Grid cells provide the missing piece: location

Grid cells were discovered in the entorhinal cortex (Nobel Prize 2014, Moser & Moser). They fire in regular hexagonal lattice patterns as an animal moves through space. They perform **path integration** — tracking position by integrating velocity over time, without needing external landmarks.

The Thousand Brains Theory proposes that grid cell-like mechanisms exist **throughout the neocortex**, not just in navigational circuits. Each cortical column has its own grid cell modules that maintain an **object-centric reference frame** — a location within the object being sensed, not in allocentric room coordinates.

When you move your finger from a cup's handle to its rim:
1. Grid cells in that column update the location signal via path integration (integrating the motor command / displacement).
2. The column now associates "handle features at location A" and "rim features at location B."
3. This pair of (feature, location) observations constrains which object is being sensed.

Without location, a column can only say "I see a handle." With location, it can say "I see a handle at *this specific position on the object*," which is far more discriminative.

### The recognition process

1. A column receives a sensory feature (from feedforward input) and a location (from its grid cell module).
2. It activates all stored object representations consistent with that (feature, location) pair.
3. It sends this candidate set **laterally** to other columns via long-range horizontal connections (L2/3).
4. Other columns doing the same thing **vote** — intersecting their candidate sets.
5. With each new sensory sample (saccade, finger movement), candidates are eliminated.
6. **Convergence**: all columns agree on a single object.

This explains why recognition improves with more sensory contact — each observation eliminates candidates. It also explains why you can recognize objects from novel viewpoints (the reference frame handles rotation), and why multiple senses can collaborate (each column votes regardless of modality).

### Displacement cells

Objects have internal structure — the handle is always in a consistent spatial relationship to the rim. **Displacement cells** encode the relative offset (vector) between two locations on an object. During learning, they record the grid cell displacement between consecutive sensory samples. During recognition, they **predict** where the next feature should be based on the known object structure, further constraining the candidate set.

### The Column as a complete sensory-motor unit

In the Thousand Brains framework, a cortical column is not just a feature detector. It is a complete modeling unit with:

| Component | Function | Cortical layer |
|-----------|----------|---------------|
| Input layer | Receives feedforward sensory features | L4 |
| Object layer | Learns and stores object models as (feature, location) pairs | L2/3 |
| Location signal | Grid cell-derived reference frame | L6 (via thalamus) |
| Displacement | Relative structure between features | Connections within L2/3 |
| Motor output | Drives next sensor movement | L5 |
| Lateral voting | Consensus between columns | L2/3 long-range connections |

---

## Part 5: Key Theoretical Constraints for Implementation

These are the invariants that an implementation must respect to be faithful to the theory:

1. **SDR sparsity must be maintained.** ~2% is the target. If sparsity drifts, the mathematical guarantees (capacity, noise tolerance, union properties) break down.

2. **Permanence-based synaptic learning.** Synapses have a continuous permanence value; they are "connected" only above a threshold. Learning adjusts permanence, not binary connectivity.

3. **Proximal vs. distal vs. apical dendrites are functionally distinct.** Proximal drives activation, distal drives prediction/depolarization, apical provides top-down context. Collapsing these loses the theory.

4. **Prediction is depolarization, not activation.** A predicted cell does not fire until it receives feedforward input. Prediction alone only primes the cell.

5. **Bursting signals surprise.** When no cell in a column is predicted, all cells activate. This is the anomaly signal and the trigger for new learning.

6. **Learning looks backward; prediction looks forward.** Segments are grown/reinforced to previous active cells. Predictions are computed against current active cells.

7. **Each column has its own reference frame.** In the Thousand Brains model, location is per-column and object-centric, not global.

8. **Recognition is iterative convergence through voting**, not a single feedforward pass.

9. **Grid cells perform path integration.** Location updates come from integrating displacement/motor commands, not from external coordinates.

10. **Columns learn complete object models**, not partial features that compose hierarchically.
