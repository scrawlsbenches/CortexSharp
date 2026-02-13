# CortexSharp — Implementation Plan: Theoretical Alignment

> **Purpose:** This file is the persistent task tracker for fixing all 17 theoretical
> drift issues identified in `docs/THEORETICAL_DRIFT_AUDIT.md`. It is designed to
> survive context window compression — any future session can pick this up cold and
> know exactly what to do next.
>
> **Branch:** `claude/htm-thousand-brains-theory-BapCk`
> **Single source file:** `HtmEnhanced.cs` (all fixes modify this one file)
> **Theory reference:** `CLAUDE.md` and `docs/HTM_AND_THOUSAND_BRAINS_THEORY.md`

---

## How to Use This File

1. Find the first unchecked (`[ ]`) task
2. Read its description, location, and acceptance criteria
3. Do the work
4. Mark it `[x]` and update the "Completed in commit" field
5. Commit this file alongside the code change

---

## Phase 1 — Standalone Fixes (no dependencies, no behavioral coupling)

These can be done in any order. Each is isolated to a single class or config default.

### Task 1.1: Rename "allocentric" to "object-centric" in grid cell comments
- **Audit item:** #1 (Critical — naming/conceptual)
- **Status:** [ ] Not started
- **Effort:** Trivial
- **Location:** `HtmEnhanced.cs:2414-2427` (section header comment), `HtmEnhanced.cs:2561-2572` (displacement cell comment also references "allocentric")
- **What to do:** Replace "allocentric (world-centered)" with "object-centric (object-relative)" in the section 8 header. Review displacement cell section header (section 9) for similar language. Leave code behavior unchanged.
- **Acceptance criteria:** `grep -i allocentric HtmEnhanced.cs` returns zero matches. All grid/displacement comments say "object-centric."
- **Completed in commit:** —

### Task 1.2: Enable boosting by default (BoostStrength > 0)
- **Audit item:** #10 (Major — Spatial Pooler)
- **Status:** [ ] Not started
- **Effort:** Trivial
- **Location:** `HtmEnhanced.cs:1104` — `SpatialPoolerConfig.BoostStrength` default
- **What to do:** Change `BoostStrength` default from `0.0f` to `3.0f`. Update comment to explain this is the standard active value (Numenta used similar ranges). The exponential boosting logic at lines 1357-1358 already exists and will start working.
- **Acceptance criteria:** Default config produces boost factors != 1.0 after sufficient iterations. No dead columns with default config on standard inputs.
- **Completed in commit:** —

### Task 1.3: Validate sensory patch count matches column count
- **Audit item:** #11 (Major — Architecture)
- **Status:** [ ] Not started
- **Effort:** Trivial
- **Location:** `HtmEnhanced.cs:3139` — `ThousandBrainsEngine.Process()`
- **What to do:** Replace the silent `i % sensoryPatches.Length` wrap with a guard: `if (sensoryPatches.Length != _config.ColumnCount) throw new ArgumentException(...)`. Alternatively, document clearly if intentional sharing is a valid use case and add an explicit `AllowPatchSharing` config flag. The silent modulo is the problem — it hides a logic error.
- **Acceptance criteria:** Passing fewer patches than columns throws or warns. Passing the correct count works unchanged.
- **Completed in commit:** —

### Task 1.4: Widen SP permanence initialization range
- **Audit item:** #14 (Minor — Spatial Pooler)
- **Status:** [ ] Not started
- **Effort:** Trivial
- **Location:** `HtmEnhanced.cs:1220` — `SpatialPooler.InitializeProximalConnections()`
- **What to do:** Change `_rng.NextDouble() * 0.1 - 0.05` (range ±0.05 around threshold) to `_rng.NextDouble() * 0.2 - 0.1` (range ±0.1). This means ~50% start connected vs ~50% before, but with more variance — columns start with more diverse connectivity. The range ±0.1 is closer to NuPIC's `synPermActiveInc` default.
- **Acceptance criteria:** Permanences initialize in range [ConnectedThreshold-0.1, ConnectedThreshold+0.1].
- **Completed in commit:** —

### Task 1.5: Add learning rate decay to SP and TM
- **Audit item:** #16 (Minor — Learning)
- **Status:** [ ] Not started
- **Effort:** Small
- **Location:** `SpatialPoolerConfig` (line 1090), `TemporalMemoryConfig` (line 1476), `SpatialPooler.Compute()` (line 1228), `TemporalMemory.Compute()` (line 1549)
- **What to do:** Add optional decay parameters to both configs:
  ```
  public float LearningRateDecay { get; init; } = 1.0f;  // 1.0 = no decay
  public int LearningRateDecayStartIteration { get; init; } = 0;
  ```
  In each Compute(), compute an effective multiplier:
  ```
  float decayFactor = _iteration > _config.LearningRateDecayStartIteration
      ? MathF.Pow(_config.LearningRateDecay, _iteration - _config.LearningRateDecayStartIteration)
      : 1.0f;
  ```
  Apply to permanence increment/decrement. Default of 1.0 means zero behavior change for existing users.
