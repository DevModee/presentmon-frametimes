using System.Globalization;

namespace PresentMonFrametimes;

public static class PresentMonCsvParser
{
    private static readonly string[] FrametimeColumns = ["MsBetweenPresents", "MsBetweenAppStart", "FrameTime"];
    private static readonly string[] GpuBusyColumns = ["MsGPUBusy", "MsGPUActive"];
    private static readonly string[] LatencyColumns = ["MsUntilDisplayed", "DisplayLatency"];

    public static IReadOnlyList<FrameSample> Parse(TextReader reader, int? processId = null, double warmupSeconds = 0)
    {
        var header = Clean(reader.ReadLine());
        if (header is null)
        {
            return [];
        }

        var columns = header.Split(',').Select(column => column.Trim()).ToArray();
        int Find(params string[] names) => names
            .Select(name => Array.FindIndex(columns, column => string.Equals(column, name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(index => index >= 0, -1);

        var frametimeIndex = Find(FrametimeColumns);
        if (frametimeIndex < 0)
        {
            throw new FormatException("The PresentMon capture has no frame time column.");
        }

        var processIndex = Find("ProcessID");
        var swapChainIndex = Find("SwapChainAddress");
        var cpuBusyIndex = Find("MsCPUBusy");
        var gpuBusyIndex = Find(GpuBusyColumns);
        var latencyIndex = Find(LatencyColumns);
        var presentModeIndex = Find("PresentMode");

        var bySwapChain = new Dictionary<string, List<(double Frametime, double? Cpu, double? Gpu, double? Latency, string? Mode)>>(StringComparer.Ordinal);
        string? line;
        while ((line = Clean(reader.ReadLine())) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length <= frametimeIndex)
            {
                continue;
            }

            if (processId is { } pid && processIndex >= 0 && (!int.TryParse(Field(fields, processIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rowPid) || rowPid != pid))
            {
                continue;
            }

            var frametime = Number(Field(fields, frametimeIndex));
            if (frametime is not > 0 || frametime > 5000)
            {
                continue;
            }

            var swapChain = swapChainIndex >= 0 ? Field(fields, swapChainIndex) ?? string.Empty : string.Empty;
            if (!bySwapChain.TryGetValue(swapChain, out var frames))
            {
                frames = [];
                bySwapChain[swapChain] = frames;
            }

            frames.Add((
                frametime.Value,
                Number(Field(fields, cpuBusyIndex)),
                Number(Field(fields, gpuBusyIndex)),
                Number(Field(fields, latencyIndex)),
                Field(fields, presentModeIndex)));
        }

        var primary = bySwapChain.Values.OrderByDescending(frames => frames.Count).FirstOrDefault() ?? [];
        var result = new List<FrameSample>(primary.Count);
        var elapsed = 0d;
        foreach (var frame in primary)
        {
            elapsed += frame.Frametime / 1000;
            if (elapsed <= warmupSeconds)
            {
                continue;
            }

            result.Add(new FrameSample(elapsed, frame.Frametime, frame.Cpu, frame.Gpu, frame.Latency, frame.Mode));
        }

        return result;
    }

    private static string? Clean(string? line) => line?.Replace("\0", string.Empty, StringComparison.Ordinal);

    private static string? Field(string[] fields, int index) => index >= 0 && index < fields.Length ? fields[index].Trim() : null;

    private static double? Number(string? value) =>
        value is null || value.Length == 0 || value.Equals("NA", StringComparison.OrdinalIgnoreCase)
            ? null
            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
