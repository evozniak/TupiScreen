using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TupiScreen;

public partial class MainWindow : Window
{
    private readonly KScreenDoctor _screenDoctor = new();
    private readonly JoystickWatcher _watcher = new();
    private AppSettings _settings = AppSettings.Load();

    private IReadOnlyList<KScreenOutput> _outputs = Array.Empty<KScreenOutput>();

    // Snapshot used only for the manual 15-second test/revert flow.
    private List<(string Name, bool Enabled)> _testSnapshot = new();
    private string _testSnapshotAudio = string.Empty;
    private CancellationTokenSource? _countdownCts;

    // Prevent auto-save from firing while we're programmatically populating the UI.
    private bool _suppressAutoSave;

    public MainWindow()
    {
        InitializeComponent();

        Opened       += async (_, _) => await OnOpenedAsync();
        RefreshButton.Click += async (_, _) => await RefreshAsync();
        TestJoyButton.Click += async (_, _) => await ApplyAndCountdownAsync();
        RevertButton.Click  += async (_, _) => await RevertTestAsync();

        _watcher.JoystickPresenceChanged += present =>
            Dispatcher.UIThread.Post(async () =>
            {
                JoystickStatus.Text = present ? "🕹️ Joystick connected" : "🎮 No joystick";
                await OnJoystickChangedAsync(present);
            });

        JoystickStatus.Text = _watcher.IsJoystickPresent() ? "🕹️ Joystick connected" : "🎮 No joystick";
        _watcher.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher.Dispose();
        base.OnClosed(e);
    }

    // Hide to tray instead of exiting. The tray "Quit" item calls Environment.Exit(0).
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    // ── startup ───────────────────────────────────────────────────────────────

    private async Task OnOpenedAsync()
    {
        await RefreshAsync();

        // Crash/restart recovery: if we shut down (or were killed) while the
        // joystick preset was active and the joystick is now gone, revert the
        // snapshot so the user's monitors are restored.
        if (_settings.JoystickWasActive && !_watcher.IsJoystickPresent())
        {
            JoystickStatus.Text = "🔄 Reverting (restart recovery)…";
            await OnJoystickChangedAsync(false);
            JoystickStatus.Text = "🎮 No joystick";
        }
    }

    // ── refresh UI ────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        _outputs = await _screenDoctor.GetOutputsAsync(CancellationToken.None);
        var sinks = await AudioControl.GetSinksAsync();

        _suppressAutoSave = true;
        try
        {
            PopulatePanel(JoyPanel, _settings.WithJoystick);

            JoyAudioCombo.ItemsSource  = sinks;
            JoyAudioCombo.SelectedItem = _settings.WithJoystickAudioSink;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        // Wire auto-save after population so we don't double-wire on re-refresh.
        WireAutoSave();
    }

    private bool _autoSaveWired;

    private void WireAutoSave()
    {
        if (_autoSaveWired) return;
        _autoSaveWired = true;

        JoyAudioCombo.SelectionChanged += (_, _) => AutoSavePreset();

        // Checkboxes are added inside PopulatePanel; hook them here after the fact.
        foreach (var child in JoyPanel.Children)
            if (child is CheckBox cb)
                cb.IsCheckedChanged += (_, _) => AutoSavePreset();
    }

    private void PopulatePanel(StackPanel panel, DisplayPreset preset)
    {
        panel.Children.Clear();
        foreach (var o in _outputs)
        {
            var enabled = preset.Outputs.TryGetValue(o.Name, out var v) ? v : o.Enabled;
            var cb = new CheckBox
            {
                Tag       = o.Name,
                Content   = $"{o.Name}  {o.CurrentWidth}x{o.CurrentHeight}  Scale:{o.Scale}",
                IsChecked = enabled,
                Margin    = new Avalonia.Thickness(2)
            };
            cb.IsCheckedChanged += (_, _) => AutoSavePreset();
            panel.Children.Add(cb);
        }
    }

    // ── auto-save preset ──────────────────────────────────────────────────────

    private void AutoSavePreset()
    {
        if (_suppressAutoSave) return;

        var preset = new DisplayPreset();
        foreach (var child in JoyPanel.Children)
            if (child is CheckBox cb && cb.Tag is string name)
                preset.Outputs[name] = cb.IsChecked == true;

        _settings.WithJoystick         = preset;
        _settings.WithJoystickAudioSink = JoyAudioCombo.SelectedItem as string ?? string.Empty;
        _settings.Save();
    }

