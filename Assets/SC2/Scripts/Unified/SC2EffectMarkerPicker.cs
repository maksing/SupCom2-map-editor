using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SC2.MapEditor.Document;
using UnityEngine;

/// <summary>
/// The map-marker effects the game offers: the <c>EffectTemplates.Marker</c> table of its
/// lua/sim/EffectTemplates.lua (and the DLC1 copy, which adds more), read out of the game archives the first
/// time an Effect marker is inspected and kept until the archives change. The compiled file and the
/// uncompiled source are both understood; whichever the install has is used.
/// </summary>
public static class SC2EffectTemplateCatalog
{
    // Base game first, then DLC1, whose entries win on a name clash. Each group lists the compiled file before
    // the uncompiled source of the same file.
    private static readonly string[][] SourceGroups =
    {
        new[] { "lua/sim/EffectTemplates.lua", "uncompiled/lua/sim/EffectTemplates.lua" },
        new[] { "mods/DLC1/shadow/lua/sim/EffectTemplates.lua", "uncompiled/mods/DLC1/shadow/lua/sim/EffectTemplates.lua" },
    };

    private static ISC2SourceContainerReader _loadedFor;
    private static List<SC2MarkerEffectTemplate> _templates = new List<SC2MarkerEffectTemplate>();
    private static string _error;

    /// <summary>Why the list is empty (no game archives, file missing, unreadable file), or null.</summary>
    public static string Error => _error;

    public static IReadOnlyList<SC2MarkerEffectTemplate> Templates
    {
        get
        {
            if (!SC2GameArchiveRegistry.TryGetReader(out var reader))
            {
                _loadedFor = null;
                _templates = new List<SC2MarkerEffectTemplate>();
                _error = "The game archives are not available (set the game data folder on the startup screen).";
                return _templates;
            }
            if (!ReferenceEquals(reader, _loadedFor)) Load(reader);
            return _templates;
        }
    }

    public static bool Contains(string name) => Templates.Any(template => string.Equals(template.Name, name, StringComparison.Ordinal));

    private static void Load(ISC2SourceContainerReader reader)
    {
        _loadedFor = reader;
        _error = null;
        var merged = new Dictionary<string, SC2MarkerEffectTemplate>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in reader.Entries) paths.Add(entry.NormalizedPath);

        var problems = new List<string>();
        var foundAny = false;
        foreach (var group in SourceGroups)
        {
            var path = group.FirstOrDefault(paths.Contains);
            if (path == null) continue;
            foundAny = true;
            try
            {
                byte[] bytes;
                using (var stream = reader.OpenEntry(path))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }
                foreach (var template in SC2EffectTemplateNames.ReadMarkerTemplates(bytes)) merged[template.Name] = template;
            }
            catch (Exception exception)
            {
                problems.Add(path + ": " + exception.Message);
            }
        }

        _templates = merged.Values.OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (_templates.Count == 0)
            _error = !foundAny ? "lua/sim/EffectTemplates.lua was not found in the game archives." : "Could not read the effect templates. " + string.Join(" ", problems);
    }
}

/// <summary>
/// The Effect marker's chooser in the marker inspector: a searchable list of the game's marker effects plus the
/// scale and offset the game applies to them. It edits the marker's <c>EmitterTemplate</c>, <c>EmitterScale</c> and
/// <c>EmitterOffset</c> fields, which _save.lua stores exactly as the vanilla campaign maps do:
/// <c>['EmitterTemplate'] = STRING( 'C02Fog01' )</c>, <c>['EmitterScale'] = FLOAT( 1.0 )</c>,
/// <c>['EmitterOffset'] = VECTOR3( 0, 0, 0 )</c>. All three are always written together: the game reads
/// <c>EmitterOffset.x</c> without checking it exists, so a marker with a template but no offset would error.
/// </summary>
public sealed class SC2EffectMarkerPicker
{
    public const string TemplateKey = "EmitterTemplate";
    public const string ScaleKey = "EmitterScale";
    public const string OffsetKey = "EmitterOffset";

    /// <summary>The three fields a symmetric copy of an Effect marker shares with the original.</summary>
    public static readonly string[] FieldKeys = { TemplateKey, ScaleKey, OffsetKey };

    private string _search = string.Empty;
    private Vector2 _scroll;

    public static string GetTemplate(SC2ObjMarker marker)
    {
        var raw = FindRaw(marker, TemplateKey);
        if (raw == null) return string.Empty;
        var open = raw.IndexOf('\'');
        var close = raw.LastIndexOf('\'');
        return open >= 0 && close > open ? raw.Substring(open + 1, close - open - 1) : string.Empty;
    }

    public static float GetScale(SC2ObjMarker marker)
    {
        var numbers = ParseNumbers(FindRaw(marker, ScaleKey));
        return numbers.Count >= 1 ? numbers[0] : 1f;
    }

    public static Vector3 GetOffset(SC2ObjMarker marker)
    {
        var numbers = ParseNumbers(FindRaw(marker, OffsetKey));
        return numbers.Count >= 3 ? new Vector3(numbers[0], numbers[1], numbers[2]) : Vector3.zero;
    }

