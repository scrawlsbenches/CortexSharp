# ColumnPooler Implementation Plan

## Problem Statement

CortexSharp's L2/3 object layer uses segment-based Hebbian learning (identical
to TM's approach) for forming object representations. This is architecturally
wrong — Numenta's reference implementation uses a fundamentally different
algorithm called the **ColumnPooler** with dense permanence matrices, synapses
born connected, random object assignment, and emergent convergence through
competitive selection with lateral modulation.

The segment-based approach fails because:
- Synapses start at 0.21 permanence (below the 0.5 threshold), needing 3+
  reinforcements to connect. Each touch creates a new segment (different
  location = different input), so no segment is ever revisited.
- During recognition, Tier 1/2 cells never activate (no mature segments).
  The system falls through to Tier 5 (random allocation) every time.
- Evidence accumulation uses explicit intersection, which is brittle — one
  noisy bit permanently eliminates a candidate.
- Location enters at L2/3 concatenated with L4 column output, but should
  enter at L4 as a basal signal so L4's cell-level output already encodes
  "feature at location."

Additionally, the CorticalColumn has been rewritten multiple times. This plan
is designed to be thorough enough that we get it right once.

## Reference: Numenta's Architecture

From `htmresearch/algorithms/column_pooler.py` and the L2L4 experiment
framework:

```
Grid Cells ──location──→ L4 TM (basal) ──activeCells──→ L2/3 ColumnPooler
                              ↑                              │
                         sensory input                  apical feedback
                         (proximal/SP)                   (top-down)
                              ↑                              │
                              └──── L2/3 → L4 apical ────────┘
```

Key parameters (Numenta defaults):
- `cellCount = 4096`, `sdrSize = 40` (~1% sparsity)
- `initialPermanence = 0.6` (ABOVE 0.5 threshold — born connected)
- `connectedPermanence = 0.50`
- `synPermProximalInc = 0.1`, `synPermProximalDec = 0.001`
- `synPermDistalInc = 0.1`, `synPermDistalDec = 0.001`
- `sampleSizeProximal = 20`, `minThresholdProximal = 10`
- `sampleSizeDistal = 20`, `activationThresholdDistal = 13`
- `inertiaFactor = 1.0` (100% carry-forward of previous active cells)
- `numLearningPoints = 3` (each sensation presented 3 times)

---

## Component 1: TM Basal Pathway (Location → L4)

### Why

In Numenta's model, L4 is an ExtendedTM / ApicalTMPairRegion that receives
location via a **basal** input (separate from the distal/sequence pathway).
This means L4 cells learn to represent "feature X at location Y" — the
cell-level output already encodes both dimensions. The ColumnPooler then
receives this rich cell-level signal as feedforward input without needing
to see location directly.

Our TM currently has:
- **Distal** (basal dendrites): lateral context from nearby cells (sequence)
- **Apical**: top-down from higher region

We need to add:
- **External basal**: location signal from grid cells

### Design

Add a new input pathway to `TemporalMemory` that works identically to the
apical pathway but for a different signal. The mechanism is the same — cells
with basal support from the location signal are preferred during activation.

#### Config additions to TemporalMemoryConfig

```csharp
// External basal input — location signal from grid cells.
// When enabled (BasalInputSize > 0), L4 TM cells learn to associate
// features (proximal/column) with locations (basal), producing cell-level
// output that encodes "feature at location." This is how grid cell location
// signals enter the cortical column in the Thousand Brains Theory.
//
// Without basal input, L4 TM only learns temporal sequences. With basal
// input, L4 TM additionally learns feature-location associations, making
// its cell output suitable as feedforward input to a ColumnPooler.
public int BasalInputSize { get; init; } = 0;           // 0 = disabled
public int BasalActivationThreshold { get; init; } = 6;
public int BasalMinThreshold { get; init; } = 4;
public int BasalMaxSegmentsPerCell { get; init; } = 16;
public int BasalMaxSynapsesPerSegment { get; init; } = 32;
public int BasalMaxNewSynapseCount { get; init; } = 16;
public float BasalConnectedThreshold { get; init; } = 0.5f;
public float BasalInitialPermanence { get; init; } = 0.21f;
public float BasalPermanenceIncrement { get; init; } = 0.1f;
public float BasalPermanenceDecrement { get; init; } = 0.02f;
public float BasalPredictedDecrement { get; init; } = 0.005f;
public bool BasalEnabled => BasalInputSize > 0;
```

#### TM internal changes

