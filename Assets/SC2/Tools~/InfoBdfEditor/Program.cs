using System.Windows.Forms;

namespace SC2InfoBdfEditorApp;

internal static class Program
{
    // Diagnostic-only log, next to the exe, so a real unhandled exception's
    // full text (including inner exceptions) can be inspected without
    // depending on Windows Event Log timing/provider-name guesswork —
    // needed to confirm whether IncludeNativeLibrariesForSelfExtract
    // actually fixes a DllNotFoundException from the
    // published exe.
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "crash.log");

    [STAThread]
    private static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
        Application.ThreadException += (_, e) =>
            LogCrash(e.Exception, "Application.ThreadException");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var editor = new InfoBdfEditorUi();
            var form = new MainForm(editor.Draw);
            editor.Host = form;
            Application.Run(form);
        }
        catch (Exception ex)
        {
            LogCrash(ex, "Main try/catch");
            throw;
        }
    }

    private static void LogCrash(Exception? ex, string source)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {source}\n{ex}\n\n");
        }
        catch
        {
            // Logging must never itself throw during crash handling.
        }
    }
}
