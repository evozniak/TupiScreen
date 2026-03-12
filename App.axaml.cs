using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace TupiScreen;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep the process alive when the window is hidden (minimize-to-tray).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var icon = LoadIcon();

            var mainWindow = new MainWindow { Icon = icon };
            // Do NOT set desktop.MainWindow — the window starts hidden in the tray.
            // ShutdownMode.OnExplicitShutdown keeps the process alive.

            // Build the tray icon menu.
            var showItem = new NativeMenuItem("Show TupiScreen");
            showItem.Click += (_, _) => { mainWindow.Show(); mainWindow.Activate(); };

            var quitItem = new NativeMenuItem("Quit");
            quitItem.Click += (_, _) => Environment.Exit(0);

            var menu = new NativeMenu();
            menu.Items.Add(showItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quitItem);

            var tray = new TrayIcon { ToolTipText = "TupiScreen", Icon = icon, Menu = menu };
            tray.Clicked += (_, _) => { mainWindow.Show(); mainWindow.Activate(); };

            var icons = new TrayIcons();
            icons.Add(tray);
            TrayIcon.SetIcons(this, icons);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(
            new Uri("avares://TupiScreen/assets/icons/hicolor/512x512/apps/tupiscreen.png"));
        return new WindowIcon(stream);
    }
}