```
New fields (mirror the apical pattern):
  _basalSegments: CellSegmentManager[]  (length = ColumnCount * CellsPerColumn)
  _basalActiveSegmentCache: Dictionary<int, List<DendriteSegment>>
  _basalMatchingSegmentCache: Dictionary<int, List<DendriteSegment>>
  _basallyDepolarizedCells: HashSet<int>
```

#### TM.Compute() signature change

```csharp
public TemporalMemoryOutput Compute(
    SDR activeColumns, bool learn = true,
    SDR? apicalInput = null, SDR? basalInput = null)
```

#### Cell activation priority (4-tier within each column)

```
When a column is active (from SP):
  Tier 1: cells with distal + basal + apical  (all three agree)
  Tier 2: cells with distal + basal            (sequence + location)
          OR     distal + apical               (sequence + top-down)
  Tier 3: cells with distal only               (sequence context)
          OR     basal only                    (location context)
  Tier 4: burst                                (no prediction)

Simplified priority for implementation:
  - Collect predicted cells (distal depolarized)
  - Among predicted: prefer those with basal AND/OR apical support
  - If no predicted cells: burst, winner = best basal match or least-used
```

NOTE: The exact priority interaction between 3 pathways is complex. A
pragmatic first implementation: basal depolarization works like apical
depolarization — it biases which cells win but doesn't activate on its own.
The priority becomes:
  predicted + basal + apical  >  predicted + (basal OR apical)
  > predicted only  >  burst with basal preference  >  burst

#### TMRegion updates

```csharp
// Add "basalIn" port when BasalEnabled
// In SetInput: convert SDR to HashSet<int> like apicalIn does
// In Compute: pass basalInput to _tm.Compute()
```

#### TemporalMemoryOutput additions

```csharp
public HashSet<int> BasallyDepolarizedCells { get; init; }
```

### Alternatives if this approach doesn't work

If adding a third dendritic zone to TM proves too complex for the cell
activation logic (combinatorial explosion of 3-way tier interactions):

**Alternative A**: Use a separate "LocationTM" that takes location as
its external/apical input and use a standard TM for sequence context.
The CorticalColumn would run two TMs in parallel and combine their outputs.

**Alternative B**: Skip the basal pathway entirely and have the ColumnPooler
receive location as a separate lateral input (as Numenta's simpler L2-only
experiments sometimes do). This is less faithful but simpler.

**Alternative C**: Create a dedicated ExtendedTemporalMemory class that
wraps TemporalMemory and adds the basal logic, without modifying the core
TM class. Keeps existing TM untouched at the cost of some code duplication.

---

## Component 2: ColumnPooler Algorithm

### Why

The L2/3 object layer needs to form stable, distinct representations for
objects and narrow them down during recognition through competitive selection.
This is a fundamentally different algorithm from TM.

### Design

New standalone class, like SpatialPooler or TemporalMemory.

#### ColumnPoolerConfig

```csharp
public sealed class ColumnPoolerConfig
{
    // Cell population
    public int CellCount { get; init; } = 4096;
    public int SdrSize { get; init; } = 40;         // Active cells per object

    // Proximal: feedforward from L4 (cell-level TM output)
    public int FeedforwardInputSize { get; init; }   // = L4 ColumnCount * CellsPerColumn
    public int SampleSizeProximal { get; init; } = 20;
    public int MinThresholdProximal { get; init; } = 10;
    public float ConnectedPermanenceProximal { get; init; } = 0.50f;
    public float InitialProximalPermanence { get; init; } = 0.6f;   // Born connected
    public float SynPermProximalInc { get; init; } = 0.1f;
    public float SynPermProximalDec { get; init; } = 0.001f;

    // Internal distal: recurrent within L2/3 (temporal continuity)
    public int SampleSizeDistal { get; init; } = 20;
    public int ActivationThresholdDistal { get; init; } = 13;
    public float ConnectedPermanenceDistal { get; init; } = 0.50f;
    public float InitialDistalPermanence { get; init; } = 0.6f;     // Born connected
    public float SynPermDistalInc { get; init; } = 0.1f;
    public float SynPermDistalDec { get; init; } = 0.001f;

    // Lateral distal: from other columns' L2/3 layers
    public int NumOtherColumns { get; init; } = 0;   // Set by ThousandBrainsEngine

    // Inertia: fraction of previously active cells carried forward
    public float InertiaFactor { get; init; } = 1.0f;

    public int Seed { get; init; } = 42;
}
```

#### Data structures

