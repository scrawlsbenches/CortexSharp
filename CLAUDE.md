# CLAUDE.md — HTM Enhanced Codebase

## Project Overview

This is a **Hierarchical Temporal Memory (HTM)** implementation in C# based on Numenta's BAMI theory and the Thousand Brains Framework. It is a single-file reference architecture (`HtmEnhanced.cs`, ~5000 lines, 18 sections) intended as high-level pseudocode that prioritizes algorithmic clarity while using real C# idioms, types, and patterns.

**Namespace:** `HierarchicalTemporalMemory.Enhanced`
**Target:** .NET 8+ (C# 12). Uses `System.Numerics`, `System.Runtime.Intrinsics`, `System.Threading.Channels`.

This is **not** a library you `dotnet build` out of the box — it is a design document expressed as compilable-shaped code. Treat it as the authoritative architectural blueprint.

## Core HTM Pipeline

```
Raw Data → Encoder → Spatial Pooler → Temporal Memory → Temporal Pooler → Predictor
                                           ↓                   ↓
                                    Anomaly Likelihood    Stable SDR
                                                      (sequence-level)
```

Every change you make must preserve this pipeline's data flow contract:
- Encoders produce `SDR` (sparse binary vectors)
- Spatial Pooler consumes `SDR`, produces `SDR` (fixed sparsity)
- Temporal Memory consumes `SDR` (columns), produces `TemporalMemoryOutput` (cells + anomaly)
- Temporal Pooler consumes `TemporalMemoryOutput`, produces `TemporalPoolerOutput` (stable pooled SDR for hierarchy)
- Predictor consumes `HashSet<int>` (active cells from TM), produces `Dictionary<int, SdrPrediction>`

## File Structure & Section Map

| Section | Lines | Key Types | Purpose |
|---------|-------|-----------|---------|
| §1 | 45–376 | `SDR` | SIMD bitvector ops, noise, subsampling, projection |
| §2 | 377–839 | `ScalarEncoder`, `RDSE`, `DateTimeEncoder`, `CategoryEncoder`, `GeospatialEncoder`, `DeltaEncoder`, `CompositeEncoder` | Raw data → SDR conversion |
| §3 | 840–1068 | `Synapse`, `DendriteSegment`, `CellSegmentManager` | Synaptic infrastructure + lifecycle |
| §4 | 1069–1442 | `SpatialPooler`, `SpatialPoolerConfig`, `SpatialPoolerMetrics` | Competitive learning with local/global inhibition |
| §5 | 1443–2021 | `TemporalMemory`, `TemporalMemoryConfig`, `TemporalMemoryOutput`, `TemporalMemoryMetrics` | Sequence memory + prediction |
| §5b | 2022–2138 | `TemporalPooler`, `TemporalPoolerConfig`, `TemporalPoolerOutput` | Stable sequence-level pooling for hierarchy |
| §6 | 2139–2277 | `AnomalyLikelihood` | Statistical anomaly scoring (Welford + Gaussian tail) |
| §7 | 2278–2380 | `SdrPredictor`, `SdrPrediction` | Multi-step value prediction from cell activity |
| §8 | 2381–2527 | `GridCellModule`, `GridCellModuleConfig` | Allocentric location via toroidal grid |
| §9 | 2528–2657 | `DisplacementCellModule` | Relative offset encoding between locations |
| §10 | 2658–2892 | `CorticalColumn`, `CorticalColumnConfig`, `CorticalColumnOutput` | Feature-at-location processing unit |
| §11 | 2893–3008 | `LateralVotingMechanism`, `LateralVotingConfig` | Multi-column consensus |
| §12 | 3009–3213 | `ThousandBrainsEngine`, `ThousandBrainsConfig`, `ThousandBrainsOutput` | Full object learning/recognition |
| §13 | 3214–3926 | `IRegion`, `SPRegion`, `TMRegion`, `EncoderRegion`, `PredictorRegion`, `TemporalPoolerRegion`, `Network`, `RegionLink` | Declarative computation graph |
| §14 | 3927–4285 | `HtmSerializer` | Binary serialization with magic number + versioning |
| §15 | 4286–4408 | `HtmDiagnostics`, `SdrQualityReport`, `SystemHealthReport` | Monitoring + SDR quality analysis |
| §16 | 4409–4632 | `MultiStreamProcessor`, `StreamPipeline`, `StreamConfig` | Concurrent multi-stream via Channels |
| §17 | 4633–4786 | `HtmEngine`, `HtmEngineConfig`, `HtmResult` | Single-stream convenience orchestrator |
| §18 | 4787–5005 | `HtmExamples` | Four runnable demo patterns |

## Critical Invariants — Do Not Break

### SDR Invariants
- `_activeBits` is **always sorted and deduplicated**. Every constructor and factory enforces this. If you add a mutation path, you must maintain sort order or invalidate + rebuild.
- `_denseCache` (bitvector) is lazily computed and never exposed mutably. If you add SDR mutation, set `_denseCache = null` to invalidate.
- `Overlap()` auto-selects between sorted-merge (when both SDRs have < 64 active bits) and bitvector POPCNT. Do not remove either path.
- SDR size (`_size`) is immutable after construction. Binary operations (`Union`, `Intersect`, etc.) assert matching sizes.

### Synapse/Segment Invariants
- `CellSegmentManager` enforces `_maxSegmentsPerCell` via LRU eviction in `CreateSegment()`. Never bypass this by adding segments directly to the internal list. The `RestoreSegment()` method is **only** for deserialization — it does not enforce LRU.
- `DendriteSegment.AdaptSynapses()` and `BumpAllPermanences()` use `CollectionsMarshal.AsSpan()` for zero-copy mutation. This is intentional — the synapses list is mutated in-place for performance.
- Segment cleanup (`CellSegmentManager.Maintain()`) is called periodically by TM, not on every step. The interval is `TemporalMemoryConfig.SegmentCleanupInterval`.

### Temporal Memory State Machine
The TM maintains a two-timestep state window. The compute cycle is:
1. Save current → previous (`_prevActiveCells`, `_prevWinnerCells`, `_prevPredictiveCells`)
2. Build segment caches against **previous** active cells
3. Activate cells (predicted path vs. bursting path)
4. Compute anomaly score
5. Learn (reinforce, grow, punish) — all learning uses **previous** timestep context
6. Compute **next** predictions against **newly** active cells

**The prev/current distinction is the single most common source of bugs.** When modifying TM learning, always ask: "am I using `_prevActiveCells` or `_activeCells`?" Learning always looks backward; prediction always looks forward.

### Thousand Brains Contracts
- `CorticalColumn.Compute()` must always call `_featureSP.Compute()` then `_sequenceTM.Compute()` in that order.
- `CorticalColumn.ReceiveLateralInput()` modifies `_currentObjectRepresentation` in-place. The voting loop calls this iteratively.
- `ThousandBrainsEngine.Process()` path: grid move → column compute → lateral voting loop → recognition match → displacement prediction.
- Displacement cells compute displacements between consecutive grid locations during learning (stored per object in `_objectDisplacements`), and predict next location during recognition via `PredictTarget()`.
- `StartNewObject()` must be called before learning a new object; it resets all columns' object representations **and** displacement state (`_prevGridLocations`, `_currentDisplacements`).
- `LearnObject()` stores the current displacement sequence alongside the consensus SDR.

## Coding Conventions

### Style
- **Records for immutable data/config**: `SpatialPoolerConfig`, `HtmResult`, `SdrPrediction`, etc.
- **Sealed classes for stateful components**: `SpatialPooler`, `TemporalMemory`, `GridCellModule`, etc.
- **`[MethodImpl(AggressiveInlining)]`** on hot-path accessors (`CellIndex`, `CellColumn`, `GetBit`, `ComputeActivity`).
- **Config objects use `init` properties** with sensible defaults — consumers only override what they need.
- Metrics classes use exponential moving averages (`alpha = 0.01f`) updated via `Record*()` methods.

### Naming
- `_camelCase` for private fields, `PascalCase` for everything public.
- Cell indices are **global** (`column * CellsPerColumn + cellInColumn`), not (column, cell) tuples.
- `Compute()` is the universal method name for "process one timestep."
- `*Config` suffix for configuration records, `*Output` for compute results, `*Metrics` for diagnostic state.

### Patterns in Use
- **Dual representation** (SDR): sparse index array for iteration + dense bitvector for SIMD ops.
- **Lazy caching** (SDR `_denseCache`, TM `_activeSegmentCache` / `_matchingSegmentCache`).
- **LRU eviction** (`CellSegmentManager.CreateSegment`).
- **Channel-based producer/consumer** (`MultiStreamProcessor`).
- **Topological sort** for `Network` execution order.
- **Circular/toroidal arithmetic** in `GridCellModule` and `DisplacementCellModule`.

## How to Work With This Code

### Adding a New Encoder
1. Implement `IEncoder<T>` with `OutputSize` property and `Encode(T)` method.
2. The returned SDR must have semantically similar inputs produce high-overlap SDRs. This is the fundamental encoder contract.
3. Register via `CompositeEncoder.AddEncoder<T>(name, encoder)`.
4. Add to the section 2 region of the file, keeping encoders grouped.

### Adding a New NetworkAPI Region
1. Implement `IRegion`: define `InputPorts`, `OutputPorts`, `SetInput`, `GetOutput`, `Compute`.
2. Wrap your algorithm. The region is just a port adapter.
3. Register with `network.AddRegion(...)` and wire with `network.Link(...)`.
4. The network's topological sort will determine execution order automatically.

### Modifying the Spatial Pooler
- Overlap computation and inhibition are the hot paths. Profile before optimizing.
- `BumpWeakColumns()` calls `DendriteSegment.BumpAllPermanences()` with `0.1 * ConnectedThreshold` per BAMI. The method uses `CollectionsMarshal.AsSpan()` for zero-copy mutation, consistent with `AdaptSynapses`.
- Local inhibition creates variable total active counts (unlike global which is exactly `TargetSparsity * ColumnCount`). This is intentional and biologically motivated.

### Modifying the Temporal Memory
- **Always run `BuildSegmentCaches()` before activation/learning.** The caches map cell index → matching segments for the current timestep.
- The three learning phases must execute in order: (1) reinforce active segments, (2) grow on bursting columns, (3) punish incorrect predictions.
- `SelectBestMatchingCell()` has a two-tier fallback: best matching segment → fewest segments. Do not change this priority without understanding why TM converges.
- Segment cleanup is intentionally infrequent (default every 1000 steps). Making it per-step will tank throughput.

### Extending Thousand Brains
- Each `CorticalColumn` is independent except for lateral voting. Never share state between columns except through `ReceiveLateralInput()`.
- `GridCellModule` orientation and scale must differ across modules for unique location codes. The default config spreads orientations evenly across π.
- To add a new object modality, create a new encoder, wire it into the cortical column's input, and update `CombineFeatureAndLocation()`.

## Key Algorithms — Quick Reference

### Overlap Computation (SDR)
Two paths selected automatically:
- **Sparse** (< 64 active bits each): sorted merge, O(n+m)
- **Dense**: bitvector AND + POPCNT, O(size/64), AVX2-accelerated when available

### Spatial Pooler Inhibition
- **Global**: sort all columns by boosted overlap, take top-k. O(n log n).
- **Local**: for each column, count neighbors with higher overlap. Column wins if fewer than k neighbors beat it. O(n * radius).

### Grid Cell Path Integration
Position update: rotate movement vector by module orientation → scale by spatial period → add noise → wrap toroidally. SDR output: Gaussian bump on toroidal surface, top-k activation.

### Lateral Voting
Bit-count aggregation: each bit's vote count = number of columns with that bit active. Threshold = `VoteThreshold * numColumns`. Convergence = average pairwise `MatchScore` across all column pairs exceeds `ConvergenceThreshold`.

## Performance Considerations

- **SDR.Overlap()** is the single hottest function in the system. The SIMD path processes 256 bits per loop iteration. Do not regress this.
- **TM segment cache** (`BuildSegmentCaches`) runs O(total_segments) per timestep. For very large models (>1M segments), consider spatial partitioning.
- **`CollectionsMarshal.AsSpan()`** on `List<Synapse>` avoids copying during learning. This couples to internal `List<T>` layout — acceptable for performance-critical pseudocode but document if changed.
- **MultiStreamProcessor** uses bounded channels with `BoundedChannelFullMode.Wait` for backpressure. Worker count should be ≤ CPU cores for compute-bound HTM workloads.

## Serialization Format

Magic number: `0x48544D31` ("HTM1"), followed by version byte, type byte, then type-specific payload.
- Type `0x01`: SDR (size, activeCount, int[] bits)
- Type `0x02`: SpatialPooler (config + learned state via `SaveSpatialPooler`/`LoadSpatialPooler`)
- Type `0x03`: TemporalMemory (config + learned state via `SaveTemporalMemory`/`LoadTemporalMemory`)
- Type `0x10`: Network (region count, per-region name + type + serialized blob, link count, per-link 4 strings, FNV-1a checksum)
- Type `0x20`: HtmEngine (iteration + SP config/state + TM config/state via `SaveHtmEngine`/`LoadHtmEngineComponents`)
- Region blobs are self-describing: config properties followed by learned state. `SPRegion`/`TMRegion` provide static `CreateFromData(name, blob)` factories for reconstruction.
- `LoadNetwork()` uses a `_regionFactory` dictionary (type name → factory). Custom region types must be registered via `HtmSerializer.RegisterRegionFactory()` before loading.
- `SaveNetwork()` appends an FNV-1a checksum; `LoadNetwork()` verifies it before parsing.

## Testing Guidelines

When adding tests, validate these properties:

### SDR Tests
- Overlap(A, A) == A.ActiveCount (self-overlap)
- Overlap(A, B) == Overlap(B, A) (symmetric)
- AddNoise(0.0) returns identical SDR; AddNoise(1.0) returns zero overlap with original
- Union(A, B).ActiveCount >= max(A.ActiveCount, B.ActiveCount)
- Bitvector path and sorted-merge path produce identical overlap counts

### Encoder Tests
- Similar inputs → high overlap (e.g., ScalarEncoder(50) vs ScalarEncoder(51) should share most bits)
- Dissimilar inputs → low overlap (e.g., ScalarEncoder(0) vs ScalarEncoder(100))
- Output sparsity is within expected range
- CategoryEncoder with overlapBits=0 produces zero overlap between different categories

### SP Tests
- Output sparsity matches TargetSparsity ± tolerance after learning period
- No dead columns after sufficient boosting iterations
- Local inhibition produces spatially distributed activation

### TM Tests
- Repeated sequence → anomaly drops to near zero after learning
- Novel input after learned sequence → anomaly spikes
- Segment count stays bounded by MaxSegmentsPerCell
- Cleanup reduces synapse/segment counts

### Thousand Brains Tests
- Learning then re-sensing same object → recognition with high confidence
- Different objects produce distinguishable consensus SDRs
- Convergence iteration count decreases with more sensory observations

## Known Limitations & TODOs

- No GPU acceleration path (see Etaler project for OpenCL reference)

## Remaining Work

See `HTM_IMPLEMENTATION_TASKS.md` for the full prioritized task list. **Tiers 1, 2, and 3 implementation tasks are complete.** Only task 3.3 (hierarchical multi-region example) remains as a demo/example task. Remaining:

- **Tier 3** — One example task: hierarchical multi-region demo (3.3)
- **Tier 4** — Quality-of-life and hardening (test suite, compilable project, performance profiling, IDisposable lifecycle, logging hooks, serialization hardening)

## Reference Material

- **BAMI (Biological and Machine Intelligence)**: https://numenta.com/resources/biological-and-machine-intelligence/
- **HTM School**: https://numenta.org/htm-school/
- **Thousand Brains Theory paper**: "A Framework for Intelligence and Cortical Function Based on Grid Cells in the Neocortex" (Hawkins et al., 2019)
- **htm.core (C++ reference)**: https://github.com/htm-community/htm.core
- **NeoCortexAPI (.NET reference)**: https://github.com/ddobric/neocortexapi
