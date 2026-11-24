using System.Globalization;
using PresentMonFrametimes;

namespace PresentMonFrametimes.Tests;

public sealed class FrameStatisticsTests
{
    private static List<FrameSample> Frames(IEnumerable<double> frametimes)
    {
        var elapsed = 0d;
        return frametimes.Select(frametime =>
        {
            elapsed += frametime / 1000;
            return new FrameSample(elapsed, frametime, frametime * 0.5, frametime * 0.95, 10, "Hardware: Independent Flip");
        }).ToList();
    }

    [Fact]
    public void Average_fps_is_time_weighted_not_the_mean_of_instant_fps()
    {
        var statistics = FrameStatisticsCalculator.Compute(Frames([10, 30]))!;
        Assert.Equal(50, statistics.AverageFps, 6);
    }

    [Fact]
    public void One_percent_low_averages_the_slowest_one_percent_of_frames()
    {
        var frametimes = Enumerable.Repeat(5.0, 990).Concat(Enumerable.Repeat(20.0, 10));
        var statistics = FrameStatisticsCalculator.Compute(Frames(frametimes))!;

        Assert.Equal(50, statistics.OnePercentLowFps, 6);
        Assert.Equal(50, statistics.PointOnePercentLowFps, 6);
        Assert.Equal(1000, statistics.FrameCount);
        Assert.False(statistics.LowsReliable);
    }

    [Fact]
    public void Busy_ratios_latency_and_present_mode_are_summarized()
    {
        var statistics = FrameStatisticsCalculator.Compute(Frames(Enumerable.Repeat(8.0, 200)))!;

        Assert.Equal(0.95, statistics.GpuBusyRatio!.Value, 6);
        Assert.Equal(0.5, statistics.CpuBusyRatio!.Value, 6);
        Assert.Equal(10, statistics.DisplayLatencyMs);
        Assert.Equal("Hardware: Independent Flip", statistics.PresentMode);
        Assert.Equal(100, statistics.ConsistencyPercent, 6);
    }

    [Fact]
    public void Stutters_are_frames_far_above_the_rolling_median()
    {
        var frametimes = Enumerable.Repeat(7.0, 300).ToList();
        frametimes[150] = 40;
        frametimes[250] = 30;

        Assert.Equal(2, FrameStatisticsCalculator.Compute(Frames(frametimes))!.StutterCount);
    }

    [Fact]
    public void Percentile_interpolates_linearly()
    {
        Assert.Equal(9.91, FrameStatisticsCalculator.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0.99), 6);
    }

    [Fact]
    public void Too_few_frames_produce_no_statistics()
    {
        Assert.Null(FrameStatisticsCalculator.Compute(Frames([7])));
    }
}

public sealed class PresentMonParserAndLiveWindowTests
{
    private const string V2Header = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,DisplayLatency,DisplayedTime,MsAnimationError,AnimationTime,MsClickToPhotonLatency,MsAllInputToPhotonLatency,InstrumentedLatency,MsBetweenPresents,MsInPresentAPI,MsBetweenDisplayChange,MsUntilDisplayed,MsRenderPresentLatency,MsBetweenSimulationStart,MsPCLatency,MsBetweenAppStart";

    private static string Row(int processId, string swapChain, double frametime, string gpuBusy = "7.0") =>
        $"game.exe,{processId},{swapChain},DXGI,0,512,1,Hardware: Independent Flip,1.0,3.5,1.0,0.5,7.2,{gpuBusy},0.1,12.0,7.6,NA,NA,NA,NA,NA,{frametime.ToString(CultureInfo.InvariantCulture)},0.2,7.6,11.5,5.0,NA,NA,{frametime.ToString(CultureInfo.InvariantCulture)}";

    [Fact]
    public void Parses_v2_columns_and_keeps_the_busiest_swap_chain()
    {
        var csv = string.Join('\n', new[] { V2Header }
            .Concat(Enumerable.Range(0, 50).Select(_ => Row(42, "0x1", 8)))
            .Concat(Enumerable.Range(0, 5).Select(_ => Row(42, "0x2", 16))));

        var frames = PresentMonCsvParser.Parse(new StringReader(csv), 42);

        Assert.Equal(50, frames.Count);
        Assert.All(frames, frame => Assert.Equal(8, frame.FrametimeMs));
        Assert.Equal(7.0, frames[0].GpuBusyMs);
        Assert.Equal(11.5, frames[0].DisplayLatencyMs);
        Assert.Equal("Hardware: Independent Flip", frames[0].PresentMode);
    }

    [Fact]
    public void Skips_other_processes_not_applicable_values_and_warmup()
    {
        var csv = string.Join('\n', new[] { V2Header, Row(99, "0x1", 8), Row(42, "0x1", 500), Row(42, "0x1", 500), Row(42, "0x1", 10, "NA") }
            .Append("game.exe,42,0x1,DXGI,0,512,1,Composed: Flip,1.0,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA"));

        var frames = PresentMonCsvParser.Parse(new StringReader(csv), 42, warmupSeconds: 1.0);

        var frame = Assert.Single(frames);
        Assert.Equal(10, frame.FrametimeMs);
        Assert.Null(frame.GpuBusyMs);
    }

    [Fact]
    public void Live_reader_and_window_compute_dominant_chain_lows()
    {
        var reader = new LiveFrameCsvReader("Application,SwapChainAddress,MsBetweenPresents");
        Assert.True(reader.IsSupported);

        var window = new LiveFrameWindow(windowTicks: 1000);
        for (var i = 0; i < 99; i++)
        {
            Assert.True(reader.TryRead("game.exe,0x1,5.0", out var ft, out var chain));
            window.Add(chain, ft, 100 + i);
        }

        Assert.True(reader.TryRead("game.exe,0x1,20.0", out var slowFt, out var slowChain));
        window.Add(slowChain, slowFt, 200);

        var stats = window.Current(250)!;
        Assert.Equal(100, stats.Frames);
        Assert.Equal(50, stats.OnePercentLowFps, 6);
    }
}
