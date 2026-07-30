using System.Runtime.InteropServices;

namespace Quill;

/// A WinExe has no console, so anything it writes to stdout goes nowhere even
/// when it was launched from a terminal. Attaching to the parent's console gets
/// `quill doctor` and the dev harnesses their output back.
///
/// Reopening the streams afterwards is the part that's easy to miss: by the time
/// this runs, Console.Out is already bound to the null device it was given at
/// startup, so attaching alone changes nothing.
///
/// DllImport rather than the source-generated LibraryImport: the generator emits
/// unsafe code, and turning on AllowUnsafeBlocks project-wide is a poor trade for
/// one P/Invoke on a non-hot path.
internal static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    public static void Attach()
    {
        if (!AttachConsole(AttachParentProcess)) return;

        try
        {
            // The console defaults to the OEM code page, which turns doctor's
            // ✓/✗ and any accented path into mojibake.
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception)
        {
            // Redirected or otherwise unsettable — the text still gets through.
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
}
