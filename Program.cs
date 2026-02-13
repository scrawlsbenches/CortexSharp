using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HierarchicalTemporalMemory.Enhanced;

// ============================================================================
// CLI dispatch:  dotnet run [example-name]
//
//   dotnet run              — list available examples
//   dotnet run all          — run all examples
//   dotnet run sdr          — SDR fundamentals
//   dotnet run monitoring   — machine monitoring with fault detection
//   dotnet run serialize    — serialization round-trip
//   dotnet run hotgym       — single-stream anomaly detection
//   dotnet run network      — Network API pipeline
//   dotnet run 1000brains   — Thousand Brains object recognition
//   dotnet run hierarchical — hierarchical temporal memory
//   dotnet run gridcells    — grid cells, displacement cells, Thousand Brains
// ============================================================================

var examples = new (string Name, string Description, Action Run)[]
{
    ("sdr",          "SDR Fundamentals — sparsity, overlap, noise, unions",     RunSdrFundamentals),
    ("monitoring",   "Machine Monitoring — 3-sensor pump with fault detection", HtmExamples.RunMachineMonitoringDemo),
    ("serialize",    "Serialization — save/load SP+TM round-trip",             RunSerializationDemo),
    ("hotgym",       "Hot Gym — single-stream anomaly detection (1000 steps)",  RunHotGym),
    ("network",      "Network API — declarative region-based pipeline",         HtmExamples.RunNetworkApiDemo),
    ("1000brains",   "Thousand Brains — multi-column object recognition",       HtmExamples.RunThousandBrainsDemo),
    ("hierarchical", "Hierarchical TM — multi-timescale sequence learning",     HtmExamples.RunHierarchicalDemo),
    ("gridcells",    "Grid Cells — path integration, displacement, 1000 Brains", HtmExamples.RunGridCellDemo),
};

string selected = args.Length > 0 ? args[0].ToLowerInvariant() : "";

if (selected == "" || selected == "help" || selected == "--help" || selected == "-h")
{
    Console.WriteLine("CortexSharp — HTM Examples");
    Console.WriteLine();
    Console.WriteLine("Usage:  dotnet run [example]");
    Console.WriteLine();
    foreach (var (name, desc, _) in examples)
        Console.WriteLine($"  {name,-14} {desc}");
    Console.WriteLine($"  {"all",-14} Run all examples");
    Console.WriteLine();
    Console.WriteLine("Example:  dotnet run sdr");
    return;
}

if (selected == "all")
{
    for (int i = 0; i < examples.Length; i++)
    {
        var (name, desc, run) = examples[i];
        Console.WriteLine($"[{i + 1}/{examples.Length}] {desc}");
        run();
        Console.WriteLine();
    }
    Console.WriteLine("=== All examples completed ===");
    return;
}

var match = examples.FirstOrDefault(e => e.Name == selected);
if (match.Run == null)
{
    Console.Error.WriteLine($"Unknown example: '{selected}'");
    Console.Error.WriteLine("Run 'dotnet run' with no arguments to see available examples.");
    Environment.Exit(1);
}

match.Run();


