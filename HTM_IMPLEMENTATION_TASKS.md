# HTM Enhanced — Prioritized Implementation Task List

## How to read this list

Tasks are grouped into four tiers by impact and dependency order. Within each tier they are sequenced so that earlier items unblock later ones. Each task includes a scope estimate (S/M/L) reflecting relative effort, the section(s) affected, and a brief rationale.

---

## Tier 1 — Fix Broken Stubs That Block Core Functionality

These are things that exist in the code right now as non-functional placeholders. Anything downstream of them silently produces wrong results or no-ops.

### 1.1 ~~Add `DendriteSegment.BumpPermanences()` and remove the reflection hack (§3, §4)~~ **[S]** — DONE

- Added `BumpAllPermanences(float amount)` to `DendriteSegment` using `CollectionsMarshal.AsSpan` for zero-copy mutation, consistent with `AdaptSynapses`/`PunishSynapses`.
- Replaced reflection hack in `BumpWeakColumns` with: `_proximalDendrites[col].BumpAllPermanences(0.1f * _config.ConnectedThreshold)`.
- Bump amount corrected to BAMI's `0.1 * connectedPerm` (the stub had `0.01f`, an order of magnitude too low).

### 1.2 ~~Implement `SPRegion.Serialize()` / `Deserialize()` (§13)~~ **[M]** — DONE

- Added `SerializeState(BinaryWriter)` / `DeserializeState(BinaryReader)` to `SpatialPooler` (proximal dendrites via `WriteSegment`/`ReadSegment`, boost factors, duty cycles, iteration; validated against config dimensions).
- Added `SerializeState(BinaryWriter)` / `DeserializeState(BinaryReader)` to `TemporalMemory` (all `CellSegmentManager` contents, current cell sets, iteration).
- Added `ClearSegments()` / `RestoreSegment()` to `CellSegmentManager` for deserialization support.
- SPRegion/TMRegion now store config, serialize config + state into self-describing blobs, and provide static `CreateFromData()` factory methods for reconstruction.
- Region blobs include full config so deserialization is self-contained (no external config needed).

### 1.3 ~~Implement `HtmSerializer.LoadNetwork()` (§14)~~ **[M]** — DONE

- Implemented `LoadNetwork(string filePath)` that reads the `SaveNetwork` format: magic, version, type, regions (with factory reconstruction), links.
- Added a static `_regionFactory` dictionary mapping type names to `Func<string, byte[], IRegion>` factories. Pre-registered `SPRegion` and `TMRegion`.
- Added `RegisterRegionFactory()` for user-registered custom region types.
- Updated `SaveNetwork` to write FNV-1a checksum at end of file. `LoadNetwork` verifies it before parsing.
- Regions are reconstructed via `CreateFromData()` static factories (reads config from blob, constructs region, restores learned state).

### 1.4 ~~Wire displacement cell predictions into `ThousandBrainsEngine.Process()` (§12)~~ **[M]** — DONE

- During `Process()` with learning: computes displacement SDR between previous and current grid cell locations via `DisplacementCellModule.ComputeDisplacement()`, accumulates into `_currentDisplacements`.
- `LearnObject()` now stores the displacement sequence in `_objectDisplacements[label]`.
- During recognition: if the recognized object has stored displacements, uses `PredictTarget()` to predict the next expected grid cell location.
- `StartNewObject()` resets displacement state (`_prevGridLocations`, `_currentDisplacements`).
- Added `PredictedNextLocation` field to `ThousandBrainsOutput` record.

---

## Tier 2 — Complete Partially Implemented Features

These work at a basic level but have gaps that limit correctness, robustness, or coverage.

### 2.1 Fix `CategoryEncoder.EncodeWithSimilarity()` mutation side effect (§2) **[S]**

Line 666: `_categoryBits[value] = result` overwrites the category's canonical encoding. If you call `EncodeWithSimilarity("cat", "dog", 5)` and then later call `Encode("cat")`, you get the mutated bits, not the original. This violates the encoder contract (same input → same output).

- Compute and return the similarity-adjusted SDR without writing it back to `_categoryBits`.
- If the caller needs persistent similarity relationships, provide a separate `SetSimilarity()` method or a similarity graph at construction time.

