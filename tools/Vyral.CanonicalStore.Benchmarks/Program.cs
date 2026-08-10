using System.Text.Json;
using Vyral.CanonicalStore.Benchmarks;

try
{
    var options = CanonicalMySqlBenchmarkOptions.Parse(args);
    var report = await CanonicalMySqlContentionBenchmark.RunAsync(options);
    var json = JsonSerializer.Serialize(report, CanonicalMySqlBenchmarkJson.Options);
    Console.WriteLine(json);

    if (!string.IsNullOrWhiteSpace(options.OutputPath))
    {
        var outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, json + Environment.NewLine);
        Console.Error.WriteLine($"canonical-mysql-benchmark-report={outputPath}");
    }

    return report.Decision.WithinBudget ? 0 : 3;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"canonical-mysql-benchmark=failed class={CanonicalMySqlBenchmarkFailureClassifier.Classify(exception)}");
    return 2;
}
