using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HierarchicalTemporalMemory.Enhanced;

Console.WriteLine("=== CortexSharp — Running Examples ===");
Console.WriteLine();

Console.WriteLine("[1/5] Single-Stream Anomaly Detection (Hot Gym) — 1000 steps");
RunSingleStreamSmall();
Console.WriteLine();

Console.WriteLine("[2/5] Network API Pipeline");
HtmExamples.RunNetworkApiDemo();
Console.WriteLine();

Console.WriteLine("[3/5] Thousand Brains Object Recognition");
HtmExamples.RunThousandBrainsDemo();
Console.WriteLine();

Console.WriteLine("[4/5] Hierarchical Temporal Memory");
HtmExamples.RunHierarchicalDemo();
Console.WriteLine();

Console.WriteLine("[5/5] Machine Monitoring with Fault Detection");
HtmExamples.RunMachineMonitoringDemo();
Console.WriteLine();

Console.WriteLine("=== All examples completed ===");

static void RunSingleStreamSmall()
{
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
                $"  [{i,4}] {timestamp:HH:mm} | val={demand,6:F1} | anomaly={result.TmOutput.Anomaly * 100,3:F0} % | burst={result.TmOutput.BurstingColumnCount}");
        }
    }
}
