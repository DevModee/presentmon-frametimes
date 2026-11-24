# presentmon-frametimes

Lightweight, zero-dependency .NET 10 library and CLI for capturing and analyzing real PC gaming frametimes via Intel [PresentMon](https://github.com/GameTechDev/PresentMon), extracted from [Atom](https://aerosdev.com/atom).

## What it measures

- **Time-weighted Average FPS** (not the naive mean of instantaneous FPS).
- **1% Low and 0.1% Low FPS** from the slowest 1% / 0.1% of presented frames, with an explicit `LowsReliable` flag (`>= 10,000` frames).
- **Frametime percentiles:** mean, `P99`, `P99.9`, and standard deviation in milliseconds.
- **Stutter count:** frames exceeding `2.5x` the 60-frame rolling median.
- **Dominant swap chain filtering:** automatically ignores auxiliary overlays/launchers sharing the same PID.
- **GPU / CPU busy ratios, display latency, and dominant PresentMode** when present in PresentMon v2 CSVs.
- **Live rolling monitor:** `LiveFrameMonitor` streams `--output_stdout` using a `ReadOnlySpan<char>` parser and `ArrayPool<double>` rolling 10-second window.

## Quick start

### Analyze an existing PresentMon CSV

```csharp
using PresentMonFrametimes;

using var reader = new StreamReader("capture.csv");
var frames = PresentMonCsvParser.Parse(reader, processId: 1234, warmupSeconds: 5);
var stats = FrameStatisticsCalculator.Compute(frames);

if (stats is not null)
{
    Console.WriteLine($"Avg: {stats.AverageFps:0.0} FPS | 1% Low: {stats.OnePercentLowFps:0.0} | 0.1% Low: {stats.PointOnePercentLowFps:0.0}");
}
```

### Run a timed capture (Windows, elevated)

```csharp
using PresentMonFrametimes;

var capture = new PresentMonCapture();
if (capture.IsAvailable)
{
    var result = await capture.CaptureAsync(processId: 1234, warmup: TimeSpan.FromSeconds(5), duration: TimeSpan.FromSeconds(30));
    var stats = FrameStatisticsCalculator.Compute(result.Frames);
}
```

### Run the sample CLI

```bash
dotnet run --project samples/PresentMonFrametimes.Cli -- --csv path/to/capture.csv --warmup 5
```

### Run tests

```bash
dotnet test tests/PresentMonFrametimes.Tests
```

## License

MIT
