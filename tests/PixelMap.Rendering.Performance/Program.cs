if (args.Contains("--terrain-streaming-cost", StringComparer.Ordinal))
    await TerrainArtworkInvalidationChecks.RunBenchmarkAsync(args);
else
    await WebGpuFrameBenchmark.RunAsync(args);