// ============================================================================
// Example: SDR Fundamentals
// Validates the core mathematical properties from CLAUDE.md:
//   - Sparsity: 2% active bits → enormous representational capacity
//   - Overlap: similar inputs share bits, different inputs don't
//   - Noise robustness: corrupt half the bits and still match
//   - Union: OR of multiple SDRs matches any constituent
//   - Subsampling: a fraction of bits suffices for recognition
// ============================================================================
static void RunSdrFundamentals()
{
    Console.WriteLine("SDR Fundamentals — Core Properties of Sparse Distributed Representations");
    Console.WriteLine(new string('=', 72));

    int n = 2048;  // Total bits (matches typical SP column count)
    int w = 40;    // Active bits (~2% sparsity)
    var rng = new Random(42);

    // ---- 1. Sparsity & Capacity ----
    Console.WriteLine("\n1. SPARSITY & REPRESENTATIONAL CAPACITY");
    Console.WriteLine($"   n={n} total bits, w={w} active bits, sparsity={100.0 * w / n:F1}%");

    // C(n,w) ≈ 10^84 — compute log10 of the binomial coefficient
    double logCapacity = 0;
    for (int i = 0; i < w; i++)
        logCapacity += Math.Log10((n - i) / (double)(w - i));
    Console.WriteLine($"   Unique patterns: ~10^{logCapacity:F0} (atoms in universe: ~10^80)");
    Console.WriteLine();

    // ---- 2. Random SDR collision probability ----
    Console.WriteLine("2. FALSE MATCH PROBABILITY");
    Console.WriteLine("   Two random SDRs — what's the chance they share ≥θ bits?");

    // Generate 1000 random SDR pairs and measure overlap
    int trials = 1000;
    var overlaps = new int[trials];
    for (int t = 0; t < trials; t++)
    {
        var a = MakeRandomSDR(n, w, rng);
        var b = MakeRandomSDR(n, w, rng);
        overlaps[t] = a.Overlap(b);
    }

    double avgOverlap = overlaps.Average();
    int maxOverlap = overlaps.Max();
    double expectedOverlap = (double)w * w / n;  // E[overlap] for random SDRs

    Console.WriteLine($"   Expected overlap: {expectedOverlap:F2} bits");
    Console.WriteLine($"   Measured (1000 pairs): avg={avgOverlap:F2}, max={maxOverlap}");
    Console.WriteLine($"   Pairs with overlap ≥ 10: {overlaps.Count(o => o >= 10)} / {trials}");
    Console.WriteLine($"   Pairs with overlap ≥ 13: {overlaps.Count(o => o >= 13)} / {trials} " +
                      $"(TM ActivationThreshold)");
    Console.WriteLine($"   Pairs with overlap ≥ 20: {overlaps.Count(o => o >= 20)} / {trials}");
    Console.WriteLine("   → Random SDRs almost never collide at biologically relevant thresholds.");
    Console.WriteLine();

    // ---- 3. Semantic similarity = overlap ----
    Console.WriteLine("3. SIMILARITY = OVERLAP");
    Console.WriteLine("   Encoder outputs for nearby values should share bits.");

    // Window width = activeBits * (range / size). With 400 bits, 41 active,
    // range 0-100: window ≈ 10 value units — enough to show a clear gradient.
    var encoder = new ScalarEncoder(size: 400, activeBits: 41, minValue: 0, maxValue: 100);
    var sdrs = new Dictionary<double, SDR>();
    foreach (double v in new[] { 50.0, 52.0, 55.0, 58.0, 62.0, 70.0 })
        sdrs[v] = encoder.Encode(v);

    Console.WriteLine($"   {"A",4} vs {"B",4} | {"Overlap",7} | {"Match%",7} | Relationship");
    Console.WriteLine($"   {"",-4}    {"",-4} | {"",-7} | {"",-7} |");
    PrintOverlap(sdrs, 50, 52, "Very close (Δ=2)");
    PrintOverlap(sdrs, 50, 55, "Close (Δ=5)");
    PrintOverlap(sdrs, 50, 58, "Nearby (Δ=8)");
    PrintOverlap(sdrs, 50, 62, "Further (Δ=12)");
    PrintOverlap(sdrs, 50, 70, "Far apart (Δ=20)");
    Console.WriteLine("   → Overlap decreases monotonically with semantic distance.");
    Console.WriteLine();

    // ---- 4. Noise robustness ----
    Console.WriteLine("4. NOISE ROBUSTNESS");
    Console.WriteLine("   Corrupt a fraction of bits — can we still recognize the pattern?");

    var original = MakeRandomSDR(n, w, rng);
    int theta = 13; // Recognition threshold

    Console.WriteLine($"   {"Noise%",6} | {"Overlap",7} | {"Match?",6} | Note");
    foreach (float noise in new[] { 0.0f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f })
    {
        var corrupted = original.AddNoise(noise, rng);
        int overlap = original.Overlap(corrupted);
        bool match = overlap >= theta;
        string note = noise == 0.5f ? "← 50% corruption, still matches!" : "";
        Console.WriteLine($"   {noise * 100,5:F0}% | {overlap,7} | {(match ? "YES" : "NO"),6} | {note}");
    }
    Console.WriteLine($"   → With threshold θ={theta}, tolerates ~50% bit corruption.");
    Console.WriteLine();

    // ---- 5. Union property ----
    Console.WriteLine("5. UNION PROPERTY");
    Console.WriteLine("   OR of multiple SDRs — can match any constituent.");

    var sdrA = MakeRandomSDR(n, w, rng);
    var sdrB = MakeRandomSDR(n, w, rng);
    var sdrC = MakeRandomSDR(n, w, rng);
    var sdrX = MakeRandomSDR(n, w, rng); // Not in the union
    var union = sdrA.Union(sdrB).Union(sdrC);

    Console.WriteLine($"   Union of A∪B∪C: {union.ActiveCount} active bits " +
                      $"(vs {w} per individual SDR)");
    Console.WriteLine($"   Union ∩ A: {union.Overlap(sdrA),3} bits (A has {w} bits) → MATCH");
    Console.WriteLine($"   Union ∩ B: {union.Overlap(sdrB),3} bits (B has {w} bits) → MATCH");
    Console.WriteLine($"   Union ∩ C: {union.Overlap(sdrC),3} bits (C has {w} bits) → MATCH");
    Console.WriteLine($"   Union ∩ X: {union.Overlap(sdrX),3} bits (X not in union) → " +
                      $"{(union.Overlap(sdrX) >= theta ? "FALSE POSITIVE!" : "no match")}");
    Console.WriteLine("   → A dendritic segment can store synapses for multiple patterns via union.");
    Console.WriteLine();

    // ---- 6. Subsampling ----
    Console.WriteLine("6. SUBSAMPLING");
    Console.WriteLine("   A random subset of bits still identifies the pattern.");

    var full = MakeRandomSDR(n, w, rng);
    Console.WriteLine($"   {"Sample",7} | {"Self-overlap",12} | {"vs Random",10}");
    foreach (int sampleSize in new[] { 40, 30, 20, 15, 10 })
    {
        var sub = full.Subsample(sampleSize, rng);
        var randSdr = MakeRandomSDR(n, w, rng);
        var randSub = randSdr.Subsample(sampleSize, rng);
        Console.WriteLine($"   {sampleSize,4}/{w}  | {sub.Overlap(full),12} | {sub.Overlap(randSdr),10}");
    }
    Console.WriteLine("   → Even 15 bits give strong discrimination vs random.");
    Console.WriteLine();

    // ---- 7. SDR quality analysis (using HtmDiagnostics) ----
    Console.WriteLine("7. POPULATION QUALITY ANALYSIS");
    var population = new List<SDR>();
    for (int i = 0; i < 200; i++)
        population.Add(MakeRandomSDR(n, w, rng));

    var report = HtmDiagnostics.AnalyzeSDRQuality(population);
    Console.WriteLine($"   {report.Count} random SDRs analyzed:");
    Console.WriteLine($"   Avg sparsity:          {report.AvgSparsity:P2} (target: {100.0 * w / n:F1}%)");
    Console.WriteLine($"   Sparsity std dev:      {report.SparsityStdDev:F4}");
    Console.WriteLine($"   Avg pairwise overlap:  {report.AvgPairwiseOverlap:F4} " +
                      $"(expected: {(double)w / n:F4})");
    Console.WriteLine($"   Unique bits used:      {report.UniqueBitsUsed}/{n}");
    Console.WriteLine($"   Bit entropy:           {report.BitEntropy:F2} bits");
    Console.WriteLine();

    Console.WriteLine("Summary: SDRs provide collision-free, noise-robust, similarity-preserving");
    Console.WriteLine("representations — the mathematical foundation of cortical computation.");
}

