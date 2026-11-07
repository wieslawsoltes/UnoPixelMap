using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PixelMap.Data;
using PixelMap.Simulation;
using PixelMap.Simulation.Tests.Reference;

internal static partial class CollisionBroadPhaseChecks
{
    private sealed record CostSample(string Scene, string Variant, int Block, int Sample,
        double Milliseconds, long AllocatedBytes, int Contacts);

    internal static void RunCost(string[] args)
    {
        Run();
        string output = Path.GetFullPath(args.FirstOrDefault(a => a.StartsWith("--collision-output=", StringComparison.Ordinal))?
            ["--collision-output=".Length..] ?? Path.Combine(Path.GetTempPath(), "PixelMap.CollisionCost"));
        Directory.CreateDirectory(output);
        var samples = new List<CostSample>();
        var summaries = new List<object>();
        var tile = new TileId(14, 8192, 8192);
        var features = Features();
        var clear = Enumerable.Range(0, 512).Select(i =>
        {
            var at = At(tile, i % 32 * 128 + 112, i / 32 * 128 + 112);
            return new Query(VehicleProfile.Walker, at, at, 0, 1.7);
        }).ToArray();
        var walking = Queries(tile, 512).Where(q => q.Vehicle == VehicleProfile.Walker && q.FromAltitude == 0).ToArray();
        var flying = Queries(tile, 512).Where(q => q.Vehicle == VehicleProfile.FreeCamera)
            .Select(q => q with { FromAltitude = 20, ToAltitude = 25 }).ToArray();
        var coast = Enumerable.Range(0, 2048).Select(i =>
        {
            double angle = i * Math.Tau / 2048;
            double radius = 1850 + 125 * Math.Sin(angle * 19);
            return new TilePoint((int)(2048 + Math.Cos(angle) * radius), (int)(2048 + Math.Sin(angle) * radius));
        }).ToArray();
        var scenarios = new[]
        {
            (Name: "dense-short-walk", Features: features, Queries: walking),
            (Name: "dense-clear-body", Features: features, Queries: clear),
            (Name: "complex-water-flight", Features: features.Append(Polygon("water", coast)).ToArray(), Queries: flying)
        };
        const int repeats = 16;
        foreach (var scenario in scenarios)
        {
            var candidate = new VectorCollisionWorld();
            var reference = new LegacyVectorCollisionWorld();
            var tiles = new[] { new MapTile(tile, scenario.Features) };
            candidate.Update(tiles);
            reference.Update(tiles);
            foreach (var query in scenario.Queries) Equivalent(candidate, reference, query, scenario.Name);
            Func<Query, bool> original = q => reference.Blocks(q.Vehicle, q.From, q.To, q.FromAltitude, q.ToAltitude);
            Func<Query, bool> improved = q => candidate.Blocks(q.Vehicle, q.From, q.To, q.FromAltitude, q.ToAltitude);
            for (int warm = 0; warm < 32; warm++)
            {
                Batch(original, scenario.Queries, repeats);
                Batch(improved, scenario.Queries, repeats);
            }
            for (int block = 0; block < 4; block++)
            {
                bool optimized = block is 1 or 2;
                var execute = optimized ? improved : original;
                for (int sample = 0; sample < 8; sample++)
                {
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    int contacts = Batch(execute, scenario.Queries, repeats);
                    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    samples.Add(new(scenario.Name, optimized ? "bounded" : "reference", block, sample, elapsed, bytes, contacts));
                }
            }
            var before = samples.Where(s => s.Scene == scenario.Name && s.Variant == "reference").ToArray();
            var after = samples.Where(s => s.Scene == scenario.Name && s.Variant == "bounded").ToArray();
            double baseline = Median(before.Select(s => s.Milliseconds)), bounded = Median(after.Select(s => s.Milliseconds));
            summaries.Add(new
            {
                Scene = scenario.Name, QueriesPerBatch = scenario.Queries.Length * repeats,
                ReferenceMedianMs = baseline, BoundedMedianMs = bounded, ChangePercent = (bounded / baseline - 1) * 100,
                ReferenceBytesPerBatch = Median(before.Select(s => (double)s.AllocatedBytes)),
                BoundedBytesPerBatch = Median(after.Select(s => (double)s.AllocatedBytes))
            });
            Console.WriteLine($"Collision {scenario.Name}: reference {baseline:F3} ms, bounded {bounded:F3} ms " +
                $"({(bounded / baseline - 1) * 100:+0.0;-0.0;0.0}%) for {scenario.Queries.Length * repeats:N0} sweeps.");
        }
        File.WriteAllText(Path.Combine(output, "collision-broad-phase-cost.json"), JsonSerializer.Serialize(new
        {
            Method = "Synthetic dense city and complex shoreline fixtures. Frozen c937ddd collision implementation and " +
                "candidate run in the same process over identical precomputed queries; reference/bounded/bounded/reference " +
                "blocks each measure eight batches after paired warmup. Index creation and query generation excluded. " +
                "Times are batch medians, not browser frame-rate claims.",
            Runtime = RuntimeInformation.FrameworkDescription, Processors = Environment.ProcessorCount,
            SimulationSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(VectorCollisionWorld).Assembly.Location))),
            Summaries = summaries, Samples = samples
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Collision timing: {output}");
    }

    private static int Batch(Func<Query, bool> execute, Query[] queries, int repeats)
    {
        int contacts = 0;
        for (int repeat = 0; repeat < repeats; repeat++)
        foreach (var query in queries)
            if (execute(query)) contacts++;
        return contacts;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) * .5;
    }
}
