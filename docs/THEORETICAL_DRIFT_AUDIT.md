# CortexSharp — Theoretical Drift Audit

This document identifies where the implementation has drifted from HTM theory and the Thousand Brains Theory as described in the published papers (Hawkins et al., 2016–2019) and the project's CLAUDE.md specification.

Findings are grouped by severity:
- **Critical** — Contradicts a core theoretical invariant; will produce wrong behavior
- **Major** — Missing or incorrect implementation of a key mechanism
- **Minor** — Simplification that may limit fidelity but doesn't break correctness

---

## Critical Drift

### 1. Grid Cells Labeled "Allocentric" — Theory Requires Object-Centric

**Location:** `HtmEnhanced.cs:2417`

**Implementation says:**
> "Grid cells provide allocentric (world-centered) location reference frames."

**Theory says:**
> "Each cortical column has its own grid cell modules that maintain an **object-centric reference frame** — a location within the object being sensed, not in allocentric room coordinates." (CLAUDE.md)

This is a direct semantic contradiction. The Thousand Brains Theory's central insight is that cortical columns use **object-centric** reference frames — positions relative to the object, not the world. Allocentric (world-centered) grids exist in the entorhinal cortex for navigation, but the theory proposes that neocortical grid cells maintain object-relative coordinates. This distinction is what allows recognition to be viewpoint-invariant.

The actual code does reset grid positions to (0,0) in `StartNewObject()` (line 3235), which effectively makes them object-centric in practice. But the comment and conceptual framing are wrong, and any future developer will build on the wrong mental model.

**Fix:** Correct the section comment to "object-centric." Ensure all documentation and naming reflects that these are object-relative locations, not world locations.

---

### 2. Object Evidence Accumulation Grows Instead of Narrowing

**Location:** `HtmEnhanced.cs:2881–2901` (`AccumulateObjectEvidence`)

**Implementation does:**
```
intersection-boosted union: keep bits from EITHER previous OR current evidence
```

**Theory says:**
> "Over several sensory samples (saccades, finger movements), the set of candidates narrows." Each observation should **eliminate** inconsistent candidates, not add more.

The theory's recognition process is progressive narrowing: start with all objects consistent with the first observation, then intersect with objects consistent with the second, etc. The current implementation does the opposite — it takes the union of previous and current representations, preferring the intersection but filling the rest from the union. This means the representation grows or stays constant rather than converging to a single object.

**Fix:** Recognition should use intersection (or at minimum, weighted intersection where bits must appear in both previous and current to survive). The union fill-up defeats the candidate elimination mechanism.

---

### 3. Cortical Column Collapses Multi-Layer Structure Into a Single SP

**Location:** `HtmEnhanced.cs:2769–2776`

**Implementation does:**
```csharp
// Step 1: Combine sensory feature and location into a single input
var combinedInput = CombineFeatureAndLocation(sensoryInput, locationInput);
// Step 2: Spatial Pooler — create sparse representation of feature-at-location
var activeColumns = _featureSP.Compute(combinedInput, learn);
```

**Theory says:**
The cortex has distinct layers with distinct functions:
- **L4** receives feedforward sensory input (what the SP models)
- **L6** provides the location signal (from grid cells)
- **L2/3** combines feature + location for object learning

By concatenating the feature and location SDRs and feeding the result through a single SP, the implementation collapses these three layers into one. The SP was designed to process sensory input alone and produce stable column activations. Feeding it a combined (feature + location) vector means the SP must learn to jointly represent both — a fundamentally different computation than what the theory describes.

The location signal should arrive at a separate layer (or through a separate dendritic pathway) and be integrated downstream, not mixed into the feedforward input.

**Fix:** Implement a two-layer architecture within the column: SP processes sensory input alone (modeling L4), and a separate object layer (modeling L2/3) integrates SP output with the location signal to form feature-at-location representations.

---

### 4. Lateral Voting Uses Blending Instead of Intersection

