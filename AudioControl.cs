using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TupiScreen;

/// <summary>Thin wrapper around <c>pactl</c> for PulseAudio / PipeWire audio-sink management.</summary>
public static class AudioControl
{
    /// <summary>Returns the short names of all available sinks.</summary>
    public static async Task<IReadOnlyList<string>> GetSinksAsync(CancellationToken ct = default)
    {
        var raw = await RunAsync("pactl", "list sinks short", ct);
        return raw.Output.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
                  .Select(line => line.Split('\t') is { Length: >= 2 } p ? p[1].Trim() : null)
                  .OfType<string>()
                  .ToList();
    }

    /// <summary>Returns the name of the current default sink.</summary>
    public static async Task<string> GetDefaultSinkAsync(CancellationToken ct = default)
    {
        var result = await RunAsync("pactl", "get-default-sink", ct);
        return result.Output.Trim();
    }

    /// <summary>
    /// Sets the default PulseAudio / PipeWire sink by name.
    /// Retries up to <paramref name="maxAttempts"/> times with a short delay between
    /// attempts, because PipeWire can briefly recreate the HDMI audio device when
    /// the HDMI monitor is enabled/disabled by kscreen-doctor.
    /// </summary>
    public static async Task SetDefaultSinkAsync(
        string sinkName,
        CancellationToken ct = default,
        int maxAttempts = 6,
        int delayMs = 800)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // Small upfront delay on all but the first attempt, and also a
            // one-shot initial pause so PipeWire can settle after a monitor change.
            if (attempt == 1)
                await Task.Delay(500, ct).ConfigureAwait(false); // let kscreen-doctor settle
            else
                await Task.Delay(delayMs, ct).ConfigureAwait(false);

            var result = await RunAsync("pactl", $"set-default-sink {sinkName}", ct);
            if (result.ExitCode == 0) return; // success
        }
        // All attempts failed — silently give up (non-fatal).
    }

    // ── internal helpers ──────────────────────────────────────────────────────

    private readonly record struct ProcessResult(string Output, string Error, int ExitCode);

    private static async Task<ProcessResult> RunAsync(string command, string args, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            });
            if (process is null) return new("", "Could not start process", -1);

            var stdOut = process.StandardOutput.ReadToEndAsync(ct);
            var stdErr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return new(await stdOut, await stdErr, process.ExitCode);
        }
        catch
        {
            return new("", "Exception", -1);
        }
    }
}
