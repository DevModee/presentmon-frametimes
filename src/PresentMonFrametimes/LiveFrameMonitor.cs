using System.Buffers;
using System.Diagnostics;
using System.Globalization;

namespace PresentMonFrametimes;

public sealed class LiveFrameCsvReader
{
    private readonly int _frametimeIndex;
    private readonly int _swapChainIndex;

    public LiveFrameCsvReader(string header)
    {
        var columns = header.Split(',');
        _frametimeIndex = Array.FindIndex(columns, column => column.Trim() == "MsBetweenPresents");
        _swapChainIndex = Array.FindIndex(columns, column => column.Trim() == "SwapChainAddress");
    }

    public bool IsSupported => _frametimeIndex >= 0;

    public bool TryRead(ReadOnlySpan<char> line, out double frametime, out ReadOnlySpan<char> swapChain)
    {
        frametime = 0;
        swapChain = default;
        if (!IsSupported)
        {
            return false;
        }

        ReadOnlySpan<char> time = default;
        var last = Math.Max(_frametimeIndex, _swapChainIndex);
        for (var index = 0; index <= last; index++)
        {
            var separator = line.IndexOf(',');
            var field = (separator < 0 ? line : line[..separator]).Trim();
            if (index == _frametimeIndex)
            {
                time = field;
            }

            if (index == _swapChainIndex)
            {
                swapChain = field;
            }

            if (separator < 0)
            {
                break;
            }

            line = line[(separator + 1)..];
        }

        return double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out frametime)
            && double.IsFinite(frametime) && frametime is > 0 and <= 5000;
    }
}

public sealed class LiveFrameWindow(long windowTicks)
{
    private const int MaximumFrames = 32_768;
    private readonly Queue<Frame> _frames = new();
    private readonly Dictionary<string, Chain> _chains = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public void Add(ReadOnlySpan<char> swapChain, double frametime, long timestamp)
    {
        if (!double.IsFinite(frametime) || frametime is <= 0 or > 5000)
        {
            return;
        }

        lock (_gate)
        {
            Prune(timestamp);
            if (_frames.Count == MaximumFrames)
            {
                RemoveOldest();
            }

            var lookup = _chains.GetAlternateLookup<ReadOnlySpan<char>>();
            if (!lookup.TryGetValue(swapChain, out var chain))
            {
                chain = new Chain(swapChain.ToString());
                _chains.Add(chain.Key, chain);
            }

            chain.Count++;
            _frames.Enqueue(new Frame(timestamp, frametime, chain));
        }
    }

    public LiveFrameStats? Current(long timestamp)
    {
        double[] buffer;
        int count;
        lock (_gate)
        {
            Prune(timestamp);
            Chain? dominant = null;
            foreach (var chain in _chains.Values)
            {
                if (dominant is null || chain.Count > dominant.Count)
                {
                    dominant = chain;
                }
            }

            if (dominant is null || dominant.Count < 10)
            {
                return null;
            }

            buffer = ArrayPool<double>.Shared.Rent(dominant.Count);
            count = 0;
            foreach (var frame in _frames)
            {
                if (ReferenceEquals(frame.Chain, dominant))
                {
                    buffer[count++] = frame.Frametime;
                }
            }
        }

        try
        {
            var times = buffer.AsSpan(0, count);
            var lowCount = Math.Max(1, (int)Math.Ceiling(count * 0.01));
            times.Sort();
            var total = 0d;
            var slowest = 0d;
            for (var index = 0; index < times.Length; index++)
            {
                total += times[index];
                if (index >= times.Length - lowCount)
                {
                    slowest += times[index];
                }
            }

            return new LiveFrameStats(count * 1000d / total, lowCount * 1000d / slowest, total / count, count);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(buffer);
        }
    }

    private void Prune(long timestamp)
    {
        var cutoff = timestamp - windowTicks;
        while (_frames.Count > 0 && _frames.Peek().Timestamp < cutoff)
        {
            RemoveOldest();
        }
    }

    private void RemoveOldest()
    {
        var chain = _frames.Dequeue().Chain;
        if (--chain.Count == 0)
        {
            _chains.Remove(chain.Key);
        }
    }

    private sealed class Chain(string key)
    {
        public string Key { get; } = key;

        public int Count { get; set; }
    }

    private readonly record struct Frame(long Timestamp, double Frametime, Chain Chain);
}

public sealed class LiveFrameMonitor : IDisposable
{
    private readonly Process _process;
    private readonly string _executable;
    private readonly string _session;
    private readonly LiveFrameWindow _frames = new(10 * Stopwatch.Frequency);
    private readonly Task _reader;
    private bool _disposed;

    private LiveFrameMonitor(Process process, string executable, string session)
    {
        _process = process;
        _executable = executable;
        _session = session;
        _reader = Task.Run(ReadAsync);
    }

    public static LiveFrameMonitor? TryStart(string? executable, int processId)
    {
        if (executable is null || !Environment.IsPrivilegedProcess)
        {
            return null;
        }

        var session = "PMFrametimesLive_" + Guid.NewGuid().ToString("N")[..10];
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "--process_id", processId.ToString(CultureInfo.InvariantCulture),
            "--output_stdout",
            "--no_console_stats",
            "--no_track_input",
            "--v2_metrics",
            "--stop_existing_session",
            "--session_name", session,
            "--terminate_on_proc_exit",
        })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            var process = Process.Start(start);
            return process is null ? null : new LiveFrameMonitor(process, executable, session);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public LiveFrameStats? Current() => _frames.Current(Stopwatch.GetTimestamp());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
            }

            using var cleanup = Process.Start(new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "--terminate_existing_session", "--session_name", _session },
            });
            cleanup?.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning($"Live frame monitor cleanup failed: {exception.Message}");
        }

        _process.Dispose();
    }

    private async Task ReadAsync()
    {
        try
        {
            var header = await ReadLineAsync().ConfigureAwait(false);
            if (header is null)
            {
                return;
            }

            var csv = new LiveFrameCsvReader(header);
            if (!csv.IsSupported)
            {
                return;
            }

            string? line;
            while ((line = await ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (!csv.TryRead(line, out var frametime, out var swapChain))
                {
                    continue;
                }

                _frames.Add(swapChain, frametime, Stopwatch.GetTimestamp());
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Trace.TraceWarning($"Live frame monitor stopped: {exception.Message}");
        }
    }

    private async Task<string?> ReadLineAsync()
    {
        var line = await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
        return line?.Replace("\0", string.Empty, StringComparison.Ordinal);
    }
}
