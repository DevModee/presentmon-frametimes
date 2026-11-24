using System.Globalization;
using PresentMonFrametimes;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project samples/PresentMonFrametimes.Cli -- --csv <path> [--pid <id>] [--warmup <seconds>]");
    Console.WriteLine("  dotnet run --project samples/PresentMonFrametimes.Cli -- --pid <id> [--duration <seconds>] [--warmup <seconds>] [--presentmon <path>]");
    return 0;
}

string? GetArg(string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

var csvPath = GetArg("--csv");
var pid = int.TryParse(GetArg("--pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPid) ? parsedPid : (int?)null;
var warmup = double.TryParse(GetArg("--warmup"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWarmup) ? parsedWarmup : 0d;

IReadOnlyList<FrameSample> frames;

if (csvPath is not null)
{
    if (!File.Exists(csvPath))
    {
        Console.Error.WriteLine($"CSV file not found: {csvPath}");
        return 1;
    }

    using var reader = new StreamReader(csvPath);
    frames = PresentMonCsvParser.Parse(reader, pid, warmup);
}
else if (pid is { } targetPid)
{
    var duration = double.TryParse(GetArg("--duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration) ? parsedDuration : 15d;
    var capture = new PresentMonCapture(GetArg("--presentmon"));
    if (!capture.IsAvailable)
    {
        Console.Error.WriteLine(capture.AvailabilityMessage);
        return 1;
    }

    Console.WriteLine($"Capturing PID {targetPid} ({warmup:0.#}s warmup + {duration:0.#}s sample)...");
    var result = await capture.CaptureAsync(targetPid, TimeSpan.FromSeconds(warmup), TimeSpan.FromSeconds(duration));
    if (result.Status != FrameCaptureStatus.Captured)
    {
        Console.Error.WriteLine($"{result.Status}: {result.Message}");
        return 1;
    }

    frames = result.Frames;
}
else
{
    Console.Error.WriteLine("Provide either --csv <path> or --pid <id>.");
    return 1;
}

var stats = FrameStatisticsCalculator.Compute(frames);
if (stats is null)
{
    Console.Error.WriteLine("Not enough frames to compute statistics (need at least 2).");
    return 1;
}

Console.WriteLine($"Frames:       {stats.FrameCount} ({stats.DurationSeconds:0.00}s)");
Console.WriteLine($"Average FPS:  {stats.AverageFps:0.0}");
Console.WriteLine($"1% Low FPS:   {stats.OnePercentLowFps:0.0}");
Console.WriteLine($"0.1% Low FPS: {stats.PointOnePercentLowFps:0.0} {(stats.LowsReliable ? "" : "(< 10,000 frames)")}");
Console.WriteLine($"Frametime:    mean {stats.MeanFrametimeMs:0.00} ms | P99 {stats.P99FrametimeMs:0.00} ms | P99.9 {stats.P999FrametimeMs:0.00} ms | stddev {stats.FrametimeStdDevMs:0.00} ms");
Console.WriteLine($"Stutters:     {stats.StutterCount} (frames > 2.5x 60-frame rolling median)");
Console.WriteLine($"Consistency:  {stats.ConsistencyPercent:0.0}%");
if (stats.PresentMode is not null)
{
    Console.WriteLine($"PresentMode:  {stats.PresentMode}");
}

return 0;