**Location:** `HtmEnhanced.cs:2807–2828` (`ReceiveLateralInput`) and `2904–2920` (`BlendSDRs`)

**Implementation does:**
```csharp
_currentObjectRepresentation = BlendSDRs(
    _currentObjectRepresentation, consensusRepresentation,
    _config.LateralInfluence);  // Take fraction from each
```

**Theory says:**
> "Other columns doing the same thing vote — **intersecting their candidate sets**."

Blending (taking N% of bits from A and M% from B by lowest index) is fundamentally different from intersection. Intersection preserves only bits agreed upon by both parties — this is how candidate sets narrow. Blending creates a mix that may contain bits from neither's original candidate set in a meaningful way.

Additionally, the `BlendSDRs` method selects bits by lowest index, not by semantic relevance. This introduces a systematic bias toward low-index bits.

**Fix:** Replace blending with intersection-based consensus. When a column receives lateral input, it should keep only the bits in its representation that are also present in (or supported by) the consensus.

---

## Major Drift

### 5. Apical Dendrites Completely Missing

**Location:** Absent from the entire codebase.

**Theory says:**
> "**Apical dendrites** — Extend up to L1. Receive top-down feedback from higher regions. Provide contextual modulation — 'attention' in biological terms."

The three dendritic zones (proximal, distal, apical) are functionally distinct and all three are described as essential in the theory. The implementation has proximal dendrites (SP) and distal dendrites (TM) but no apical dendrites at all.

Without apical dendrites, there is no mechanism for:
- Top-down attention or expectation
- Feedback from higher cortical regions
- Contextual modulation of predictions

**Impact:** Reduces the model to a purely feedforward + lateral system. Cannot implement hierarchical feedback, attention, or expectation-driven processing.

---

### 6. Grid Cells Use Square Grid Instead of Hexagonal Lattice

**Location:** `HtmEnhanced.cs:2429–2557`

**Implementation does:**
Square grid: `ModuleSize x ModuleSize` cells on a 2D plane with Gaussian bump activation.

**Theory says:**
> "Grid cells fire in regular **hexagonal** lattice patterns."

The hexagonal structure is not incidental — it provides optimal packing and unique multi-scale encoding properties. A square grid has different spatial autocorrelation properties than a hexagonal one, which affects path integration accuracy and location uniqueness at the same resolution.

**Impact:** Location encodings may have different resolution and uniqueness properties than the theory predicts. For a given number of cells, hexagonal tiling provides higher spatial resolution.

---

### 7. One Grid Cell Module Per Column Instead of Multiple

**Location:** `HtmEnhanced.cs:3103`

**Implementation:** One `GridCellModule` per column.

**Theory says:**
Multiple grid cell modules at different scales and orientations per column provide a unique location code through their combination — analogous to Fourier components. The combination of multiple modules with different periodicities produces a unique location representation with much higher resolution than any single module.

**Impact:** Single-module columns have much lower location resolution and higher ambiguity. The combinatorial power of multiple modules is a key theoretical property.

---

### 8. Object Memory Uses Hash-Table Lookup Instead of SDR-Based Associative Memory

**Location:** `HtmEnhanced.cs:2726`

**Implementation:**
```csharp
private readonly Dictionary<int, HashSet<int>> _objectMemory = new();
```

**Theory says:**
Object models should be learned through synaptic connections and SDR patterns — the same Hebbian permanence-based learning that drives the rest of the system.

Using `Dictionary<int, HashSet<int>>` with hash-code keys is a non-biological shortcut that:
- Has no capacity limits (biological neurons have finite synapses)
- Has no graceful degradation under noise
- Cannot exploit the partial-match / noise-tolerance properties of SDRs
- Bypasses the permanence-based learning mechanism entirely

**Impact:** The object layer does not demonstrate the SDR properties that are central to the theory's claims about capacity and robustness.

---

### 9. Displacement Cell Prediction: Sequence Replay Instead of Structure-Based

**Location:** `HtmEnhanced.cs:3185`