```
Fields:
  _proximalPermanences: float[cellCount, feedforwardInputSize]
     Dense matrix. Row i = cell i's proximal synapses to feedforward input.
     Implementation: use a flat float[] with row-major indexing, or a
     dedicated DenseMatrix class for clarity.

  _internalDistalPermanences: float[cellCount, cellCount]
     Dense matrix. Row i = cell i's connections to other L2/3 cells.
     Used for temporal continuity (if cell j was active last step and
     has a connection to cell i, cell i gets distal support).

  _lateralDistalPermanences: float[numOtherColumns][cellCount, cellCount]
     One matrix per other column. Row i of matrix k = cell i's connections
     to column k's L2/3 cells.

  _activeCells: HashSet<int>
  _prevActiveCells: HashSet<int>
  _learningMode: bool   // True during learning, changes cell selection
```

NOTE: For large cellCount (4096), a dense [4096 x 4096] internal distal
matrix is 64MB of floats. This may need to be sparse in practice. Options:
- Use Dictionary<int, float> per row (sparse row)
- Use the existing DendriteSegment approach but with born-connected permanences
- Use a threshold to only store non-zero entries

For the first implementation, use sparse representation (Dictionary) with
Numenta's learning rule. We can optimize later.

#### ColumnPooler.Compute()

```
public ColumnPoolerOutput Compute(
    HashSet<int> feedforwardInput,           // L4 TM active cells
    HashSet<int> feedforwardGrowthCandidates, // L4 TM predicted-active cells
    IReadOnlyList<HashSet<int>> lateralInputs, // Other columns' active L2/3 cells
    bool learn)
```

##### Inference mode pseudocode (core algorithm)

```
function ComputeInference(feedforwardInput, lateralInputs):

    // Step 1: Compute feedforward support for each cell
    feedforwardSupportedCells = {}
    for each cell in [0, cellCount):
        overlap = CountConnectedOverlap(
            _proximalPermanences[cell], feedforwardInput, connectedPermanenceProximal)
        if overlap >= minThresholdProximal:
            feedforwardSupportedCells[cell] = overlap

    // Step 2: Compute lateral + internal distal support
    //   For each cell, count how many lateral sources have active segments
    numActiveLateralSegments = new int[cellCount]
    for each cell in [0, cellCount):
        // Internal distal: check recurrent connections to previous active cells
        internalOverlap = CountConnectedOverlap(
            _internalDistalPermanences[cell], _prevActiveCells, connectedPermanenceDistal)
        if internalOverlap >= activationThresholdDistal:
            numActiveLateralSegments[cell] += 1

        // Lateral distal: check connections to each other column's active cells
        for k in [0, numOtherColumns):
            lateralOverlap = CountConnectedOverlap(
                _lateralDistalPermanences[k][cell], lateralInputs[k], connectedPermanenceDistal)
            if lateralOverlap >= activationThresholdDistal:
                numActiveLateralSegments[cell] += 1

    // Step 3: Select active cells via competitive priority
    newActiveCells = {}
    remaining = sdrSize

    // Priority 1: Feedforward + lateral support (strongest evidence)
    //   Cells that match the current input AND are supported by context
    //   (other columns agree, or temporal continuity from previous step).
    //   Sorted by lateral segment count (descending), then feedforward overlap.
    candidatesP1 = feedforwardSupportedCells
        .Where(cell => numActiveLateralSegments[cell] > 0)
        .OrderByDescending(cell => numActiveLateralSegments[cell])
        .ThenByDescending(cell => feedforwardSupportedCells[cell])
    newActiveCells.AddRange(candidatesP1.Take(remaining))
    remaining = sdrSize - newActiveCells.Count

    // Priority 2: Inertia — previously active cells carry forward
    //   This provides temporal continuity: once cells are activated for an
    //   object, they tend to stay active as long as evidence doesn't
    //   contradict them. InertiaFactor controls what fraction can be
    //   carried (1.0 = all previous cells eligible, 0.0 = no inertia).
    if remaining > 0 and inertiaFactor > 0:
        maxInertia = (int)(sdrSize * inertiaFactor)
        inertiaCandidates = _prevActiveCells
            .Where(cell => !newActiveCells.Contains(cell))
            .Where(cell => numActiveLateralSegments[cell] > 0  // prefer laterally supported
                        || feedforwardSupportedCells.ContainsKey(cell))
            .OrderByDescending(cell => numActiveLateralSegments[cell])
        newActiveCells.AddRange(inertiaCandidates.Take(Min(remaining, maxInertia)))
        remaining = sdrSize - newActiveCells.Count

    // Priority 3: Feedforward-only (no lateral support yet)
    //   On the first touch, no lateral context exists, so all feedforward-
    //   supported cells compete equally. This is the "initial ambiguity"
    //   state that narrows on subsequent touches.
    if remaining > 0:
        candidatesP3 = feedforwardSupportedCells.Keys
            .Where(cell => !newActiveCells.Contains(cell))
            .OrderByDescending(cell => feedforwardSupportedCells[cell])
        newActiveCells.AddRange(candidatesP3.Take(remaining))
        remaining = sdrSize - newActiveCells.Count

    // Priority 4: Fill any remaining slots with least-used cells
    //   (should rarely happen after learning)
    if remaining > 0:
        fillCells = Enumerable.Range(0, cellCount)
            .Where(cell => !newActiveCells.Contains(cell))
            .OrderBy(_ => rng.Next())
            .Take(remaining)
        newActiveCells.AddRange(fillCells)

    _prevActiveCells = _activeCells
    _activeCells = newActiveCells
    return _activeCells
```