static SDR MakeRandomSDR(int size, int activeBits, Random rng)
{
    var bits = Enumerable.Range(0, size).OrderBy(_ => rng.Next()).Take(activeBits);
    return new SDR(size, bits);
}

static void PrintOverlap(Dictionary<double, SDR> sdrs, double a, double b, string label)
{
    int overlap = sdrs[a].Overlap(sdrs[b]);
    float match = sdrs[a].MatchScore(sdrs[b]);
    Console.WriteLine($"   {a,4:F0} vs {b,4:F0} | {overlap,7} | {match * 100,5:F0} % | {label}");
}


// ============================================================================
// Example: Serialization Round-Trip
// Demonstrates save/load of learned SP+TM state, proving that a trained
// model can be persisted and restored without loss of prediction accuracy.
// ============================================================================
static void RunSerializationDemo()
{
    Console.WriteLine("Serialization Round-Trip — Save and Restore Learned HTM State");
    Console.WriteLine(new string('=', 72));

    // ---- Build and train an SP+TM on a simple sequence ----
    var encoder = new ScalarEncoder(size: 400, activeBits: 21, minValue: 0, maxValue: 100);

    var spConfig = new SpatialPoolerConfig
    {
        InputSize = 400,
        ColumnCount = 1024,
        TargetSparsity = 0.02f,
    };
    var tmConfig = new TemporalMemoryConfig
    {
        ColumnCount = 1024,
        CellsPerColumn = 16,
    };

    var sp = new SpatialPooler(spConfig);
    var tm = new TemporalMemory(tmConfig);

    // Train on a 5-element repeating sequence
    double[] sequence = { 10, 30, 50, 70, 90 };
    Console.WriteLine($"\nTraining on sequence: [{string.Join(", ", sequence)}]");

    // Pre-train SP
    Console.Write("  SP pre-training (50 cycles)...");
    for (int c = 0; c < 50; c++)
        foreach (double val in sequence)
            sp.Compute(encoder.Encode(val), learn: true);
    Console.WriteLine(" done");

    // Train TM
    Console.Write("  TM sequence learning (20 cycles)...");
    for (int c = 0; c < 20; c++)
    {
        foreach (double val in sequence)
        {
            var cols = sp.Compute(encoder.Encode(val), learn: false);
            tm.Compute(cols, learn: true);
        }
    }
    Console.WriteLine(" done");

    // Measure anomaly on one more cycle (should be ~0%)
    Console.Write("  Verifying predictions...");
    float[] originalAnomalies = new float[sequence.Length];
    for (int i = 0; i < sequence.Length; i++)
    {
        var cols = sp.Compute(encoder.Encode(sequence[i]), learn: false);
        var tmOut = tm.Compute(cols, learn: false);
        originalAnomalies[i] = tmOut.Anomaly;
    }
    float originalAvg = originalAnomalies.Average();
    Console.WriteLine($" avg anomaly = {originalAvg * 100:F1}%");
    Console.WriteLine($"  Segments: {tm.TotalSegmentCount}, Synapses: {tm.TotalSynapseCount}");

    // ---- Save ----
    string spFile = Path.Combine(Path.GetTempPath(), "cortexsharp_sp.bin");
    string tmFile = Path.Combine(Path.GetTempPath(), "cortexsharp_tm.bin");

    Console.WriteLine($"\nSaving SP → {spFile}");
    HtmSerializer.SaveSpatialPooler(sp, spConfig, spFile);
    long spSize = new FileInfo(spFile).Length;
    Console.WriteLine($"  SP file: {spSize:N0} bytes");

    Console.WriteLine($"Saving TM → {tmFile}");
    HtmSerializer.SaveTemporalMemory(tm, tmConfig, tmFile);
    long tmSize = new FileInfo(tmFile).Length;
    Console.WriteLine($"  TM file: {tmSize:N0} bytes");

    // ---- Load into fresh instances ----
    Console.WriteLine("\nLoading into fresh SP+TM instances...");
    var (sp2, spConfig2) = HtmSerializer.LoadSpatialPooler(spFile);
    var (tm2, tmConfig2) = HtmSerializer.LoadTemporalMemory(tmFile);

    Console.WriteLine($"  Loaded SP: ColumnCount={spConfig2.ColumnCount}, InputSize={spConfig2.InputSize}");
    Console.WriteLine($"  Loaded TM: ColumnCount={tmConfig2.ColumnCount}, CellsPerColumn={tmConfig2.CellsPerColumn}");
    Console.WriteLine($"  Loaded TM segments: {tm2.TotalSegmentCount}, synapses: {tm2.TotalSynapseCount}");

    // ---- Verify restored model produces same predictions ----
    Console.Write("  Verifying restored predictions...");
    float[] restoredAnomalies = new float[sequence.Length];
    for (int i = 0; i < sequence.Length; i++)
    {
        var cols = sp2.Compute(encoder.Encode(sequence[i]), learn: false);
        var tmOut = tm2.Compute(cols, learn: false);
        restoredAnomalies[i] = tmOut.Anomaly;
    }
    float restoredAvg = restoredAnomalies.Average();
    Console.WriteLine($" avg anomaly = {restoredAvg * 100:F1}%");

    // ---- Compare ----
    Console.WriteLine("\nComparison:");
    Console.WriteLine($"  {"Value",6} | {"Original",9} | {"Restored",9} | Match?");
    bool allMatch = true;
    for (int i = 0; i < sequence.Length; i++)
    {
        bool match = Math.Abs(originalAnomalies[i] - restoredAnomalies[i]) < 0.01f;
        allMatch &= match;
        Console.WriteLine(
            $"  {sequence[i],6:F0} | {originalAnomalies[i] * 100,7:F1} % | " +
            $"{restoredAnomalies[i] * 100,7:F1} % | {(match ? "YES" : "NO")}");
    }
    Console.WriteLine();
    Console.WriteLine(allMatch
        ? "SUCCESS: Restored model produces identical predictions."
        : "MISMATCH: Restored model diverges from original.");

    // ---- SDR serialization ----
    Console.WriteLine("\nSDR serialization:");
    var testSdr = encoder.Encode(42.0);
    byte[] sdrBytes = HtmSerializer.SerializeSDR(testSdr);
    var roundTripped = HtmSerializer.DeserializeSDR(sdrBytes);
    bool sdrMatch = testSdr.Equals(roundTripped);
    Console.WriteLine($"  Original: {testSdr.ActiveCount} active bits, size={testSdr.Size}");
    Console.WriteLine($"  Serialized: {sdrBytes.Length} bytes");
    Console.WriteLine($"  Round-trip match: {(sdrMatch ? "YES" : "NO")}");

    // Cleanup
    File.Delete(spFile);
    File.Delete(tmFile);
}


