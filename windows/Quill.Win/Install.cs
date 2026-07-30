using Microsoft.Win32;

namespace Quill;

/// Start quill at login.
///
/// The macOS build writes a LaunchAgent plist rather than using
/// SMAppService.mainApp, because that would demand a full .app bundle. The same
/// reasoning picks the Run key here over a scheduled task: one value under HKCU,
/// no elevation, no XML, and the same per-user scope a LaunchAgent has.
internal static class Install
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "quill";

    public static int Enable()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            Console.Error.WriteLine("couldn't determine the quill executable path");
            return 1;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        // Quoted: the path routinely contains spaces, and the Run key hands the
        // string to the shell verbatim.
        key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);

        Console.WriteLine("✓ launch-at-login installed");
        Console.WriteLine($"  key:    HKCU\\{RunKeyPath}\\{ValueName}");
        Console.WriteLine($"  binary: {exe}");

        // Registering a path inside a build output directory produces an entry
        // that silently stops working the moment the folder is cleaned.
        if (LooksLikeBuildOutput(exe))
        {
            Console.WriteLine();
            Console.WriteLine("! this is a build output path, which will break when the project is");
            Console.WriteLine("  rebuilt or cleaned. Copy quill.exe somewhere stable and re-run.");
        }
        return 0;
    }

    public static int Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is null)
        {
            Console.WriteLine($"nothing to remove (no {ValueName} value under HKCU\\{RunKeyPath})");
            return 0;
        }

        key.DeleteValue(ValueName);
        Console.WriteLine("✓ launch-at-login removed");
        return 0;
    }

    /// The path currently registered to start at login, or null.
    public static string? RegisteredPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) as string;
    }

    private static bool LooksLikeBuildOutput(string path) =>
        path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
        || path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
}
