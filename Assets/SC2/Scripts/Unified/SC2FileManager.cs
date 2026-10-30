using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

/// <summary>Shows a file or a folder in the system's file manager (Explorer, Finder), in the editor and in a build alike: the File
/// menu's "Show Terrains Folder" and "Show Logs Folder".</summary>
public static class SC2FileManager
{
    /// <summary>Shows <paramref name="path"/>: a file is selected in its folder, a folder is opened. False when the path does not
    /// exist or the file manager could not be started (the reason goes to the log).</summary>
    public static bool Reveal(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var isFile = File.Exists(path);
            if (!isFile && !Directory.Exists(path)) return false;
            var full = Path.GetFullPath(path);
#if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(full);
#elif UNITY_STANDALONE_WIN
            Process.Start(new ProcessStartInfo("explorer.exe", isFile ? $"/select,\"{full}\"" : $"\"{full}\"") { UseShellExecute = false });
#elif UNITY_STANDALONE_OSX
            Process.Start(new ProcessStartInfo("open", isFile ? $"-R \"{full}\"" : $"\"{full}\"") { UseShellExecute = false });
#else
            Application.OpenURL("file://" + (isFile ? Path.GetDirectoryName(full) : full));
#endif
            return true;
        }
        catch (Exception exception)
        {
            SC2Log.LogWarning($"[SC2FileManager] Could not show '{path}': {exception.Message}");
            return false;
        }
    }

    /// <summary>The folder the saved terrains live in (created when it does not exist yet, so there is always something to show).</summary>
    public static string TerrainsFolder()
    {
        var folder = SC2SaveStorage.SaveDirectory;
        try { Directory.CreateDirectory(folder); } catch (Exception) { /* Reveal reports a folder that is still missing */ }
        return folder;
    }

    /// <summary>The log file, flushed so it holds everything so far; its folder when the log was never started.</summary>
    public static string LogTarget()
    {
        SC2Log.Flush();
        var file = SC2Log.LogPath;
        if (!string.IsNullOrEmpty(file) && File.Exists(file)) return file;
        var root = Directory.GetParent(Application.dataPath);
        return root != null ? Path.Combine(root.FullName, "Logs") : null;
    }
}