    /// <summary>Sets the template and makes sure the scale and offset fields exist next to it.</summary>
    public static void SetTemplate(SC2ObjMarker marker, string template)
    {
        SetRaw(marker, TemplateKey, "STRING( '" + template + "' )");
        EnsureScaleAndOffset(marker);
    }

    public static void SetScale(SC2ObjMarker marker, float scale) => SetRaw(marker, ScaleKey, FormatScale(scale));

    public static void SetOffset(SC2ObjMarker marker, Vector3 offset) => SetRaw(marker, OffsetKey, FormatOffset(offset));

    /// <summary>Adds EmitterScale = 1 and EmitterOffset = (0, 0, 0) when a marker does not have them yet.</summary>
    public static void EnsureScaleAndOffset(SC2ObjMarker marker)
    {
        if (FindRaw(marker, ScaleKey) == null) SetRaw(marker, ScaleKey, FormatScale(1f));
        if (FindRaw(marker, OffsetKey) == null) SetRaw(marker, OffsetKey, FormatOffset(Vector3.zero));
    }

    private static string FormatScale(float scale) => "FLOAT( " + scale.ToString("0.0###", CultureInfo.InvariantCulture) + " )";

    private static string FormatOffset(Vector3 offset) =>
        "VECTOR3( " + offset.x.ToString("0.###", CultureInfo.InvariantCulture) + ", " +
        offset.y.ToString("0.###", CultureInfo.InvariantCulture) + ", " +
        offset.z.ToString("0.###", CultureInfo.InvariantCulture) + " )";

    private static string FindRaw(SC2ObjMarker marker, string key)
    {
        foreach (var field in marker.ExtraFields)
            if (string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase)) return field.RawValue ?? string.Empty;
        return null;
    }

    private static void SetRaw(SC2ObjMarker marker, string key, string raw)
    {
        for (var i = 0; i < marker.ExtraFields.Count; i++)
        {
            if (!string.Equals(marker.ExtraFields[i].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
            marker.ExtraFields[i] = new SC2LuaField { Key = key, RawValue = raw };
            return;
        }
        marker.ExtraFields.Add(new SC2LuaField { Key = key, RawValue = raw });
    }

    private static List<float> ParseNumbers(string raw)
    {
        var result = new List<float>();
        if (string.IsNullOrEmpty(raw)) return result;
        var open = raw.IndexOf('(');
        var close = raw.LastIndexOf(')');
        if (open < 0 || close <= open) return result;
        foreach (var part in raw.Substring(open + 1, close - open - 1).Split(','))
            if (float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) result.Add(value);
        return result;
    }

    /// <summary>Draws the chooser. <paramref name="beforeChange"/> runs right before the marker is modified (for the
    /// caller's undo checkpoint); the result is true when the marker was changed.</summary>
    public bool Draw(SC2ObjMarker marker, float width, Action beforeChange)
    {
        SC2EnvFieldGui.SectionLabel("Effect");
        var current = GetTemplate(marker);
        SC2EnvFieldGui.WrapLabel(current.Length == 0 ? "No effect selected - this marker shows nothing in the game." : current, width);

        var templates = SC2EffectTemplateCatalog.Templates;
        if (templates.Count == 0)
        {
            SC2EnvFieldGui.WrapLabel(SC2EffectTemplateCatalog.Error ?? "No effect templates are available.", width);
            return false;
        }
        if (current.Length > 0 && !SC2EffectTemplateCatalog.Contains(current))
            SC2EnvFieldGui.WrapLabel("This effect is not in the game's EffectTemplates.Marker list; the game logs a warning and shows nothing for it.", width);

        var changed = false;
        _search = SC2EnvFieldGui.TextField("Search effects", _search, "mki_effsearch");
        var query = (_search ?? string.Empty).Trim();
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(SC2UiScale.Pxf(160f)));
        foreach (var template in templates)
        {
            if (query.Length > 0 && template.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var label = string.Equals(template.Name, current, StringComparison.Ordinal) ? "> " + template.Name : template.Name;
            if (!SC2EnvFieldGui.Button(label, GUILayout.Height(SC2UiScale.Pxf(18f)))) continue;
            beforeChange?.Invoke();
            SetTemplate(marker, template.Name);
            changed = true;
        }
        GUILayout.EndScrollView();

        var scale = GetScale(marker);
        var newScale = SC2EnvFieldGui.FloatField("Scale", scale, "mki_effscale");
        if (!Mathf.Approximately(newScale, scale))
        {
            beforeChange?.Invoke();
            SetScale(marker, newScale);
            changed = true;
        }
        var offset = GetOffset(marker);
        var newOffset = SC2EnvFieldGui.Vector3Field("Offset", offset, "mki_effoffset");
        if (newOffset != offset)
        {
            beforeChange?.Invoke();
            SetOffset(marker, newOffset);
            changed = true;
        }

        SC2EffectPreview.Enabled = SC2EnvFieldGui.Toggle("Show effects in the viewport", SC2EffectPreview.Enabled);
        if (SC2EffectPreview.Enabled)
        {
            SC2EnvFieldGui.WrapLabel("An approximate preview of the game's particles: it shows what the effect looks like and how big it is, not frame-exact motion.", width);
            if (!string.IsNullOrEmpty(SC2EffectPreview.Problem)) SC2EnvFieldGui.WrapLabel(SC2EffectPreview.Problem, width);
        }
        return changed;
    }
}