**Implementation:**
```csharp
int nextIdx = (_explorationSteps - 1) % learnedDisps.Count;
predictedNextLocation = _displacementModule.PredictTarget(
    currentLoc, learnedDisps[nextIdx]);
```

**Theory says:**
Displacement cells predict the next location based on the object's **spatial structure** — given the current (feature, location) and the known spatial relationships between features, predict where the next feature should be. This is structural prediction, not sequence replay.

The implementation treats displacements as a fixed ordered sequence and replays them using modular indexing. This means:
- Predictions only work if exploration follows the same order as learning
- The system cannot predict locations for arbitrary movements
- Object structure is reduced to a fixed trajectory

**Fix:** Displacement predictions should be conditioned on the current (feature, location) pair, not on exploration step count. The displacement memory should map (current feature, current location) → possible next (displacement, expected feature) pairs.

---

### 10. Boosting Effectively Disabled by Default (BoostStrength = 0.0)

**Location:** `HtmEnhanced.cs:1104`

```csharp
public float BoostStrength { get; init; } = 0.0f;   // 0 = disabled (NuPIC default)
```

**Theory says:**
> "Boosting ensures all columns participate over time (no dead columns)."

With `BoostStrength = 0.0`, the exponential boost formula produces `exp(0 * ...) = 1.0` always. Boosting is a critical homeostatic mechanism — without it, a subset of columns will dominate while others never win, wasting representational capacity and violating the theory's requirement for distributed participation.

The `BumpWeakColumns` method (line 1370) partially compensates by increasing permanences on underperforming columns, but this is a cruder mechanism than proper boosting.

**Fix:** Set a non-zero default BoostStrength (NuPIC used values around 3.0 in practice). The comment correctly notes this matches NuPIC defaults, but NuPIC's default was intended for cases where users would configure boosting themselves.

---

### 11. Sensory Patch Sharing Across Columns

**Location:** `HtmEnhanced.cs:3139`

```csharp
sensoryPatches[i % sensoryPatches.Length]
```

**Theory says:**
> "Each column processes its OWN patch of the sensor array."

If fewer patches than columns are provided, patches wrap around and multiple columns receive identical input. When multiple columns have the same sensory input, their votes are redundant — they provide no additional discriminative power. The whole point of multiple columns is that each sees a different part of the object.

**Fix:** Enforce that the number of sensory patches matches the number of columns, or handle the mismatch explicitly rather than silently wrapping.

---

## Minor Drift

### 12. Grid Cell Anchoring Uses Hash-Code Lookup

**Location:** `HtmEnhanced.cs:2525–2539`

```csharp
int hash = sensoryInput.GetHashCode();
if (_anchorMemory.TryGetValue(hash, out var remembered))
```

The theory describes anchoring as sensory-driven correction of path integration drift through learned associations. The implementation uses `SDR.GetHashCode()` as a lookup key, which loses the noise-tolerance and partial-match properties of SDRs. Two slightly different sensory inputs that should anchor to the same position will get different hash codes and miss the memory entry.

---

### 13. No Explicit Minicolumn Abstraction

**Theory says:** Minicolumns (~80–120 neurons, ~50μm wide) are the smallest functional unit, sharing feedforward input.

**Implementation:** Uses a flat `CellsPerColumn` parameter (default 32) without any intermediate minicolumn grouping. The cells-per-column in the TM effectively models this, but the number (32) is below the biological range (80–120). This is a common simplification and acceptable for computational efficiency, but worth noting.

---

### 14. SP Permanence Initialization Around Connected Threshold

**Location:** `HtmEnhanced.cs:1220`

```csharp
float perm = _config.ConnectedThreshold + (float)(_rng.NextDouble() * 0.1 - 0.05);
```

Permanences are initialized in the range [ConnectedThreshold - 0.05, ConnectedThreshold + 0.05]. This means roughly half of initial synapses are connected. This is consistent with Numenta's implementation, but the tight range around the threshold means the SP starts with high sensitivity to small permanence changes.

---

### 15. Object Recognition Via Threshold Instead of Convergence