- **Acceptance criteria:** With default config, behavior is identical. With decay < 1.0, permanence changes diminish over iterations.
- **Completed in commit:** —

### Task 1.6: Increase CellsPerColumn default
- **Audit item:** #13 (Minor — Architecture)
- **Status:** [ ] Not started
- **Effort:** Trivial (default change) to Medium (minicolumn abstraction)
- **Location:** `HtmEnhanced.cs:1479` — `TemporalMemoryConfig.CellsPerColumn`
- **What to do:** Phase 1 scope: just increase the default from `32` to `32`. Actually, 32 is already the standard computational value used by Numenta's reference implementations (nupic, htm.core) — the biological count of 80-120 is the actual neuron count, but HTM uses a lower number because each "cell" in HTM represents a group. **No change needed.** Mark as "accepted deviation" in the audit doc.
- **Acceptance criteria:** Update audit doc to note this is an intentional and standard simplification.
- **Completed in commit:** —

---

## Phase 2 — SDR Sparsity Foundation

Must be done before Phase 3 because intersection/union operations in Phase 3 can violate sparsity.

### Task 2.1: Add sparsity enforcement to SDR
- **Audit item:** #17 (Minor — SDR)
- **Status:** [ ] Not started
- **Effort:** Small
- **Location:** `HtmEnhanced.cs:57-395` — `SDR` class
- **What to do:** Add two methods:
  1. `public SDR EnforceSparsity(int maxActiveBits)` — if `ActiveCount > maxActiveBits`, keep only the first `maxActiveBits` indices (deterministic, lowest-index selection). Returns a new SDR.
  2. `public static SDR UnionCapped(SDR a, SDR b, int maxActiveBits)` — union of a and b, then enforce sparsity. Prefer bits that appear in both (intersection first), fill from union.

  Do NOT modify existing `Union()` behavior — add new methods. Existing callers are unchanged.
- **Acceptance criteria:** `UnionCapped` never returns more than `maxActiveBits` active. `EnforceSparsity` trims correctly.
- **Completed in commit:** —

---

## Phase 3 — Recognition Pipeline Fixes

These fix how columns accumulate evidence and vote. They operate on the current single-layer column architecture. If Phase 5 later refactors the column, these methods will be rewritten — but the logic (intersection not union) will carry over, and having correct behavior now makes Phase 5 validation easier.

### Task 3.1: Change evidence accumulation from union to intersection
- **Audit item:** #2 (Critical — Recognition)
- **Status:** [ ] Not started
- **Effort:** Medium
- **Location:** `HtmEnhanced.cs:2881-2901` — `CorticalColumn.AccumulateObjectEvidence()`
- **Depends on:** Task 2.1 (sparsity enforcement)
- **What to do:** Replace the current logic (intersection-boosted union) with:
  1. Compute intersection of `previous` and `current`
  2. If intersection has enough active bits (>= some minimum, e.g., `ObjectActiveBits / 4`), use it
  3. If intersection is too sparse (first touch or very different observation), use `current` alone — this is the "reset" case where the new observation starts a new candidate set
  4. If `previous` is empty, return `current` (first observation — this case already works)

  This implements the theory's "progressive narrowing" — each observation eliminates candidates.
- **Acceptance criteria:** After N observations of consistent (feature, location) pairs for one object, the representation converges to a stable intersection. After an inconsistent observation, the representation resets rather than growing.
- **Completed in commit:** —

### Task 3.2: Change lateral voting from blending to intersection
- **Audit item:** #4 (Critical — Voting)
- **Status:** [ ] Not started
- **Effort:** Medium
- **Location:** `HtmEnhanced.cs:2807-2828` — `CorticalColumn.ReceiveLateralInput()`, `HtmEnhanced.cs:2904-2920` — `BlendSDRs()`
- **Depends on:** Task 2.1 (sparsity enforcement)
- **What to do:** Replace `ReceiveLateralInput()` logic:
  1. Compute intersection of `_currentObjectRepresentation` and `consensusRepresentation`
  2. If intersection has sufficient bits, adopt it as the new representation
  3. If intersection is empty but consensus is strong (many bits), adopt consensus (column defers to collective wisdom)
  4. If both are weak, keep current representation (no lateral influence yet)

  Remove `BlendSDRs()` entirely — it implements the wrong operation.
