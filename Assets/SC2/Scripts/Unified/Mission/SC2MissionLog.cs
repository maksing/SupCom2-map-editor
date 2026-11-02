using System;
using System.Collections.Generic;

/// <summary>The Mission Log of the mission editor: what was edited, compiled and exported, newest last. Kept in memory for the session.</summary>
public static class SC2MissionLog
{
    private const int MaxEntries = 300;
    private static readonly List<string> Lines = new();

    public static IReadOnlyList<string> Entries => Lines;

    public static void Add(string message)
    {
        Lines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        if (Lines.Count > MaxEntries) Lines.RemoveRange(0, Lines.Count - MaxEntries);
    }

    public static void Clear() => Lines.Clear();
}
