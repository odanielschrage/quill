namespace Quill;

internal static class Program
{
    /// STA because NotifyIcon, ContextMenuStrip and the shell interop behind them
    /// require it. This is also why the file isn't top-level statements: the
    /// generated entry point can't carry the attribute.
    [STAThread]
    private static int Main(string[] args) => Cli.Run(args);
}