##### Learning mode pseudocode

```
function ComputeLearning(feedforwardInput, feedforwardGrowthCandidates, lateralInputs):

    // In learning mode, if we have an existing representation, keep it.
    // Otherwise, randomly assign one.
    if _activeCells.Count < sdrSize:
        // New object — random draw
        _activeCells = RandomSample([0, cellCount), sdrSize)

    // The active cells are the "object representation" — they stay fixed
    // for the duration of learning this object. Each new sensation teaches
    // them new proximal connections.

    // Proximal learning: connect active cells to current feedforward input
    for each cell in _activeCells:
        // Reinforce existing synapses that match growth candidates
        for each presynaptic in feedforwardGrowthCandidates:
            if _proximalPermanences[cell, presynaptic] > 0:
                _proximalPermanences[cell, presynaptic] += synPermProximalInc
            // Clip to [0, 1]

        // Weaken synapses to inactive inputs
        for each presynaptic in AllProximalSynapses(cell):
            if presynaptic not in feedforwardInput:
                _proximalPermanences[cell, presynaptic] -= synPermProximalDec
            // Clip to [0, 1]

        // Grow new synapses to uncovered growth candidates
        uncovered = feedforwardGrowthCandidates
            .Where(pre => _proximalPermanences[cell, pre] == 0)
        sampled = RandomSample(uncovered, sampleSizeProximal)
        for each pre in sampled:
            _proximalPermanences[cell, pre] = initialProximalPermanence

    // Internal distal learning: connect active cells to previous active cells
    //   This builds temporal continuity — "if these cells were active last
    //   step, I should be active this step."
    for each cell in _activeCells:
        for each prevCell in _prevActiveCells:
            // Same reinforce/weaken/grow pattern as proximal
            ...grow with initialDistalPermanence, sample sampleSizeDistal...

    // Lateral distal learning: connect to other columns' active cells
    for k in [0, numOtherColumns):
        for each cell in _activeCells:
            for each otherCell in lateralInputs[k]:
                ...same pattern...

    _prevActiveCells = _activeCells
```

#### ColumnPoolerOutput

```csharp
public record ColumnPoolerOutput
{
    public required HashSet<int> ActiveCells { get; init; }
    public int FeedforwardSupportedCount { get; init; }
    public int LateralSupportedCount { get; init; }
}
```

#### StartNewObject()

```csharp
public void StartNewObject()
{
    _activeCells.Clear();
    _prevActiveCells.Clear();
}
```

### Alternatives if dense matrices are too expensive

If 4096 x (L4 cell count) is too large in practice:

**Alternative A**: Sparse row representation — each cell stores only non-zero
permanences as `Dictionary<int, float>`. This is memory-efficient but slower
for the `CountConnectedOverlap` scan. Good enough for the initial implementation.

**Alternative B**: Segment-based BUT with `initialPermanence = 0.6` (born
connected). This reuses our existing DendriteSegment infrastructure but with
the critical fix that new synapses start above threshold. Less faithful to
Numenta but minimizes new code. Each cell still has discrete segments, but
segments are immediately functional on creation.

**Alternative C**: Hybrid — use dense matrices for proximal (most critical for
recognition) and segment-based for distal/lateral (where sparsity is natural).

---

## Component 3: CorticalColumn Rewrite

### Correct data flow

```
Inputs:
  sensoryInput SDR (raw features)
  locationInput SDR (from grid cells)
  apicalInput SDR (from higher region or consensus) [optional]
  lateralInputs List<HashSet<int>> (from other columns' L2/3) [set by voting]

Internal flow:
  1. L4 SP: sensoryInput → l4ActiveColumns
  2. L4 TM: l4ActiveColumns + locationInput (basal) + l23Feedback (apical) → tmOutput
     - tmOutput.ActiveCells encodes "feature at location in sequence context"
     - tmOutput.PredictedActiveCells = cells that were predicted AND activated
  3. L2/3 ColumnPooler: tmOutput.ActiveCells (proximal)
                        + lateralInputs (lateral distal)
                        + prevL23Active (internal distal/inertia)
                        → newActiveCells = object representation
  4. L2/3 → L4 feedback: newActiveCells → stored for next Compute()'s
     L4 TM apical input (with one-step delay)

Output:
  CorticalColumnOutput with ObjectRepresentation = ColumnPooler active cells
```

