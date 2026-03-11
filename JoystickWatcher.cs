using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TupiScreen;

// ── Persisted settings ────────────────────────────────────────────────────────

public sealed class DisplayPreset
{
    // output name → should be enabled
    public Dictionary<string, bool> Outputs { get; set; } = new();

    // output name → [x, y] screen position (captured in snapshot so we can
    // restore geometry on re-enable and avoid KDE defaulting to mirror/clone).
    public Dictionary<string, int[]> Positions { get; set; } = new();
}

public sealed class AppSettings
{
    /// <summary>The preset applied when a joystick is detected.</summary>
    public DisplayPreset WithJoystick         { get; set; } = new();
    public string        WithJoystickAudioSink { get; set; } = string.Empty;

    /// <summary>Auto-snapshot taken the moment the joystick is plugged in,
    /// so we can restore the previous state when it is unplugged.</summary>
    public DisplayPreset SnapshotOutputs  { get; set; } = new();
    public string        SnapshotAudioSink { get; set; } = string.Empty;

    /// <summary>
    /// True while the joystick preset is active (written to disk at connect,
    /// cleared at disconnect). If the app starts with this flag set and no
    /// joystick is present, the snapshot must be restored (crash/restart recovery).
    /// </summary>
    public bool JoystickWasActive { get; set; } = false;

    private static readonly string _path =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "tupiscreen", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize(File.ReadAllText(_path),
                           AppSettingsContext.Default.AppSettings) ?? new();
        }
        catch { /* corrupt file — start fresh */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(this, AppSettingsContext.Default.AppSettings));
    }
}

// ── Joystick watcher (polls /dev/input/js* and /dev/input/event*) ─────────────

public sealed class JoystickWatcher : IDisposable
{
    public event Action<bool>? JoystickPresenceChanged;

    private bool _lastState;
    private readonly CancellationTokenSource _cts = new();

    public JoystickWatcher() => _lastState = IsJoystickPresent();

    public bool IsJoystickPresent()
    {
        // js0-js9 covers classic joystick/gamepad nodes
        for (var i = 0; i < 10; i++)
            if (File.Exists($"/dev/input/js{i}")) return true;
        return false;
    }

    public void Start()
    {
        Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(2000, _cts.Token).ConfigureAwait(false);
                var current = IsJoystickPresent();
                if (current != _lastState)
                {
                    _lastState = current;
                    JoystickPresenceChanged?.Invoke(current);
                }
            }
        }, _cts.Token);
    }

    public void Dispose() => _cts.Cancel();
}

// ── JSON source-generation context (required for AOT) ────────────────────────

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(DisplayPreset))]
[JsonSerializable(typeof(Dictionary<string, bool>))]
[JsonSerializable(typeof(Dictionary<string, int[]>))]
[JsonSerializable(typeof(int[]))]
internal sealed partial class AppSettingsContext : JsonSerializerContext { }