**Location:** `HtmEnhanced.cs:3156`

```csharp
if (match > bestMatch && match > 0.5f)
```

Recognition uses a hard-coded 0.5 match threshold. The theory describes recognition as the outcome of the convergence process itself — when columns converge on a representation, that representation IS the recognized object. The threshold-based matching against a library is an additional non-biological step.

---

### 16. No Learning Rate Decay or Schedule

The theory describes online learning that adapts over the lifetime of the system. All permanence increments/decrements are fixed constants. There is no mechanism for reducing learning rate as the system matures, which could cause continual churn in well-learned representations.

---

### 17. SDR Union Operations May Violate Sparsity

SDR union operations (used in `AccumulateObjectEvidence` and elsewhere) can produce SDRs with more active bits than the target sparsity. The theory relies on ~2% sparsity for its mathematical guarantees. While the code does trim back to `ObjectActiveBits` in some places, there is no global invariant enforcement.

---

## Summary Table

| # | Issue | Severity | Category | Status |
|---|-------|----------|----------|--------|
| 1 | Grid cells labeled allocentric (should be object-centric) | Critical | Naming/Conceptual | RESOLVED |
| 2 | Evidence accumulation grows instead of narrows | Critical | Recognition | RESOLVED |
| 3 | Feature + location combined into single SP (collapses layers) | Critical | Architecture | RESOLVED |
| 4 | Lateral voting uses blending instead of intersection | Critical | Voting | RESOLVED |
| 5 | Apical dendrites missing entirely | Major | Dendrites | RESOLVED |
| 6 | Square grid cells instead of hexagonal | Major | Grid Cells | RESOLVED |
| 7 | One grid module per column instead of multiple | Major | Grid Cells | RESOLVED |
| 8 | Object memory uses hash-table instead of SDR associations | Major | Object Layer | RESOLVED |
| 9 | Displacement prediction is sequence replay, not structural | Major | Displacement Cells | RESOLVED |
| 10 | Boosting disabled by default (BoostStrength=0.0) | Major | Spatial Pooler | RESOLVED |
| 11 | Sensory patches shared/wrapped across columns | Major | Architecture | RESOLVED |
| 12 | Grid anchoring uses hash-code lookup | Minor | Grid Cells | RESOLVED |
| 13 | No minicolumn abstraction (32 cells vs 80–120) | Minor | Architecture | ACCEPTED |
| 14 | SP permanence initialization range | Minor | Spatial Pooler | RESOLVED |
| 15 | Recognition by threshold instead of convergence | Minor | Recognition | RESOLVED |
| 16 | No learning rate decay | Minor | Learning | RESOLVED |
| 17 | SDR unions may violate sparsity | Minor | SDR | RESOLVED |

All 17 items addressed. 16 resolved via code changes, 1 accepted as standard deviation (#13).

---

## What the Implementation Gets Right

For completeness, these aspects are correctly implemented per the theory:

- **TM two-timestep state machine**: Learning looks backward, prediction looks forward. The save/cache/activate/learn/predict cycle is correct.
- **Permanence-based synaptic learning**: Synapses have continuous permanence values with threshold-based connectivity.
- **Bursting as surprise signal**: When no cell in a column is predicted, all cells fire.
- **Winner cell selection**: Best-matching segment first, then least-used cell.
- **Punishment of incorrect predictions**: Segments that predicted cells in non-active columns are weakened.
- **Anomaly = fraction of bursting columns**: Falls naturally out of the prediction mechanism.
- **Segment lifecycle management**: LRU eviction, synapse pruning, max-segment limits.
- **SDR mathematical properties**: SIMD overlap, subsampling, noise injection, union/intersection.
- **Encoder semantic contract**: Similar inputs produce overlapping SDRs.
- **Path integration**: Grid cells update position by integrating movement vectors.
- **Displacement cell arithmetic**: Correctly computes and applies displacement vectors on toroidal surface.
- **Lateral voting loop**: Iterative consensus with convergence detection.