### New CorticalColumnConfig

```csharp
public sealed class CorticalColumnConfig
{
    // L4 Spatial Pooler
    public int InputSize { get; init; } = 512;
    public int L4ColumnCount { get; init; } = 1024;
    public int CellsPerColumn { get; init; } = 16;

    // L4 TM basal input (location from grid cells)
    public int LocationSize { get; init; } = 1600;

    // L2/3 ColumnPooler
    public int L23CellCount { get; init; } = 4096;   // was ObjectRepresentationSize
    public int L23SdrSize { get; init; } = 40;        // was ObjectActiveBits
    public int L23SampleSizeProximal { get; init; } = 20;
    public int L23MinThresholdProximal { get; init; } = 10;
    public float L23ConnectedPermanence { get; init; } = 0.50f;
    public float L23InitialPermanence { get; init; } = 0.6f;  // Born connected!
    public float L23PermanenceIncrement { get; init; } = 0.1f;
    // Numenta uses 0.001, 20x less aggressive than our old 0.02.
    // Biological rationale: LTD (long-term depression) is a slower process
    // than LTP (long-term potentiation). Aggressive decrement causes learned
    // associations to erode before they can be reinforced across exposures.
    public float L23PermanenceDecrement { get; init; } = 0.001f;
    public int L23SampleSizeDistal { get; init; } = 20;
    public int L23ActivationThresholdDistal { get; init; } = 13;
    public float L23InitialDistalPermanence { get; init; } = 0.6f;
    public float L23DistalPermanenceIncrement { get; init; } = 0.1f;
    public float L23DistalPermanenceDecrement { get; init; } = 0.001f;
    public float L23InertiaFactor { get; init; } = 1.0f;

    // Apical: top-down from higher region (fed to ColumnPooler or L4 TM)
    public int ApicalInputSize { get; init; } = 0;   // 0 = disabled

    // Backward compatibility
    public int ColumnCount { get => L4ColumnCount; init => L4ColumnCount = value; }
}
```

### New CorticalColumn fields

```csharp
private readonly CorticalColumnConfig _config;
private readonly SpatialPooler _l4SP;
private readonly TemporalMemory _l4TM;       // was _sequenceTM
private readonly ColumnPooler _l23;           // replaces _l23Cells + _l23ApicalCells
private readonly Random _rng;

// L2/3 → L4 feedback (one-step delay)
private SDR? _l23FeedbackForL4;

// Lateral inputs from other columns (set by ReceiveLateralInput or voting)
private List<HashSet<int>> _lateralInputs = new();

// Current state
private SDR _currentObjectRepresentation;
```

### New CorticalColumn constructor

```csharp
public CorticalColumn(CorticalColumnConfig config, int numOtherColumns, int seed = 42)
{
    _config = config;
    _rng = new Random(seed);

    // L4 SP: sensory input only
    _l4SP = new SpatialPooler(new SpatialPoolerConfig
    {
        InputSize = config.InputSize,
        ColumnCount = config.L4ColumnCount,
        TargetSparsity = 0.02f,
    });

    // L4 TM: with basal input for location, apical for L2/3 feedback
    _l4TM = new TemporalMemory(new TemporalMemoryConfig
    {
        ColumnCount = config.L4ColumnCount,
        CellsPerColumn = config.CellsPerColumn,
        // Enable basal pathway for grid cell location input
        BasalInputSize = config.LocationSize,
        // Enable apical for L2/3 → L4 top-down feedback
        ApicalInputSize = config.L23CellCount,
    });

    // L2/3 ColumnPooler: receives L4 cell-level output as feedforward
    _l23 = new ColumnPooler(new ColumnPoolerConfig
    {
        CellCount = config.L23CellCount,
        SdrSize = config.L23SdrSize,
        FeedforwardInputSize = config.L4ColumnCount * config.CellsPerColumn,
        SampleSizeProximal = config.L23SampleSizeProximal,
        MinThresholdProximal = config.L23MinThresholdProximal,
        ConnectedPermanenceProximal = config.L23ConnectedPermanence,
        InitialProximalPermanence = config.L23InitialPermanence,
        SynPermProximalInc = config.L23PermanenceIncrement,
        SynPermProximalDec = config.L23PermanenceDecrement,
        SampleSizeDistal = config.L23SampleSizeDistal,
        ActivationThresholdDistal = config.L23ActivationThresholdDistal,
        InitialDistalPermanence = config.L23InitialDistalPermanence,
        SynPermDistalInc = config.L23DistalPermanenceIncrement,
        SynPermDistalDec = config.L23DistalPermanenceDecrement,
        InertiaFactor = config.L23InertiaFactor,
        NumOtherColumns = numOtherColumns,
        Seed = seed + 1000,
    });

    _currentObjectRepresentation = new SDR(config.L23CellCount);
}
```

