using System.Diagnostics;
using System.Globalization;

namespace PresentMonFrametimes;

public interface IPresentMonTrustPolicy
{
    bool IsTrusted(string executablePath);
}

public static class PresentMonLocator
{
    public const string ExecutableName = "PresentMon.exe";

    public static string? Locate(string? configuredPath = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath);
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "presentmon", ExecutableName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, ExecutableName));

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            candidates.Add(Path.Combine(directory.FullName, "tools", "presentmon", ExecutableName));
            directory = directory.Parent;
        }

        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim(), ExecutableName)));

        return candidates.FirstOrDefault(File.Exists);
    }
}

public sealed class PresentMonCapture
{
    private readonly string? _executable;
    private readonly bool _trusted;

    public PresentMonCapture(string? configuredPath = null, IPresentMonTrustPolicy? trustPolicy = null)
    {
        _executable = PresentMonLocator.Locate(configuredPath);
        _trusted = _executable is not null && (trustPolicy is null || trustPolicy.IsTrusted(_executable));
    }

    public bool IsAvailable => _executable is not null && _trusted && Environment.IsPrivilegedProcess;

    public string AvailabilityMessage => _executable switch
    {
        null => "Frame capture needs PresentMon (Intel, open source). Place PresentMon.exe in tools\\presentmon or on PATH.",
        _ when !_trusted => "PresentMon was found but rejected by the configured trust policy.",
        _ when !Environment.IsPrivilegedProcess => "Frame capture needs administrator rights.",
        _ => "PresentMon ready",
    };

    public async Task<FrameCaptureResult> CaptureAsync(
        int processId,
        TimeSpan warmup,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (_executable is null)
        {
            return new FrameCaptureResult(FrameCaptureStatus.ToolUnavailable, [], AvailabilityMessage);
        }

        if (!_trusted)
        {
            return new FrameCaptureResult(FrameCaptureStatus.ToolUntrusted, [], AvailabilityMessage);
        }

        if (!Environment.IsPrivilegedProcess)
        {
            return new FrameCaptureResult(FrameCaptureStatus.RequiresElevation, [], AvailabilityMessage);
        }

        var session = "PMFrametimes_" + Guid.NewGuid().ToString("N")[..12];
        var output = Path.Combine(Path.GetTempPath(), session + ".csv");
        var totalSeconds = (int)Math.Ceiling((warmup + duration).TotalSeconds);
        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "--process_id", processId.ToString(CultureInfo.InvariantCulture),
            "--output_file", output,
            "--no_console_stats",
            "--no_track_input",
            "--v2_metrics",
            "--stop_existing_session",
            "--session_name", session,
            "--timed", totalSeconds.ToString(CultureInfo.InvariantCulture),
            "--terminate_after_timed",
            "--terminate_on_proc_exit",
        })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("PresentMon did not start.");
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(totalSeconds + 30));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                StopSession(process, session);
                return new FrameCaptureResult(FrameCaptureStatus.Cancelled, [], "Frame capture was cancelled.");
            }

            if (!File.Exists(output))
            {
                return new FrameCaptureResult(FrameCaptureStatus.Failed, [], $"PresentMon exited with code {process.ExitCode} without a capture.");
            }

            using var reader = new StreamReader(output);
            var frames = PresentMonCsvParser.Parse(reader, processId, warmup.TotalSeconds);
            return frames.Count == 0
                ? new FrameCaptureResult(FrameCaptureStatus.NoFrames, [], "No frames were presented during capture.")
                : new FrameCaptureResult(FrameCaptureStatus.Captured, frames, $"{frames.Count} frames captured");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or FormatException)
        {
            return new FrameCaptureResult(FrameCaptureStatus.Failed, [], exception.Message);
        }
        finally
        {
            TryDelete(output);
        }
    }

    private void StopSession(Process process, string session)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            using var cleanup = Process.Start(new ProcessStartInfo(_executable!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "--terminate_existing_session", "--session_name", session },
            });
            cleanup?.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning($"PresentMon cleanup failed: {exception.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not delete capture file {path}: {exception.Message}");
        }
    }
}
