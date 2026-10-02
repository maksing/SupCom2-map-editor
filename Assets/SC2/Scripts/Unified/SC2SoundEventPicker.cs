using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SC2.MapEditor.Document;
using UnityEngine;

/// <summary>
/// The game's own list of sound events a map's Sound markers can play: every event in the install's
/// FMOD event files (sounds/win/fmod/*.fev) whose path starts with
/// "SC2/SC2/Environments/" — the ambience events the vanilla maps use
/// ("SC2/SC2/Environments/SC2_MP_001/snd_SC2_MP_001_Generic_Ocean"). Read lazily the first time a Sound
/// marker is inspected and kept until the game folder changes.
/// </summary>
public static class SC2SoundEventCatalog
{
    public const string EnvironmentPrefix = "SC2/SC2/Environments/";

    private static string _loadedFor;
    private static List<string> _events = new List<string>();
    private static string _error;

    /// <summary>Why the list is empty (no game folder, no .fev files, unreadable file), or null.</summary>
    public static string Error => _error;

    public static IReadOnlyList<string> EnvironmentEvents
    {
        get
        {
            var root = SC2GameInstallSettings.RootPath ?? string.Empty;
            if (!string.Equals(_loadedFor, root, StringComparison.OrdinalIgnoreCase)) Load(root);
            return _events;
        }
    }

    /// <summary>Forces the next access to read the .fev files again.</summary>
    public static void Invalidate() => _loadedFor = null;

    private static void Load(string root)
    {
        _loadedFor = root;
        _events = new List<string>();
        _error = null;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _error = "The game data folder is not configured (set it on the startup screen).";
            return;
        }

        var folder = FindFmodFolder(root);
        if (folder == null)
        {
            _error = "No sounds/win/fmod folder was found next to the game data folder.";
            return;
        }

        var found = new List<string>();
        var problems = new List<string>();
        foreach (var file in Directory.GetFiles(folder, "*.fev"))
        {
            try
            {
                foreach (var name in SC2FevEventNames.Read(File.ReadAllBytes(file)))
                    if (name.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase)) found.Add(name);
            }
            catch (Exception exception)
            {
                problems.Add(Path.GetFileName(file) + ": " + exception.Message);
            }
        }

        _events = found.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        if (_events.Count == 0)
            _error = problems.Count > 0 ? "Could not read the FMOD event files. " + string.Join(" ", problems) : "No Environments events were found in the .fev files in " + folder + ".";
    }

    private static string FindFmodFolder(string root)
    {
        // The configured root is the game's gamedata folder; sounds/ sits beside it.
        var candidates = new List<string>();
        var parent = Directory.GetParent(root.TrimEnd('/', '\\'));
        if (parent != null) candidates.Add(Path.Combine(parent.FullName, "sounds", "win", "fmod"));
        candidates.Add(Path.Combine(root, "sounds", "win", "fmod"));
        return candidates.FirstOrDefault(Directory.Exists);
    }
}

/// <summary>
/// The Sound marker's event chooser in the marker inspector: a searchable list of the game's Environments
/// events (SC2SoundEventCatalog) that writes the marker's "EventName" field, which _save.lua stores as
/// <c>['EventName'] = STRING( 'SC2/SC2/Environments/...' )</c>.
/// </summary>
public sealed class SC2SoundEventPicker
{
    private const string EventNameKey = "EventName";

    private string _search = string.Empty;
    private Vector2 _scroll;

    /// <summary>The event name the marker currently carries, or an empty string.</summary>
    public static string GetEventName(SC2ObjMarker marker)
    {
        foreach (var field in marker.ExtraFields)
        {
            if (!string.Equals(field.Key, EventNameKey, StringComparison.OrdinalIgnoreCase)) continue;
            var raw = field.RawValue ?? string.Empty;
            var open = raw.IndexOf('\'');
            var close = raw.LastIndexOf('\'');
            return open >= 0 && close > open ? raw.Substring(open + 1, close - open - 1) : string.Empty;
        }
        return string.Empty;
    }

    /// <summary>Sets (or, with an empty name, removes) the marker's EventName field.</summary>
    public static void SetEventName(SC2ObjMarker marker, string eventName)
    {
        for (var i = 0; i < marker.ExtraFields.Count; i++)
        {
            if (!string.Equals(marker.ExtraFields[i].Key, EventNameKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(eventName)) marker.ExtraFields.RemoveAt(i);
            else marker.ExtraFields[i] = new SC2LuaField { Key = EventNameKey, RawValue = ToRaw(eventName) };
            return;
        }
        if (!string.IsNullOrEmpty(eventName))
            marker.ExtraFields.Add(new SC2LuaField { Key = EventNameKey, RawValue = ToRaw(eventName) });
    }

    private static string ToRaw(string eventName) => "STRING( '" + eventName + "' )";

    /// <summary>Draws the chooser. <paramref name="beforeChange"/> runs right before the marker is modified
    /// (for the caller's undo checkpoint); the result is true when the marker was changed.</summary>
    public bool Draw(SC2ObjMarker marker, float width, Action beforeChange)
    {
        SC2EnvFieldGui.SectionLabel("Sound event");
        var current = GetEventName(marker);
        SC2EnvFieldGui.WrapLabel(current.Length == 0 ? "No event selected - this marker is silent in the game." : current, width);

        var events = SC2SoundEventCatalog.EnvironmentEvents;
        if (events.Count == 0)
        {
            SC2EnvFieldGui.WrapLabel(SC2SoundEventCatalog.Error ?? "No sound events are available.", width);
            return false;
        }
        if (current.Length > 0 && !events.Contains(current))
            SC2EnvFieldGui.WrapLabel("This event is not in the game's Environments list (SC2.fev); it is kept as it is.", width);

        var changed = false;
        if (current.Length > 0 && SC2EnvFieldGui.Button("Clear event", GUILayout.Height(SC2UiScale.Pxf(18f))))
        {
            beforeChange?.Invoke();
            SetEventName(marker, string.Empty);
            changed = true;
        }

        _search = SC2EnvFieldGui.TextField("Search events", _search, "mki_sndsearch");
        var query = (_search ?? string.Empty).Trim();
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(SC2UiScale.Pxf(160f)));
        foreach (var name in events)
        {
            if (query.Length > 0 && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var shortName = name.Substring(SC2SoundEventCatalog.EnvironmentPrefix.Length);
            var label = string.Equals(name, current, StringComparison.Ordinal) ? "> " + shortName : shortName;
            if (!SC2EnvFieldGui.Button(label, GUILayout.Height(SC2UiScale.Pxf(18f)))) continue;
            beforeChange?.Invoke();
            SetEventName(marker, name);
            changed = true;
        }
        GUILayout.EndScrollView();
        return changed;
    }
}
