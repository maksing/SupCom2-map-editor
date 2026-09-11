using System;
using System.IO;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Centralized logger — mirrors every message to both Unity Console and a log file.
/// Log file: <project>/Logs/SC2TerrainEditor.log (project root, outside Assets).
/// AutoFlush=true so a crash/hang doesn't lose the trailing log lines — critical for
/// diagnosing exactly where a long operation (e.g. mesh simplification, export) got to
/// before Unity became unresponsive.
/// </summary>
public static class SC2Log
{
    private static StreamWriter _writer;
    private static string _logPath;
    private static readonly object _lock = new();

    public static string LogPath => _logPath;

    private static void EnsureWriter()
    {
        if (_writer != null) return;
        lock (_lock)
        {
            if (_writer != null) return;
            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
                var logDir = Path.Combine(projectRoot, "Logs");
                Directory.CreateDirectory(logDir);
                _logPath = Path.Combine(logDir, "SC2TerrainEditor.log");
                _writer = new StreamWriter(_logPath, append: false, Encoding.UTF8) { AutoFlush = true };
                _writer.WriteLine($"=== SC2 Terrain Editor log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[SC2Log] Could not open log file: {e.Message}");
            }
        }
    }

    private static void WriteLine(string level, string message)
    {
        EnsureWriter();
        if (_writer == null) return;
        lock (_lock)
        {
            try { _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}"); }
            catch { }
        }
    }

    public static void Log(string message)        { UnityEngine.Debug.Log(message);        WriteLine("INFO", message); }
    public static void LogWarning(string message)  { UnityEngine.Debug.LogWarning(message);  WriteLine("WARN", message); }
    public static void LogError(string message)    { UnityEngine.Debug.LogError(message);    WriteLine("ERROR", message); }
    public static void LogException(Exception ex)  { UnityEngine.Debug.LogException(ex);     WriteLine("EXCEPTION", ex.ToString()); }

    public static void Flush() { lock (_lock) { try { _writer?.Flush(); } catch { } } }
    public static void Close() { lock (_lock) { try { _writer?.Flush(); _writer?.Dispose(); } catch { } _writer = null; } }

#if UNITY_EDITOR
    [MenuItem("SC2/Open Log File")]
    private static void OpenLogFile()
    {
        EnsureWriter(); Flush();
        if (!string.IsNullOrEmpty(_logPath) && File.Exists(_logPath))
            EditorUtility.OpenWithDefaultApp(_logPath);
        else
            UnityEngine.Debug.LogWarning("[SC2Log] No log file yet.");
    }

    [MenuItem("SC2/Reveal Log Folder")]
    private static void RevealLogFolder()
    {
        EnsureWriter(); Flush();
        if (!string.IsNullOrEmpty(_logPath)) EditorUtility.RevealInFinder(_logPath);
    }

    [InitializeOnLoadMethod]
    private static void RegisterFlushHooks()
    {
        AssemblyReloadEvents.beforeAssemblyReload += Flush;
        EditorApplication.quitting += Close;
    }
#endif
}