### 2.2 Add `AnomalyLikelihood` Welford variance reset mechanism (§6) **[S]**

CLAUDE.md flags this: "Welford variance can drift over very long streams." The running mean/M2 accumulator has no decay, so after millions of iterations the statistics become dominated by ancient history and stop tracking regime changes.

- Add a configurable `reestimationInterval` (default: 10× window size).
- When hit, reinitialize mean/M2 from the current `_scoreHistory` queue contents rather than the full lifetime accumulator.
- Alternatively, switch to an exponentially weighted variance estimator that naturally forgets.

### 2.3 Implement full SP/TM serialization for standalone use (§14) **[M]**

The serializer handles SDRs and Network topology, but there is no way to serialize a standalone `SpatialPooler` or `TemporalMemory` outside of the NetworkAPI. The `HtmEngine` (§17) composes SP+TM directly, not via Network, so it has no save/load path.

- Add `SerializeSpatialPooler(SpatialPooler sp)` and `SerializeTemporalMemory(TemporalMemory tm)` to `HtmSerializer`.
- Add corresponding deserializers that reconstruct a fully trained instance.
- Wire these into `HtmEngine` as `Save(string path)` / `Load(string path)` convenience methods.

### 2.4 Implement `Network` deserialization with region type registry (§13, §14) **[M]**

`SaveNetwork` writes the fully qualified type name string for each region, but there is no mechanism to map that string back to a constructor. The Network class also has no `Deserialize` method.

- Add a static `RegionFactory` that maps type names to factory functions: `Dictionary<string, Func<string, byte[], IRegion>>`.
- Pre-register `SPRegion` and `TMRegion`. Allow user registration for custom regions.
- Implement `LoadNetwork()` using this factory.

### 2.5 Add `TMRegion` and `SPRegion` reset with proper state clearing (§13) **[S]**

`SPRegion.Reset()` and `TMRegion.Reset()` are empty method bodies. For the NetworkAPI to support multi-sequence learning (reset between sequences), these need to actually clear TM cell state and, optionally, SP duty cycles.

- `TMRegion.Reset()`: clear active/winner/predictive cell sets and segment caches; do not clear learned synapses.
- `SPRegion.Reset()`: optional — may be a no-op for SP, but document the decision.

---

## Tier 3 — Add Missing BAMI-Documented Features

These are algorithms or concepts that BAMI describes but the implementation does not yet include. They extend the system's capabilities without fixing existing breakage.

### 3.1 Add a Delta Encoder (§2) **[S]**

BAMI describes a delta encoder that encodes the *change* in a value rather than the absolute value. This is important for time-series applications where patterns appear in derivatives (e.g., temperature change patterns that repeat regardless of baseline).

- Implement `DeltaEncoder : IEncoder<double>` that maintains the previous value and encodes `current - previous` using an internal `ScalarEncoder`.
- Handle the first-call edge case (no previous value) by returning a zero-centered encoding.
- Register as a composable encoder via `CompositeEncoder`.

### 3.2 Add a Temporal Pooling layer (§5 or new section) **[L]**

BAMI references temporal pooling as a mechanism for forming stable representations over time — outputs that remain constant while an expected sequence plays out, and change only on sequence transitions. This is the bridge between sequence memory and hierarchy.

- Implement as a post-TM processing step that unions predicted cell activity over a stability window.
- The output SDR should be stable during a correctly predicted sequence and change sharply at sequence boundaries or anomalies.
- This is architecturally necessary for meaningful hierarchical multi-region configurations.

### 3.3 Build a hierarchical multi-region example (§13, §18) **[M]**

The `Network` class supports DAG topologies and the `CreateStandardPipeline` factory builds SP→TM, but there is no example of actual hierarchy (e.g., SP→TM→TemporalPooler→SP→TM at a higher level). BAMI's core thesis is hierarchical processing.

- Add a `Network.CreateHierarchicalPipeline()` factory that wires at least two levels.
- Add a corresponding example in `HtmExamples` demonstrating that the higher level learns slower, more abstract patterns.
- This depends on 3.2 (temporal pooling) to produce meaningful inter-level representations.