- **Acceptance criteria:** After a voting round, all columns' representations are subsets of the consensus (or equal to it). The representation never grows from voting.
- **Completed in commit:** —

### Task 3.3: Derive recognition from convergence, not threshold
- **Audit item:** #15 (Minor — Recognition)
- **Status:** [ ] Not started
- **Effort:** Small
- **Location:** `HtmEnhanced.cs:3149-3161` — `ThousandBrainsEngine.Process()` recognition block
- **Depends on:** Tasks 3.1 and 3.2
- **What to do:** Replace the `match > 0.5f` threshold logic:
  1. Check if `_voting.HasConverged(votes)` — if columns haven't converged, don't attempt recognition yet (return `RecognizedObject = null`)
  2. If converged, match the consensus against the object library using the existing `MatchScore` — but use the convergence itself as the confidence signal, not an arbitrary threshold
  3. The recognition threshold can still exist but should be much higher (e.g., 0.7) since convergence + match should produce high scores for known objects
- **Acceptance criteria:** Unknown objects that haven't been learned produce no recognition regardless of threshold. Known objects are recognized after convergence.
- **Completed in commit:** —

---

## Phase 4 — Grid Cell System Overhaul

Rebuild the location system to be biologically accurate.

### Task 4.1: Implement hexagonal grid tiling for GridCellModule
- **Audit item:** #6 (Major — Grid Cells)
- **Status:** [ ] Not started
- **Effort:** Large
- **Location:** `HtmEnhanced.cs:2429-2557` — `GridCellModuleConfig` and `GridCellModule`
- **What to do:**
  1. Replace the square `ModuleSize x ModuleSize` grid with a hexagonal tiling
  2. Use axial coordinates (q, r) for the hex grid — standard in hex grid implementations
  3. Adjust `GetCurrentLocation()` to compute Gaussian bump activations on hex neighbors
  4. Adjust `Move()` to path-integrate on the hex coordinate system
  5. Toroidal wrapping needs to work on the hex lattice (this is the tricky part — hex tori have specific constraints)
  6. Update `GridCellModuleConfig`: replace `ModuleSize` with hex-appropriate sizing (e.g., `HexRadius` defining the number of rings)
  7. `TotalCells` computation changes for hex grids: for radius R, total = 3R² + 3R + 1
- **Acceptance criteria:** Grid cell activations form a periodic hexagonal pattern. Path integration produces smooth movement across the hex surface. Toroidal wrapping is correct (moving off one edge arrives at the opposite edge).
- **Completed in commit:** —

### Task 4.2: Fix grid cell anchoring to use SDR overlap instead of hash
- **Audit item:** #12 (Minor — Grid Cells)
- **Status:** [ ] Not started
- **Effort:** Medium
- **Location:** `HtmEnhanced.cs:2525-2539` — `GridCellModule.Anchor()` and `_anchorMemory`
- **Depends on:** Task 4.1 (refactoring GridCellModule)
- **What to do:** Replace `Dictionary<int, (float, float)>` keyed by `GetHashCode()` with a list of `(SDR Pattern, float X, float Y)` entries. On anchor lookup, find the stored pattern with highest overlap to the current sensory input (above a minimum threshold). This provides noise-tolerant matching.
- **Acceptance criteria:** Slightly noisy versions of a previously anchored sensory input still trigger position correction. Completely different inputs do not.
- **Completed in commit:** —

### Task 4.3: Support multiple grid cell modules per column
- **Audit item:** #7 (Major — Grid Cells)
- **Status:** [ ] Not started
- **Effort:** Large
- **Location:** `HtmEnhanced.cs:3070-3118` — `ThousandBrainsEngine` constructor and `_gridModules` array, also `CorticalColumnConfig` and `CorticalColumn`
- **Depends on:** Task 4.1 (hexagonal modules should be correct before multiplying them)
- **What to do:**
  1. Change `_gridModules` from `GridCellModule[ColumnCount]` (one per column) to `GridCellModule[ColumnCount][]` (array of modules per column)
  2. Each column gets N modules at different scales/orientations (N=3-4 is typical in the papers)
  3. The location SDR for a column becomes the **concatenation** of all its modules' location SDRs
  4. `CorticalColumnConfig.LocationSize` must accommodate the combined size
  5. `ThousandBrainsEngine.Process()` must call `Move()` on all modules for all columns
  6. `ThousandBrainsEngine.StartNewObject()` must reset all modules
- **Acceptance criteria:** Each column has N grid modules. Location SDR is richer (more bits, higher resolution). Two locations that are ambiguous with one module are disambiguated with multiple modules at different scales.
- **Completed in commit:** —

---

## Phase 5 — Column Architecture Refactor (the big one)