// ============================================================================
// Example: Hot Gym — Single-stream anomaly detection with two-phase training
// Phase 1: SP pre-training stabilizes column assignments
// Phase 2: TM learns sequences on stable columns
//
// TODO: This example does not yet converge. Known issues to investigate:
//   1. DateTimeEncoder encodes hour + day-of-week + month + weekend, making
//      the effective sequence length 168 steps (weekly cycle), not 24 (daily).
//      SP pre-training below only covers 24 hours — needs a full 168-hour week.
//   2. RDSE + noise (0-5 at resolution 0.88) produces ~6 bucket variations per
//      hour. The SP must generalize across these — pre-training should include
//      noisy samples, not just clean sinusoid values.
//   3. Consider whether DateTimeEncoder is too high-dimensional for this demo.
//      A simpler hourOfDay-only encoder might converge faster and still show
//      the key HTM behaviors. The monitoring example proves the pipeline works
//      with RDSE on a fixed cycle.
// ============================================================================
static void RunHotGym()
{
    Console.WriteLine("Hot Gym — Single-Stream Anomaly Detection (Two-Phase Training)");
    Console.WriteLine(new string('=', 72));

    var encoder = new CompositeEncoder()
        .AddEncoder("value", new RandomDistributedScalarEncoder(
            size: 700, activeBits: 21, resolution: 0.88))
        .AddEncoder("timestamp", new DateTimeEncoder());

    var engine = new HtmEngine(new HtmEngineConfig
    {
        Encoder = encoder,
        ColumnCount = 2048,
        CellsPerColumn = 32,
        Sparsity = 0.02f,
    });

    var rng = new Random(42);
    var baseTime = new DateTime(2024, 1, 1);

    // TODO: Expand to 168 hours (full week) and include noisy samples
    // Phase 1: Generate one full day of training data for SP pre-training
    var trainingData = new List<Dictionary<string, object>>();
    for (int i = 0; i < 24; i++)
    {
        var ts = baseTime.AddHours(i);
        trainingData.Add(new Dictionary<string, object>
        {
            ["timestamp"] = ts,
            ["value"] = 50.0 + 30.0 * Math.Sin(2 * Math.PI * ts.Hour / 24.0),
        });
    }

    Console.Write("\n  Phase 1: SP pre-training (50 cycles x 24 hours)...");
    engine.PreTrainSP(trainingData, cycles: 50);
    Console.WriteLine(" done");

    // Phase 2: TM sequence learning (SP learn=false)
    Console.WriteLine("  Phase 2: TM sequence learning (1000 steps)\n");

    for (int i = 0; i < 1000; i++)
    {
        var timestamp = baseTime.AddHours(i);
        double demand = 50 + 30 * Math.Sin(2 * Math.PI * timestamp.Hour / 24.0)
                       + rng.NextDouble() * 5;

        var data = new Dictionary<string, object>
        {
            ["timestamp"] = timestamp,
            ["value"] = demand,
        };

        var result = engine.Compute(data, learn: true);

        if (i % 200 == 0 || i == 999)
        {
            Console.WriteLine(
                $"  [{i,4}] {timestamp:HH:mm} | val={demand,6:F1} | " +
                $"anomaly={result.TmOutput.Anomaly * 100,3:F0} % | " +
                $"burst={result.TmOutput.BurstingColumnCount}");
        }
    }
}
