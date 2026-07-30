using System.Reflection;
using System.Windows.Forms;

namespace Quill;

/// Command line, mirroring the macOS build's ArgumentParser surface: `run` is the
/// default subcommand, plus `doctor` and `install`.
///
/// Parsed by hand rather than with System.CommandLine, which is still
/// 3.0.0-preview. Three commands and three flags do not justify a preview
/// dependency in a project whose whole shape is "one binary, few dependencies".
internal static class Cli
{
    public static int Run(string[] args)
    {
        // A WinExe writes to nowhere unless it borrows the launching terminal's
        // console. Harmless when there isn't one (started from the Run key).
        ConsoleBridge.Attach();

        var command = args.Length > 0 ? args[0] : "run";

        // `quill --out D:\Recordings` with no subcommand, as on macOS where run
        // is the default subcommand.
        if (command.StartsWith('-') && command is not ("-h" or "--help" or "--version"))
        {
            return RunDaemon(args);
        }

        switch (command)
        {
            case "-h" or "--help" or "help":
                PrintHelp();
                return 0;

            case "--version" or "version":
                Console.WriteLine($"quill (windows) {Version()}");
                return 0;

            case "run":
                return RunDaemon(args);

            case "doctor":
            {
                var checks = DoctorReport.Run(Config.ResolveRoot(OptionValue(args, "--out")));
                DoctorReport.Print(checks);
                if (Install.RegisteredPath() is { } registered)
                {
                    Console.WriteLine($"✓ launch at login: {registered}");
                }
                return DoctorReport.AllOk(checks) ? 0 : 1;
            }

            case "install":
                return InstallCommand(args);

            default:
                if (DevCommands.Handles(command))
                {
                    return DevCommands.RunAsync(args).GetAwaiter().GetResult();
                }
                Console.Error.WriteLine($"unknown command \"{command}\"");
                Console.Error.WriteLine();
                PrintHelp();
                return 64;
        }
    }

    private static int InstallCommand(string[] args)
    {
        var enable = args.Contains("--launch-at-login");
        var uninstall = args.Contains("--uninstall");

        if (enable == uninstall)
        {
            Console.Error.WriteLine("specify exactly one of --launch-at-login or --uninstall");
            return 64;
        }
        return uninstall ? Install.Disable() : Install.Enable();
    }

    private static int RunDaemon(string[] args)
    {
        var root = Config.ResolveRoot(OptionValue(args, "--out"));

        // Non-blocking in spirit: permissions and models resolve later, so
        // warnings at startup are informational. Hard failures are not — there is
        // no point putting an icon in the tray that cannot record.
        var checks = DoctorReport.Run(root);
        if (!DoctorReport.AllOk(checks))
        {
            Console.Error.WriteLine("startup checks failed:");
            DoctorReport.Print(checks);
            return 1;
        }

        ApplicationConfiguration.Initialize();
        using var controller = new AppController(root);

        Console.Error.WriteLine($"quill up · recordings → {root} · quit from the tray icon");
        Application.Run();
        return 0;
    }

    private static string? OptionValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        return null;
    }

    private static string Version() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private static void PrintHelp()
    {
        Console.WriteLine("""
        quill — local meeting recorder + transcriber.

        Records mic and system audio as two tracks, then transcribes on-device.

        USAGE
          quill                            run the tray daemon (default)
          quill run [--out <dir>]          same, with a custom recordings root
          quill doctor                     check devices, permissions, models
          quill install --launch-at-login  start quill at login
          quill install --uninstall        stop starting quill at login
          quill --version

        DEV HARNESSES
          quill status                     resolved config and cached models
          quill record <seconds>           capture a test session, then transcribe
          quill transcribe <dir>           re-run the queue on a session folder
          quill gaptest                    loopback silence-ledger acceptance check
          quill bench <wav> [ref.txt]      time every transcription model
          quill vadtest <speech.wav>       silence-skipping timeline + speed check
          quill devicetest                 survive-a-device-change check
          quill icons <dir>                write the tray icons out as PNGs

        CONFIG
          %APPDATA%\quill\config.json, or ~/.config/quill/config.json.
          QUILL_CONFIG overrides both.
        """);
    }
}