### 3.4 Implement `Network` support for recurrent connections (§13) **[L]**

CLAUDE.md flags this: "Network does not support cycles (recurrent connections) — would need iterative settling." The current topological sort throws on cycles. Feedback connections are biologically essential (they're how predictions flow top-down).

- Detect cycles during sort and switch to an iterative settling loop for those subgraphs.
- Add a `MaxSettlingIterations` config and convergence detection (output stability across iterations).
- Mark feedback links distinctly from feedforward links so the scheduler knows which edges to break.

### 3.5 Add an Encoder Region for the NetworkAPI (§13) **[S]**

The NetworkAPI has `SPRegion` and `TMRegion` but no `EncoderRegion`. To build a complete pipeline in the NetworkAPI (raw data → encoder → SP → TM), the encoder must be wrappable as a region.

- Implement `EncoderRegion<T> : IRegion` that wraps any `IEncoder<T>`.
- Input port: raw value (boxed `T`). Output port: SDR.
- Enables fully declarative pipeline construction end-to-end.

### 3.6 Add a Classifier/Predictor Region for the NetworkAPI (§13) **[S]**

Same gap as encoders — `SdrPredictor` has no region wrapper.

- Implement `PredictorRegion : IRegion` wrapping `SdrPredictor`.
- Input port: active cells (`HashSet<int>`). Output port: predictions.
- Completes the full pipeline: Encoder → SP → TM → Predictor, all in NetworkAPI.

---

## Tier 4 — Engineering Hardening and Performance

These improve production-readiness without changing algorithmic behavior.

### 4.1 Add comprehensive unit tests (all sections) **[L]**

CLAUDE.md provides detailed testing guidelines but there are no test files in the project. Priority order for test coverage:

1. **SDR invariants**: self-overlap, symmetry, noise injection, bitvector-vs-sorted-merge equivalence, union/intersect bounds.
2. **Encoder contracts**: similar-input → high overlap, dissimilar → low overlap, sparsity bounds, periodic wrapping, category zero-overlap.
3. **SP convergence**: output sparsity matches target ± tolerance, no dead columns after boosting, local inhibition spatial distribution.
4. **TM learning**: anomaly drops on repeated sequences, spikes on novel input, segment count bounded, cleanup reduces counts.
5. **Thousand Brains**: learn-then-recognize produces high confidence, different objects distinguish, convergence iterations decrease with more observations.

### 4.2 Make the project actually compilable (global) **[M]**

CLAUDE.md says "this is not a library you `dotnet build` out of the box." For testing and validation, it should be.

- Add a `.csproj` targeting .NET 8+.
- Fix any compilation errors (the `#pragma warning disable` hints at known issues).
- Add a test project alongside.
- Gate this as a "reference build" — it doesn't need to be NuGet-packaged, just CI-green.

### 4.3 Profile and optimize `TM.BuildSegmentCaches()` for large models (§5) **[M]**

CLAUDE.md notes this is O(total_segments) per timestep and suggests spatial partitioning for >1M segments. Currently there's no partitioning — it's a flat linear scan.

- Add a cell-range index so that only segments belonging to active/predicted columns are evaluated.
- Benchmark before and after to quantify the improvement at realistic segment counts (100K, 500K, 1M).

### 4.4 Add proper `IDisposable` lifecycle to `MultiStreamProcessor` (§16) **[S]**

The `MultiStreamProcessor` creates background tasks via `Channel<T>` but has no clean shutdown path. Long-running deployments will leak tasks on reconfiguration.

- Implement `IAsyncDisposable` or `IDisposable`.
- On dispose: complete all channels, await all worker tasks, clear state.

### 4.5 Add configurable logging/tracing hooks (§15, §17) **[S]**

`HtmDiagnostics` and `Metrics` classes collect data but there's no way to export it. Add optional callback hooks or `ILogger` integration so that metrics can be streamed to external systems during long training runs.

### 4.6 Harden the serialization format (§14) **[S]**

- Actually write and verify the FNV-1a checksum during save/load (it's implemented but unused).
- Add a format version migration path so that v2 can read v1 files.
- Handle endianness explicitly for cross-platform compatibility.
