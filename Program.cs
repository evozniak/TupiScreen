using Avalonia;
using System;

namespace TupiScreen;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is not null && args.Length > 0 && args[0] == "--kscreen-test")
        {
            // Dump raw kscreen-doctor output for diagnostics
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "kscreen-doctor",
                    Arguments = "-o",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var p = System.Diagnostics.Process.Start(psi)!;
                var so = p.StandardOutput.ReadToEnd();
                var se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Console.WriteLine("---- RAW kscreen-doctor stdout ----");
                Console.WriteLine(so);
                Console.WriteLine($"---- RAW kscreen-doctor stderr ----");
                Console.WriteLine(se);
                Console.WriteLine($"ExitCode: {p.ExitCode}");

                // Quick regex test to compare against parser pattern
                var testRegex = new System.Text.RegularExpressions.Regex("^Output:\\s+(?<id>\\d+)\\s+(?<name>\\S+)\\s+(?<uuid>\\S+)$");
                Console.WriteLine("---- Regex test per-line ----");
                foreach (var rawLine2 in so.Split('\n', System.StringSplitOptions.RemoveEmptyEntries))
                {
                    var l = rawLine2.Trim();
                    Console.WriteLine($"MATCH:{testRegex.IsMatch(l)} LINE:'{l}'");
                }

                    // Quick inline parser to ensure we can show displays regardless of KScreenDoctor parsing.
                    Console.WriteLine("---- Inline parse result ----");
                    var results = new System.Collections.Generic.List<(string Name, bool? Enabled, bool? Connected, string Geometry, string Scale, string Rotation)>();
                    string? curName = null;
                    bool? curEnabled = null;
                    bool? curConnected = null;
                    string curGeometry = "";
                    string curScale = "";
                    string curRotation = "";

                    foreach (var rawLine3 in so.Split('\n'))
                    {
                        var line3 = rawLine3.Trim();
                        Console.WriteLine($"[INLINE] '{line3}' (len={line3.Length})");
                        if (line3.IndexOf("Output:", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (curName is not null)
                            {
                                results.Add((curName, curEnabled, curConnected, curGeometry, curScale, curRotation));
                                Console.WriteLine($"[ADDED] {curName} Enabled:{curEnabled} Connected:{curConnected} Geometry:{curGeometry} Scale:{curScale} Rotation:{curRotation}");
                                curEnabled = null; curConnected = null; curGeometry = ""; curScale = ""; curRotation = "";
                            }
                            var parts = line3.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                                Console.WriteLine($"[PARTS] count={parts.Length}");
                                for (int i=0;i<parts.Length;i++) Console.WriteLine($"  part[{i}]='{parts[i]}'");
                            if (parts.Length >= 4) curName = parts[2];
                            continue;
                        }

                    if (line3.IndexOf("enabled", StringComparison.OrdinalIgnoreCase) >= 0) curEnabled = true;
                    else if (line3.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0) curEnabled = false;
                    else if (line3.IndexOf("connected", StringComparison.OrdinalIgnoreCase) >= 0) curConnected = true;
                    else if (line3.IndexOf("disconnected", StringComparison.OrdinalIgnoreCase) >= 0) curConnected = false;
                    else if (line3.IndexOf("Geometry:", StringComparison.OrdinalIgnoreCase) >= 0) curGeometry = line3.Substring(line3.IndexOf(':') + 1).Trim();
                    else if (line3.IndexOf("Scale:", StringComparison.OrdinalIgnoreCase) >= 0) curScale = line3.Substring(line3.IndexOf(':') + 1).Trim();
                    else if (line3.IndexOf("Rotation:", StringComparison.OrdinalIgnoreCase) >= 0) curRotation = line3.Substring(line3.IndexOf(':') + 1).Trim();
                    }

                    if (curName is not null)
                    {
                        results.Add((curName, curEnabled, curConnected, curGeometry, curScale, curRotation));
                        Console.WriteLine($"[ADDED] {curName} Enabled:{curEnabled} Connected:{curConnected} Geometry:{curGeometry} Scale:{curScale} Rotation:{curRotation}");
                    }

                    Console.WriteLine($"[RESULTS COUNT]={results.Count}");
                    foreach (var r in results)
                    {
                        Console.WriteLine($"Display: {r.Name}, Enabled:{r.Enabled}, Connected:{r.Connected}, Geometry:{r.Geometry}, Scale:{r.Scale}, Rotation:{r.Rotation}");
                    }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to start kscreen-doctor: " + ex.Message);
            }

            try
            {
                var doctor = new KScreenDoctor();
                var outputs = doctor.GetOutputsAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                foreach (var o in outputs)
                {
                    Console.WriteLine($"Id:{o.Id} Name:{o.Name} Enabled:{o.Enabled} Connected:{o.Connected} Geometry:{o.PositionX},{o.PositionY} {o.CurrentWidth}x{o.CurrentHeight} Scale:{o.Scale} Rotation:{o.Rotation}");
                    foreach (var m in o.Modes)
                    {
                        Console.WriteLine($"  Mode: {m.Width}x{m.Height}@{m.RefreshRate} Current:{m.IsCurrent} Preferred:{m.IsPreferred}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Environment.Exit(1);
            }

            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args ?? []);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
