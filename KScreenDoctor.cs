using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TupiScreen;

public sealed class KScreenDoctor
{
    private static readonly Regex GeometryRegex =
        new(@"^(?<x>-?\d+),(?<y>-?\d+)\s+(?<w>\d+)x(?<h>\d+)$", RegexOptions.Compiled);

    private static readonly Regex ModeTokenRegex =
        new(@"\d+:(?<w>\d+)x(?<h>\d+)@(?<rate>[0-9.]+)(?<flags>[!*+]*)", RegexOptions.Compiled);

    public string BinaryPath { get; }

    public KScreenDoctor(string binaryPath = "kscreen-doctor") => BinaryPath = binaryPath;

    public async Task<IReadOnlyList<KScreenOutput>> GetOutputsAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunAsync("-o", cancellationToken).ConfigureAwait(false);
        return ParseOutputs(output);
    }

    // Expose raw output for diagnostics
    public Task<string> GetRawOutputAsync(CancellationToken cancellationToken = default) => RunAsync("-o", cancellationToken);

    public Task EnableAsync(string outputName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputName)) throw new ArgumentException("Output name is required.", nameof(outputName));
        // kscreen-doctor apply commands can return non-zero even when they succeed;
        // pass throwOnError:false so a single-output failure doesn't abort the loop.
        return RunAsync($"output.{outputName}.enable", cancellationToken, throwOnError: false);
    }

    public Task DisableAsync(string outputName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputName)) throw new ArgumentException("Output name is required.", nameof(outputName));
        return RunAsync($"output.{outputName}.disable", cancellationToken, throwOnError: false);
    }

    public Task SetPositionAsync(string outputName, int x, int y, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputName)) throw new ArgumentException("Output name is required.", nameof(outputName));
        return RunAsync($"output.{outputName}.position.{x},{y}", cancellationToken, throwOnError: false);
    }

    /// <summary>
    /// Applies an entire preset atomically in ONE kscreen-doctor call.
    /// Enables (with optional position restore) are always issued before disables
    /// so KDE never sees a state with zero active outputs and rejects the operation.
    /// </summary>
    public Task ApplyFullPresetAsync(
        IReadOnlyList<KScreenOutput> currentOutputs,
        DisplayPreset preset,
        CancellationToken cancellationToken = default)
    {
        var args = new System.Text.StringBuilder();

        // Pass 1: enable + optional position restore
        foreach (var output in currentOutputs)
        {
            var shouldEnable = preset.Outputs.TryGetValue(output.Name, out var v) && v;
            if (!shouldEnable) continue;

            if (args.Length > 0) args.Append(' ');
            args.Append($"output.{output.Name}.enable");

            if (preset.Positions.TryGetValue(output.Name, out var pos) && pos.Length >= 2)
                args.Append($" output.{output.Name}.position.{pos[0]},{pos[1]}");
        }

        // Pass 2: disable (after all enables so KDE never has 0 active outputs)
        foreach (var output in currentOutputs)
        {
            var shouldEnable = preset.Outputs.TryGetValue(output.Name, out var v) && v;
            if (shouldEnable) continue;

            if (args.Length > 0) args.Append(' ');
            args.Append($"output.{output.Name}.disable");
        }

        if (args.Length == 0) return Task.CompletedTask;
        return RunAsync(args.ToString(), cancellationToken, throwOnError: false);
    }

    public async Task ApplyPolicyAsync(CancellationToken cancellationToken = default)
    {
        var outputs = await GetOutputsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var output in outputs)
        {
            if (output.Connected && !output.Enabled)
            {
                await EnableAsync(output.Name, cancellationToken).ConfigureAwait(false);
            }
            else if (!output.Connected && output.Enabled)
            {
                await DisableAsync(output.Name, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> RunAsync(string arguments, CancellationToken cancellationToken, bool throwOnError = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = BinaryPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdOut = await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException($"kscreen-doctor failed ({process.ExitCode}): {stdErr}");

        // Strip ANSI colour escape codes that kscreen-doctor injects
        return Regex.Replace(stdOut, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty);
    }

    private static IReadOnlyList<KScreenOutput> ParseOutputs(string queryOutput)
    {
        var outputs = new List<KScreenOutput>();
        KScreenOutputBuilder? current = null;

        foreach (var rawLine in queryOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.IndexOf("Output:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (current is not null) outputs.Add(current.Build());

                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length >= 4 && int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    var name = tokens[2];
                    var uuid = tokens[3];
                    current = new KScreenOutputBuilder(id, name, uuid);
                    continue;
                }
            }

            if (current is null) continue;

            if (line.StartsWith("enabled", StringComparison.OrdinalIgnoreCase))
                current.Enabled = true;
            else if (line.StartsWith("disabled", StringComparison.OrdinalIgnoreCase))
                current.Enabled = false;

            if (line.StartsWith("connected", StringComparison.OrdinalIgnoreCase))
                current.Connected = true;
            else if (line.StartsWith("disconnected", StringComparison.OrdinalIgnoreCase))
                current.Connected = false;

            if (line.IndexOf("Geometry:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var geoText = line.Substring(line.IndexOf(':') + 1).Trim();
                var geoMatch = GeometryRegex.Match(geoText);
                if (geoMatch.Success)
                {
                    current.PositionX = int.Parse(geoMatch.Groups["x"].Value, CultureInfo.InvariantCulture);
                    current.PositionY = int.Parse(geoMatch.Groups["y"].Value, CultureInfo.InvariantCulture);
                    current.CurrentWidth = int.Parse(geoMatch.Groups["w"].Value, CultureInfo.InvariantCulture);
                    current.CurrentHeight = int.Parse(geoMatch.Groups["h"].Value, CultureInfo.InvariantCulture);
                }
            }

            if (line.IndexOf("Scale:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var idx = line.IndexOf(':');
                var scaleText = line.Substring(idx + 1).Trim();
                if (decimal.TryParse(scaleText, NumberStyles.Any, CultureInfo.InvariantCulture, out var scale))
                    current.Scale = scale;
            }

            if (line.IndexOf("Rotation:", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var idx = line.IndexOf(':');
                var rotationText = line.Substring(idx + 1).Trim();
                if (int.TryParse(rotationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rotation))
                    current.Rotation = rotation;
            }

            // collect mode tokens anywhere on the line
            foreach (Match m in ModeTokenRegex.Matches(line))
            {
                var w = int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture);
                var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
                var rateText = m.Groups["rate"].Value;
                var flags = m.Groups["flags"].Value;
                if (!decimal.TryParse(rateText, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
                    continue;

                current.Modes.Add(new KScreenMode(w, h, rate, flags.Contains('*'), flags.Contains('!') || flags.Contains('+')));
            }
        }

        if (current is not null) outputs.Add(current.Build());

        return outputs;
    }

    private sealed class KScreenOutputBuilder
    {
        public int Id { get; }
        public string Name { get; }
        public string Uuid { get; }
        public bool Enabled { get; set; }
        public bool Connected { get; set; }
        public int? CurrentWidth { get; set; }
        public int? CurrentHeight { get; set; }
        public int? PositionX { get; set; }
        public int? PositionY { get; set; }
        public decimal? Scale { get; set; }
        public int? Rotation { get; set; }
        public List<KScreenMode> Modes { get; } = new();

        public KScreenOutputBuilder(int id, string name, string uuid)
        {
            Id = id;
            Name = name;
            Uuid = uuid;
        }

        public KScreenOutput Build() =>
            new(Id, Name, Uuid, Enabled, Connected, CurrentWidth, CurrentHeight, PositionX, PositionY, Scale, Rotation, Modes);
    }
}

public sealed record KScreenOutput(
    int Id,
    string Name,
    string Uuid,
    bool Enabled,
    bool Connected,
    int? CurrentWidth,
    int? CurrentHeight,
    int? PositionX,
    int? PositionY,
    decimal? Scale,
    int? Rotation,
    IReadOnlyList<KScreenMode> Modes);

public sealed record KScreenMode(
    int Width,
    int Height,
    decimal RefreshRate,
    bool IsCurrent,
    bool IsPreferred);