### New CorticalColumn.Compute()

```csharp
public CorticalColumnOutput Compute(
    SDR sensoryInput, SDR locationInput,
    bool learn = true, SDR? apicalInput = null)
{
    // 1. L4 SP: feature extraction
    var l4ActiveColumns = _l4SP.Compute(sensoryInput, learn);

    // 2. L4 TM: sequence + location context
    //    - basalInput = location from grid cells
    //    - apicalInput = L2/3 feedback from previous step (one-step delay)
    var tmOutput = _l4TM.Compute(
        l4ActiveColumns, learn,
        apicalInput: _l23FeedbackForL4,
        basalInput: locationInput);

    // 3. L2/3 ColumnPooler: object representation
    //    - feedforward = L4 TM active cells (encodes feature+location+sequence)
    //    - feedforwardGrowthCandidates = L4 TM predicted-active cells
    //    - lateral = other columns' L2/3 active cells (set by voting)
    var growthCandidates = tmOutput.ActiveCells
        .Where(c => tmOutput.PredictiveCells.Contains(c))  // predicted AND active
        .ToHashSet();
    // If no cells were predicted (burst), use all active as growth candidates
    if (growthCandidates.Count == 0)
        growthCandidates = tmOutput.ActiveCells;

    var cpOutput = _l23.Compute(
        tmOutput.ActiveCells,
        growthCandidates,
        _lateralInputs,
        learn);

    // 4. Store L2/3 output as feedback for next L4 TM step
    _l23FeedbackForL4 = new SDR(
        _config.L23CellCount, cpOutput.ActiveCells);

    // 5. Update object representation
    _currentObjectRepresentation = new SDR(
        _config.L23CellCount, cpOutput.ActiveCells);

    return new CorticalColumnOutput
    {
        ActiveCells = tmOutput.ActiveCells,
        PredictedCells = tmOutput.PredictiveCells,
        ObjectRepresentation = _currentObjectRepresentation,
        Anomaly = tmOutput.Anomaly,
        Confidence = 1.0f - tmOutput.Anomaly,
    };
}
```

### ReceiveLateralInput changes

```csharp
// Old: explicit intersection of SDRs (brittle)
// New: store other columns' active cells for ColumnPooler's lateral pathway
public void SetLateralInputs(IReadOnlyList<HashSet<int>> otherColumnActiveCells)
{
    _lateralInputs = otherColumnActiveCells.ToList();
}

// Keep ReceiveLateralInput for backward compat with voting mechanism,
// but it now just stores the consensus for the ColumnPooler to use as context
public void ReceiveLateralInput(SDR consensusRepresentation)
{
    // The ColumnPooler handles convergence emergently through lateral
    // connections — no explicit intersection needed. But we can still
    // use the consensus as a lightweight signal.
    // (Detailed interaction TBD during implementation)
}
```

### Methods removed

```
- CombineL4AndLocation()        — location now goes to L4 TM basal, not L2/3
- GrowL23Synapses()             — ColumnPooler handles its own learning
- GrowApicalSynapses()          — ColumnPooler handles its own learning
- AccumulateObjectEvidence()    — ColumnPooler converges emergently
```

### Methods unchanged

```
- ResetObjectRepresentation()   — delegates to ColumnPooler.StartNewObject()
- GetObjectRepresentation()     — returns _currentObjectRepresentation (same)
```

---

## Component 4: ThousandBrainsEngine Updates

### Config propagation

Update `adjustedColumnConfig` construction in the constructor to pass the
new ColumnPooler parameters instead of the old L23* segment parameters.

### Constructor change

```csharp
// Pass numOtherColumns to each CorticalColumn so it knows how many
// lateral distal matrices to allocate
_columns[i] = new CorticalColumn(
    adjustedColumnConfig,
    numOtherColumns: config.ColumnCount - 1,
    seed: 100 + i);
```

### Lateral voting integration

The voting mechanism currently:
1. Gets each column's object representation via `GetObjectRepresentation()`
2. Computes consensus via `ComputeConsensus()`
3. Feeds consensus back via `ReceiveLateralInput()`

With the ColumnPooler, lateral input should be **per-column active cells**,
not a consensus SDR. Each column sends its L2/3 active cells to every other
column's lateral distal pathway.

