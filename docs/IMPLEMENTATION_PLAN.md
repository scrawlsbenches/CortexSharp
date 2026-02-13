# CortexSharp — Implementation Plan: Theoretical Alignment

> **Purpose:** This file is the persistent task tracker for fixing all 17 theoretical
> drift issues identified in `docs/THEORETICAL_DRIFT_AUDIT.md`. It is designed to
> survive context window compression — any future session can pick this up cold and
> know exactly what to do next.
>
> **Branch:** `claude/htm-thousand-brains-theory-BapCk`
> **Single source file:** `HtmEnhanced.cs` (all fixes modify this one file)
> **Theory reference:** `CLAUDE.md` and `docs/HTM_AND_THOUSAND_BRAINS_THEORY.md`
>
> **Status: ALL PHASES COMPLETE.** All 17 audit items addressed. All examples pass.

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
- **Status:** [x] Complete
- **Effort:** Trivial
- **Location:** `HtmEnhanced.cs` section 8 and 9 header comments
- **What was done:** Replaced all "allocentric" references with "object-centric" in grid cell and displacement cell section comments.
- **Acceptance criteria:** `grep -i allocentric HtmEnhanced.cs` returns zero matches. All grid/displacement comments say "object-centric."
- **Completed in commit:** Phase 1 commit

### Task 1.2: Enable boosting by default (BoostStrength > 0)
- **Audit item:** #10 (Major — Spatial Pooler)
- **Status:** [x] Complete
- **Effort:** Trivial
- **Location:** `SpatialPoolerConfig.BoostStrength`
- **What was done:** Changed default from `0.0f` to `3.0f`.
- **Completed in commit:** Phase 1 commit

### Task 1.3: Validate sensory patch count matches column count
- **Audit item:** #11 (Major — Architecture)
- **Status:** [x] Complete
- **Effort:** Trivial
- **Location:** `ThousandBrainsEngine.Process()`
- **What was done:** Added guard: throws `ArgumentException` if `sensoryPatches.Length != ColumnCount && != 1`. Allows broadcast (1 patch) or exact match.
- **Completed in commit:** Phase 1 commit

### Task 1.4: Widen SP permanence initialization range
- **Audit item:** #14 (Minor — Spatial Pooler)
- **Status:** [x] Complete
- **Effort:** Trivial
- **Location:** `SpatialPooler.InitializeProximalConnections()`
- **What was done:** Changed range from ±0.05 to ±0.1 around `ConnectedThreshold`.
- **Completed in commit:** Phase 1 commit

### Task 1.5: Add learning rate decay to SP and TM
- **Audit item:** #16 (Minor — Learning)
- **Status:** [x] Complete
- **Effort:** Small
- **What was done:** Added `LearningRateDecay` and `LearningRateDecayStartIteration` to both `SpatialPoolerConfig` and `TemporalMemoryConfig`. Applied exponential decay factor to permanence increments/decrements in SP.Compute() and TM learning methods. Default 1.0 = no change for existing users.
- **Completed in commit:** Phase 1 commit

### Task 1.6: CellsPerColumn assessment
- **Audit item:** #13 (Minor — Architecture)
- **Status:** [x] Complete (no code change needed)
- **What was done:** Determined that CellsPerColumn=32 is the standard computational value used by all Numenta reference implementations. The biological 80-120 count represents actual neurons; each HTM "cell" models a group. Accepted deviation.
- **Completed in commit:** Phase 1 commit (documented)

---

## Phase 2 — SDR Sparsity Foundation

Must be done before Phase 3 because intersection/union operations in Phase 3 can violate sparsity.

### Task 2.1: Add sparsity enforcement to SDR
- **Audit item:** #17 (Minor — SDR)
- **Status:** [x] Complete
- **Effort:** Small
- **What was done:** Added `SDR.EnforceSparsity(int maxActiveBits)` and `static SDR.UnionCapped(SDR a, SDR b, int maxActiveBits)` methods. UnionCapped prefers intersection bits, fills from union, caps at maxActiveBits.
- **Completed in commit:** Phase 2 commit

---

## Phase 3 — Recognition Pipeline Fixes

### Task 3.1: Change evidence accumulation from union to intersection
- **Audit item:** #2 (Critical — Recognition)
- **Status:** [x] Complete
- **Effort:** Medium
- **What was done:** Rewrote `AccumulateObjectEvidence()` to use intersection-based narrowing. If intersection has >= `ObjectActiveBits/4` bits, use it; otherwise reset to current (new candidate set). Progressive narrowing as theory requires.
- **Completed in commit:** Phase 3 commit

### Task 3.2: Change lateral voting from blending to intersection
- **Audit item:** #4 (Critical — Voting)
- **Status:** [x] Complete
- **Effort:** Medium
- **What was done:** Replaced `ReceiveLateralInput()` with intersection-based consensus. Removed `BlendSDRs()` entirely. Column keeps only bits agreed upon by both its representation and the consensus.
- **Completed in commit:** Phase 3 commit