This is the most architecturally significant change. It splits the single-SP cortical column into a biologically accurate two-layer structure.

### Task 5.1: Implement SDR-based associative object memory + two-layer column
- **Audit items:** #3 (Critical) and #8 (Major) — tightly coupled, done together
- **Status:** [ ] Not started
- **Effort:** Very Large
- **Location:** `HtmEnhanced.cs:2706-2920` — `CorticalColumnConfig`, `CorticalColumn`
- **Depends on:** Phases 1-3 ideally complete. Phase 4 is independent but nice-to-have.
- **What to do:**
  1. **L4 layer (input):** The existing `_featureSP` processes ONLY `sensoryInput` (not combined with location). Config `InputSize` drops to just the sensory size.
  2. **L2/3 layer (object):** New component that:
     - Receives L4 output (active columns → feature representation)
     - Receives location SDR from grid cells
     - Associates (feature, location) pairs with object representations
     - Uses permanence-based synaptic connections (like TM segments) — NOT a hash-table
     - Maintains candidate object set that narrows with each observation
  3. **Remove** `_objectMemory = new Dictionary<int, HashSet<int>>()`
  4. **Remove** `CombineFeatureAndLocation()` — no longer needed
  5. **Rework** `Compute()` to be a proper L4 → L2/3 pipeline
  6. **Rework** `ProjectToObjectLayer()` — the object representation should come from L2/3 cell activity, not hashed TM cells
  7. The L2/3 layer should use TM-like mechanics: cells with distal segments that learn (feature+location) → object associations via Hebbian learning
- **Acceptance criteria:** Column processes sensory input through L4 SP, then combines with location in a separate L2/3 layer. Object memory is stored in synaptic permanences, not a hash table. The column's object representation comes from L2/3 cell activity.
- **Completed in commit:** —

### Task 5.2: Rewrite displacement prediction as structure-based
- **Audit item:** #9 (Major — Displacement Cells)
- **Status:** [ ] Not started
- **Effort:** Large
- **Location:** `HtmEnhanced.cs:3079-3188` — `ThousandBrainsEngine` displacement logic
- **Depends on:** Task 5.1 (the associative memory pattern)
- **What to do:**
  1. Replace `_objectDisplacements = Dictionary<string, List<SDR>>` (ordered sequence) with an associative structure: `(feature SDR, location SDR)` → `List<(displacement SDR, expected feature SDR)>`
  2. During learning: when moving from (feature_A, location_A) to (feature_B, location_B), store the displacement and the expected target feature
  3. During recognition: given current (feature, location) and recognized object, look up which displacements were learned from this (feature, location) and predict possible next locations + expected features
  4. Remove the modular-index replay (`nextIdx = (steps - 1) % count`)
- **Acceptance criteria:** Predictions are conditioned on current (feature, location), not step count. Exploring an object in a different order than during learning still produces correct predictions.
- **Completed in commit:** —

---

## Phase 6 — Apical Dendrites

The most ambitious addition. Requires the two-layer column from Phase 5 to exist.

### Task 6.1: Add apical dendrite support
- **Audit item:** #5 (Major — Dendrites)
- **Status:** [ ] Not started
- **Effort:** Very Large
- **Location:** `HtmEnhanced.cs:876-1055` (DendriteSegment, CellSegmentManager), `HtmEnhanced.cs:1496-1975` (TemporalMemory), `HtmEnhanced.cs:2717-2920` (CorticalColumn)
- **Depends on:** Task 5.1 (two-layer column with L2/3)
- **What to do:**
  1. Add a `DendriteType` enum: `{ Proximal, Distal, Apical }`
  2. Extend `CellSegmentManager` to manage apical segments separately from distal
  3. Add apical segment computation: apical input depolarizes L2/3 cells (top-down prediction/attention)
  4. Add an apical input pathway to `CorticalColumn.Compute()` — a new parameter for top-down feedback SDR
  5. In the TM or L2/3 layer: cells with both distal (lateral/temporal context) AND apical (top-down) depolarization are in the strongest predictive state
  6. Define the source of apical input — for a single-region system this could come from the consensus (lateral voting already provides a form of "expectation"); for a hierarchical system it would come from higher regions via the NetworkAPI
- **Acceptance criteria:** Cells have three distinct dendritic zones. Apical input modulates prediction strength. Top-down feedback can bias which cells become predictive.
- **Completed in commit:** —

---

## Post-Implementation

After all phases are complete:
- [ ] Update `docs/THEORETICAL_DRIFT_AUDIT.md` — mark all items as resolved
- [ ] Update `CLAUDE.md` if any theoretical descriptions need revision
- [ ] Run all examples in `Program.cs` to verify nothing is broken
- [ ] Commit final state with summary of all changes