    // ── joystick hotplug: snapshot-on-connect, restore-on-disconnect ──────────

    private async Task OnJoystickChangedAsync(bool joystickPresent)
    {
        // Always fetch fresh state — the cached list may be stale or empty
        // (window might never have been opened).
        _outputs = await _screenDoctor.GetOutputsAsync(CancellationToken.None);
        if (_outputs.Count == 0) return;

        if (joystickPresent)
        {
            // Capture the current system state (including positions) before touching anything.
            var snap = new DisplayPreset();
            foreach (var o in _outputs)
            {
                snap.Outputs[o.Name] = o.Enabled;
                if (o.PositionX.HasValue && o.PositionY.HasValue)
                    snap.Positions[o.Name] = new[] { o.PositionX.Value, o.PositionY.Value };
            }
            _settings.SnapshotOutputs   = snap;
            _settings.SnapshotAudioSink = await AudioControl.GetDefaultSinkAsync();
            _settings.JoystickWasActive = true;
            _settings.Save();

            // Apply the joystick preset (only if the user has configured one).
            if (_settings.WithJoystick.Outputs.Count == 0) return;
            await ApplyPresetAsync(_settings.WithJoystick);
            if (!string.IsNullOrEmpty(_settings.WithJoystickAudioSink))
                await AudioControl.SetDefaultSinkAsync(_settings.WithJoystickAudioSink, CancellationToken.None);
        }
        else
        {
            // Restore the snapshot saved when the joystick was plugged in.
            if (_settings.SnapshotOutputs.Outputs.Count > 0)
            {
                await ApplyPresetAsync(_settings.SnapshotOutputs);
                if (!string.IsNullOrEmpty(_settings.SnapshotAudioSink))
                    await AudioControl.SetDefaultSinkAsync(_settings.SnapshotAudioSink, CancellationToken.None);
            }

            _settings.JoystickWasActive = false;
            _settings.Save();
        }
    }

    // ── apply preset (enable-first, disable-after, single kscreen-doctor call) ──

    private Task ApplyPresetAsync(DisplayPreset preset)
        => _screenDoctor.ApplyFullPresetAsync(_outputs, preset, CancellationToken.None);

    // ── manual Test with 15-second countdown ──────────────────────────────────

    private async Task ApplyAndCountdownAsync()
    {
        _testSnapshot      = _outputs.Select(o => (o.Name, o.Enabled)).ToList();
        _testSnapshotAudio = await AudioControl.GetDefaultSinkAsync();

        var preset = new DisplayPreset();
        foreach (var child in JoyPanel.Children)
            if (child is CheckBox cb && cb.Tag is string name)
                preset.Outputs[name] = cb.IsChecked == true;

        await ApplyPresetAsync(preset);

        var sink = JoyAudioCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(sink))
            await AudioControl.SetDefaultSinkAsync(sink, CancellationToken.None);

        _countdownCts?.Cancel();
        _countdownCts = new CancellationTokenSource();
        var token = _countdownCts.Token;

        RevertButton.IsVisible = true;

        try
        {
            for (var i = 15; i > 0; i--)
            {
                token.ThrowIfCancellationRequested();
                CountdownText.Text = $"Reverting in {i}s…";
                await Task.Delay(1000, token);
            }
            await RevertTestAsync();
        }
        catch (OperationCanceledException) { }
    }

    // ── revert (manual test only) ─────────────────────────────────────────────

    private async Task RevertTestAsync()
    {
        _countdownCts?.Cancel();
        CountdownText.Text     = string.Empty;
        RevertButton.IsVisible = false;

        foreach (var (name, enabled) in _testSnapshot)
        {
            if (enabled) await _screenDoctor.EnableAsync(name,  CancellationToken.None);
            else         await _screenDoctor.DisableAsync(name, CancellationToken.None);
        }

        if (!string.IsNullOrEmpty(_testSnapshotAudio))
            await AudioControl.SetDefaultSinkAsync(_testSnapshotAudio, CancellationToken.None);

        await RefreshAsync();
    }
}