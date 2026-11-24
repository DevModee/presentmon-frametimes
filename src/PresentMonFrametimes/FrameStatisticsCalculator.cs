namespace PresentMonFrametimes;

public static class FrameStatisticsCalculator
{
    public const int ReliableLowFrameCount = 10_000;
    private const double StutterFactor = 2.5;
    private const int StutterWindow = 60;

    public static FrameStatistics? Compute(IReadOnlyList<FrameSample> frames)
    {
        if (frames.Count < 2)
        {
            return null;
        }

        var frametimes = frames.Select(frame => frame.FrametimeMs).ToArray();
        var totalMs = frametimes.Sum();
        var descending = frametimes.OrderByDescending(value => value).ToArray();
        var ascending = descending.Reverse().ToArray();
        var mean = totalMs / frametimes.Length;
        var variance = frametimes.Sum(value => (value - mean) * (value - mean)) / (frametimes.Length - 1);

        return new FrameStatistics(
            frametimes.Length,
            totalMs / 1000,
            frametimes.Length / (totalMs / 1000),
            LowFps(descending, 0.01),
            LowFps(descending, 0.001),
            mean,
            Percentile(ascending, 0.99),
            Percentile(ascending, 0.999),
            Math.Sqrt(variance),
            CountStutters(frametimes),
            MedianRatio(frames, frame => frame.GpuBusyMs),
            MedianRatio(frames, frame => frame.CpuBusyMs),
            Median(frames.Select(frame => frame.DisplayLatencyMs).OfType<double>().ToArray()),
            frames
                .Select(frame => frame.PresentMode)
                .OfType<string>()
                .GroupBy(mode => mode)
                .OrderByDescending(group => group.Count())
                .Select(group => group.Key)
                .FirstOrDefault(),
            frametimes.Length >= ReliableLowFrameCount);
    }

    public static double LowFps(double[] descendingFrametimes, double fraction)
    {
        var count = Math.Max(1, (int)Math.Ceiling(descendingFrametimes.Length * fraction));
        var slowest = descendingFrametimes.Take(count).Average();
        return slowest <= 0 ? 0 : 1000 / slowest;
    }

    public static double Percentile(double[] ascending, double percentile)
    {
        if (ascending.Length == 0)
        {
            return 0;
        }

        var position = percentile * (ascending.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? ascending[lower] : ascending[lower] + ((ascending[upper] - ascending[lower]) * (position - lower));
    }

    private static int CountStutters(double[] frametimes)
    {
        var stutters = 0;
        var window = new double[StutterWindow];
        for (var index = StutterWindow; index < frametimes.Length; index++)
        {
            Array.Copy(frametimes, index - StutterWindow, window, 0, StutterWindow);
            Array.Sort(window);
            var median = (window[(StutterWindow / 2) - 1] + window[StutterWindow / 2]) / 2;
            if (frametimes[index] > median * StutterFactor)
            {
                stutters++;
            }
        }

        return stutters;
    }

    private static double? MedianRatio(IReadOnlyList<FrameSample> frames, Func<FrameSample, double?> busy)
    {
        var ratios = frames
            .Where(frame => busy(frame) is > 0)
            .Select(frame => Math.Min(1.5, busy(frame)!.Value / frame.FrametimeMs))
            .ToArray();
        return ratios.Length < frames.Count / 2 ? null : Median(ratios);
    }

    private static double? Median(double[] values)
    {
        if (values.Length == 0)
        {
            return null;
        }

        Array.Sort(values);
        return values.Length % 2 == 1 ? values[values.Length / 2] : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2;
    }
}
