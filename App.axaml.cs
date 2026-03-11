using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
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

            var mainWindow = new MainWindow();
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

            var tray = new TrayIcon { ToolTipText = "TupiScreen", Icon = GenerateTrayIcon(), Menu = menu };
            tray.Clicked += (_, _) => { mainWindow.Show(); mainWindow.Activate(); };

            var icons = new TrayIcons();
            icons.Add(tray);
            TrayIcon.SetIcons(this, icons);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Generates a small monitor-shaped icon at runtime — no image file needed.
    private static WindowIcon GenerateTrayIcon()
    {
        const int S = 32;
        var pixels = new byte[S * S * 4]; // BGRA8888, all transparent

        // BGRA byte order: [0]=B [1]=G [2]=R [3]=A
        void Fill(int x0, int y0, int x1, int y1, byte b, byte g, byte r)
        {
            for (var y = y0; y <= y1 && y < S; y++)
            for (var x = x0; x <= x1 && x < S; x++)
            {
                var i = (y * S + x) * 4;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255;
            }
        }

        Fill(1, 3, 30, 21, 0xEB, 0x63, 0x25); // monitor body  — #2563EB (blue)
        Fill(3, 5, 28, 19, 0xE0, 0xF2, 0xFE); // screen area   — #FEF2E0 (light)
        Fill(13, 22, 18, 26, 0xEB, 0x63, 0x25); // stand
        Fill(9, 27, 22, 28, 0xEB, 0x63, 0x25); // base

        var bmp = new WriteableBitmap(
            new PixelSize(S, S),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = bmp.Lock())
        {
            var stride = fb.RowBytes;
            for (var y = 0; y < S; y++)
                Marshal.Copy(pixels, y * S * 4, IntPtr.Add(fb.Address, y * stride), S * 4);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}