```csharp
// In Process(), after computing all column outputs:
// Set lateral inputs: each column receives all OTHER columns' L2/3 cells
for (int i = 0; i < _config.ColumnCount; i++)
{
    var lateralInputs = new List<HashSet<int>>();
    for (int j = 0; j < _config.ColumnCount; j++)
    {
        if (j != i)
            lateralInputs.Add(
                columnOutputs[j].ObjectRepresentation
                    .ActiveBits.ToArray().ToHashSet());
    }
    _columns[i].SetLateralInputs(lateralInputs);
}
```

The existing `LateralVotingMechanism` is still useful for computing the
consensus SDR (for object library matching, recognition, and hierarchical
feedback), but it no longer drives convergence — that's now emergent from
the ColumnPooler's lateral connections.

### Settling loop fix

```csharp
public ThousandBrainsOutput Process(
    SDR[] sensoryPatches, float moveDeltaX, float moveDeltaY,
    bool learn = true, SDR? hierarchicalFeedback = null)
{
    _explorationSteps++;

    // Grid cells move ONCE per physical touch
    for (int i = 0; i < _config.ColumnCount; i++)
        foreach (var module in _gridModules[i])
            module.Move(moveDeltaX, moveDeltaY);

    // Column computation (may be called multiple times during settling,
    // but grid cells hold their position)
    var columnOutputs = ComputeColumns(sensoryPatches, learn, hierarchicalFeedback);

    // ... voting, recognition, displacement ...
}

// Extracted method — called during settling without moving grid cells
private CorticalColumnOutput[] ComputeColumns(
    SDR[] sensoryPatches, bool learn, SDR? hierarchicalFeedback)
{
    SDR? apicalSignal = hierarchicalFeedback ?? _prevConsensus;
    var outputs = new CorticalColumnOutput[_config.ColumnCount];
    for (int i = 0; i < _config.ColumnCount; i++)
    {
        var locationSDR = GetCompositeLocation(i);
        outputs[i] = _columns[i].Compute(
            sensoryPatches.Length == _config.ColumnCount
                ? sensoryPatches[i]
                : sensoryPatches[i % sensoryPatches.Length],
            locationSDR, learn, apicalInput: apicalSignal);
    }
    return outputs;
}
```

### HierarchicalThousandBrainsEngine

No changes needed — it calls `ThousandBrainsEngine.Process()` which
handles the new internals transparently. But the settling loop bug fix
(separating Move from Compute) must be applied here too if the hierarchical
engine calls Process() multiple times per physical touch.

---

## Component 5: Demo Rewrite

### Design principles for new demos

1. **Objects MUST share features** — e.g., "cup" and "glass" both have "rim"
   and "base" at different locations. Recognition MUST use location to
   disambiguate. If the demo passes without location, the implementation
   is broken.

2. **Each column MUST see a different feature** — no broadcasting the same
   patch to all columns. If demos broadcast, the multi-column architecture
   is untested.

3. **Recognition MUST require multiple touches** — single-touch recognition
   means the system isn't narrowing candidates. The demo should show the
   candidate set shrinking across touches.

4. **Results MUST be asserted** — not printed with "Expected:" comments.
   Use actual assertions that fail loudly if the implementation is wrong.

