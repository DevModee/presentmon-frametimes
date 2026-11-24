namespace PresentMonFrametimes;

public sealed record FrameSample(
    double TimeSeconds,
    double FrametimeMs,
    double? CpuBusyMs,
    double? GpuBusyMs,
    double? DisplayLatencyMs,
    string? PresentMode);

public sealed record FrameStatistics(
    int FrameCount,
    double DurationSeconds,
    double AverageFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double MeanFrametimeMs,
    double P99FrametimeMs,
    double P999FrametimeMs,
    double FrametimeStdDevMs,
    int StutterCount,
    double? GpuBusyRatio,
    double? CpuBusyRatio,
    double? DisplayLatencyMs,
    string? PresentMode,
    bool LowsReliable)
{
    public double ConsistencyPercent => AverageFps <= 0 ? 0 : Math.Clamp(OnePercentLowFps / AverageFps * 100, 0, 100);
}

public sealed record LiveFrameStats(double Fps, double OnePercentLowFps, double FrametimeMs, int Frames);

public enum FrameCaptureStatus
{
    Captured,
    ToolUnavailable,
    ToolUntrusted,
    RequiresElevation,
    NoFrames,
    Failed,
    Cancelled,
}

public sealed record FrameCaptureResult(
    FrameCaptureStatus Status,
    IReadOnlyList<FrameSample> Frames,
    string Message);