### Task 3.3: Derive recognition from convergence, not threshold
- **Audit item:** #15 (Minor — Recognition)
- **Status:** [x] Complete
- **Effort:** Small
- **What was done:** Recognition now requires convergence before attempting library matching. Threshold lowered to 0.3 since convergence + match together provide strong confidence.
- **Completed in commit:** Phase 3 commit

---

## Phase 4 — Grid Cell System Overhaul

### Task 4.1: Implement hexagonal grid tiling for GridCellModule
- **Audit item:** #6 (Major — Grid Cells)
- **Status:** [x] Complete
- **Effort:** Large
- **What was done:** Complete rewrite of `GridCellModule` for hexagonal tiling using axial coordinates (q, r). Hex distance formula: `dist² = 3*(dq² + dq*dr + dr²)`. Cartesian-to-axial conversion. Gaussian bump activation on hex neighbors. Toroidal wrapping preserved. Same API surface (ModuleSize, TotalCells = N×N, public methods).
- **Completed in commit:** Phase 4 commit

### Task 4.2: Fix grid cell anchoring to use SDR overlap instead of hash
- **Audit item:** #12 (Minor — Grid Cells)
- **Status:** [x] Complete
- **Effort:** Medium
- **What was done:** Replaced `Dictionary<int, (float, float)>` (hash-keyed) with `List<(SDR Pattern, float Q, float R)>` anchor memory. Anchor lookup uses SDR overlap matching (threshold = 5 bits). Noise-tolerant: similar sensory inputs find the same anchor.
- **Completed in commit:** Phase 4 commit

### Task 4.3: Support multiple grid cell modules per column
- **Audit item:** #7 (Major — Grid Cells)
- **Status:** [x] Complete
- **Effort:** Large
- **What was done:** Changed `_gridModules` from `GridCellModule[]` to `GridCellModule[][]` (multiple modules per column). Added `ModulesPerColumn` config (default 3). Modules at different scales (1.0, 1.7, 2.4) and orientations. Location SDR = concatenation of all module outputs. `CorticalColumnConfig.LocationSize` auto-adjusted.
- **Completed in commit:** Phase 4 commit

---

## Phase 5 — Column Architecture Refactor (the big one)

### Task 5.1: Implement SDR-based associative object memory + two-layer column
- **Audit items:** #3 (Critical) and #8 (Major) — done together
- **Status:** [x] Complete
- **Effort:** Very Large
- **What was done:**
  - L4 SP processes ONLY sensory input (not combined with location)
  - New L2/3 Object Layer using `CellSegmentManager[]` with distal dendrite segments
  - Combined L4 output + location fed to L2/3 through `CombineL4AndLocation()`
  - L2/3 cells compete via top-k selection on segment activity
  - Novel inputs activate least-used cells
  - Learning reinforces/grows segments via Hebbian permanence-based learning
  - Removed `Dictionary<int, HashSet<int>> _objectMemory`
  - Removed `CombineFeatureAndLocation()`, `ProjectToObjectLayer()` (hash-based), `BlendSDRs()`
  - Added L23* config parameters
  - `ColumnCount` → `L4ColumnCount` rename with backward compatibility
- **Completed in commit:** Phase 5 commit

### Task 5.2: Rewrite displacement prediction as structure-based
- **Audit item:** #9 (Major — Displacement Cells)
- **Status:** [x] Complete
- **Effort:** Large
- **What was done:** Changed displacement storage from ordered `List<SDR>` to `List<(SDR SourceLocation, SDR Displacement, SDR TargetLocation)>`. Predictions now use SDR overlap matching on current location, not step index. Exploring an object in a different order than during learning produces correct predictions.
- **Completed in commit:** Phase 5 commit

---

## Phase 6 — Apical Dendrites

### Task 6.1: Add apical dendrite support
- **Audit item:** #5 (Major — Dendrites)
- **Status:** [x] Complete
- **Effort:** Very Large
- **What was done:**
  - Added `DendriteType` enum: `{ Proximal, Distal, Apical }`
  - Added apical config parameters to `CorticalColumnConfig` (thresholds, permanences, boost factor)
  - Added `_l23ApicalCells[]` — separate `CellSegmentManager` per cell for apical segments
  - `CorticalColumn.Compute()` accepts optional `apicalInput` SDR parameter
  - L2/3 cell selection: apical depolarization boosts cell priority (modulatory, not driving — matches biology)
  - Apical learning: active L2/3 cells grow/reinforce apical segments to associate with top-down context
  - `ThousandBrainsEngine` passes previous consensus as apical input to columns
  - `StartNewObject()` resets `_prevConsensus`
- **Completed in commit:** Phase 6 commit

---

## Post-Implementation

After all phases are complete:
- [x] Run all examples in `Program.cs` to verify nothing is broken — ALL 11 PASS
- [x] Update this implementation plan with completion status
- [ ] Update `docs/THEORETICAL_DRIFT_AUDIT.md` — mark all items as resolved
- [x] Commit final state with summary of all changes