5. **Multi-pass training** — each sensation presented 3 times (matching
   Numenta's numLearningPoints=3).

### Demo: Object Discrimination Test

```csharp
/// Validates that the ColumnPooler can form distinct representations for
/// objects that share features. This demo is designed to FAIL if:
///   - L2/3 doesn't form distinct per-object representations
///   - Location information doesn't reach L4 (all feature-at-location combos look the same)
///   - Lateral voting doesn't narrow candidates across touches
///   - Multi-pass training is broken
///
/// Objects:
///   cup:   {rim@(0,0), handle@(1,0), base@(2,0), ceramic@(3,0)}
///   glass: {rim@(0,0), stem@(1,0),   base@(2,0), crystal@(3,0)}
///   bowl:  {rim@(0,0), curve@(1,0),  base@(2,0), ceramic@(3,0)}
///
/// Note: "rim" and "base" appear in ALL objects. "ceramic" appears in cup
/// AND bowl. Recognition requires location-aware feature discrimination.
/// After 2-3 touches, the system should converge to the correct object.
public static void RunColumnPoolerDemo()
{
    // ...setup...

    // ASSERTION: After learning, each object has a distinct representation
    //   (pairwise overlap between object SDRs < 20%)
    // ASSERTION: Recognition converges within 3 touches for each object
    // ASSERTION: Recognized object matches the ground truth
}
```

### Existing Thousand Brains demos

The three existing demos (`1000brains`, `gridcells` section 5, `hier1000b`)
should be:
- **Annotated with comments** explaining that they exercise the legacy
  segment-based L2/3 approach and may not produce correct object
  discrimination results
- **Retained for now** as reference for the old architecture
- **Considered for retirement** during the post-implementation cleanup
  (discussed as point 8 in the user's feedback)

Example comment to add:

```csharp
/// NOTE: This demo was written for the segment-based L2/3 implementation
/// (prior to the ColumnPooler rewrite). It broadcasts identical sensory
/// patches to all columns and uses single-pass training, which does not
/// exercise the multi-column voting or location-dependent discrimination
/// that the ColumnPooler is designed for. Retained as a reference for the
/// old architecture — see RunColumnPoolerDemo() for the proper test.
```

---

## Component 6: Cleanup (post-implementation)

Deferred until after the implementation is working. Items to address:

1. Remove dead L23* config fields if no code references them
2. Remove `CombineL4AndLocation`, `GrowL23Synapses`, `GrowApicalSynapses`,
   `AccumulateObjectEvidence` once CorticalColumn no longer uses them
3. Evaluate whether `_l23ApicalCells` segment managers should be removed
   entirely (ColumnPooler may handle apical internally)
4. Consider whether `LateralVotingMechanism.RunVotingLoop()` needs updating
   (it currently calls `ReceiveLateralInput` which may become a no-op)
5. Evaluate whether old demos should be retired or updated
6. Address TM serialization gaps (missing apical/basal config fields)
7. Address BoostStrength inconsistency between SpatialPoolerConfig and
   HtmEngineConfig

---

## Implementation Order

```
Phase 1: TM Basal Pathway
  - Add BasalInputSize + basal config fields to TemporalMemoryConfig
  - Add basal segment arrays, caches, depolarization tracking to TM
  - Integrate into TM.Compute() cell activation priority
  - Update TMRegion to expose "basalIn" port
  - Update TM serialization for new fields
  - Test: existing TM demos still pass (basal disabled by default)

Phase 2: ColumnPooler Algorithm
  - New ColumnPoolerConfig class
  - New ColumnPooler class with Compute(), StartNewObject()
  - Sparse row representation for permanence matrices
  - Learning mode (random assignment, proximal/distal/lateral learning)
  - Inference mode (competitive selection with lateral modulation + inertia)
  - Unit-level validation: learn 2 objects, verify distinct representations

Phase 3: CorticalColumn Rewrite
  - New CorticalColumnConfig (replace L23* with ColumnPooler params)
  - New constructor (SP + TM-with-basal + ColumnPooler)
  - New Compute() (correct data flow: SP→TM→ColumnPooler, with feedback)
  - SetLateralInputs() for per-column lateral routing
  - Remove dead methods (CombineL4AndLocation, GrowL23Synapses, etc.)
  - Test: builds, basic smoke test

Phase 4: ThousandBrainsEngine Integration
  - Update adjustedColumnConfig construction
  - Pass numOtherColumns to CorticalColumn
  - Route per-column lateral inputs (not just consensus)
  - Fix settling loop (separate Move from Compute)
  - Test: existing engine API still works

Phase 5: Demos
  - New RunColumnPoolerDemo() with assertions
  - Annotate old demos with explanatory comments
  - Verify all 10+ unaffected demos still pass

Phase 6: Cleanup (deferred, discussed with user)
```

---

## Open Questions

1. **Sparse vs. dense matrices**: The initial implementation should use sparse
   rows (Dictionary<int, float>) for all permanence matrices. If performance
   is an issue, we can explore dense arrays for proximal (which has the most
   active synapses) and keep sparse for distal/lateral.

2. **Apical in ColumnPooler**: Numenta's ColumnPooler doesn't have explicit
   apical support — higher-region feedback enters via the lateral pathway or
   is treated as an additional lateral source. We should follow this for now
   and add apical as a distinct pathway later if needed.

3. **L2/3 → L4 feedback delay**: The feedback from L2/3 to L4's apical
   dendrites should have a one-step propagation delay (as in Numenta's model).
   This means the L2/3 output from step N is used as L4's apical input at
   step N+1. This is naturally achieved by storing the feedback in
   `_l23FeedbackForL4` and using it on the next Compute() call.

4. **Growth candidates**: Numenta uses `predictedActiveCells` (cells that were
   both predicted and activated) as growth candidates for proximal learning.
   This means learning is selective — it connects to context-specific cell
   patterns. When no cells are predicted (burst), we fall back to all active
   cells as candidates. This distinction is important for forming
   location-specific representations.
