using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>Runtime lookup + persistence for every remappable hotkey. Every
/// call site that used to compare a literal KeyCode/modifier against
/// Event.current or Input now asks this by the action's stable string id
/// instead, so a user rebind (SC2HotkeysPanel) takes effect everywhere that
/// action is checked. Persisted the same way every other cross-Editor/Build
/// setting in this project is (PlayerPrefs + JsonUtility) — there is no
/// EditorPrefs anywhere here since it has no Build equivalent.</summary>
public static class SC2Hotkeys
{
    private const string PrefKey = "SC2.Hotkeys.v1";

    // null = not loaded from PlayerPrefs yet, same lazy-static-field
    // sentinel pattern as SC2UiScale's own _scale = -1f.
    private static Dictionary<string, SC2HotkeyBinding[]> _current;

    /// <summary>Fired whenever any binding actually changes — the rebind
    /// panel and any cached tooltip text should repaint.</summary>
    public static event Action Changed;

    private static void EnsureLoaded()
    {
        if (_current != null) return;
        _current = new Dictionary<string, SC2HotkeyBinding[]>(SC2HotkeyActions.All.Length);
        foreach (var action in SC2HotkeyActions.All) _current[action.Id] = action.Defaults;

        var json = PlayerPrefs.GetString(PrefKey, "");
        if (string.IsNullOrEmpty(json)) return;

        HotkeysFile file = null;
        try { file = JsonUtility.FromJson<HotkeysFile>(json); }
        catch (Exception) { file = null; } // corrupt/foreign pref value — fall back to defaults entirely

        if (file?.Actions == null) return;
        foreach (var entry in file.Actions)
        {
            if (entry == null || string.IsNullOrEmpty(entry.ActionId)) continue;
            // Unknown/removed action id (e.g. a downgrade, or a future save
            // read by an older build) is silently dropped — the dictionary
            // was already seeded from current defaults above, so an action
            // added since this save was written simply keeps its default.
            if (SC2HotkeyActions.Find(entry.ActionId) == null) continue;
            if (entry.Bindings == null || entry.Bindings.Length == 0) continue;
            _current[entry.ActionId] = Array.ConvertAll(entry.Bindings, ToBinding);
        }
    }

    public static SC2HotkeyBinding[] GetBindings(string actionId)
    {
        EnsureLoaded();
        return _current.TryGetValue(actionId, out var bindings) ? bindings : Array.Empty<SC2HotkeyBinding>();
    }

    public static void SetBindings(string actionId, SC2HotkeyBinding[] bindings)
    {
        EnsureLoaded();
        _current[actionId] = bindings ?? Array.Empty<SC2HotkeyBinding>();
        Save();
        Changed?.Invoke();
    }

    public static void ResetToDefault(string actionId)
    {
        var action = SC2HotkeyActions.Find(actionId);
        if (action == null) return;
        SetBindings(actionId, action.Defaults);
    }

    public static void ResetAllToDefaults()
    {
        EnsureLoaded();
        foreach (var action in SC2HotkeyActions.All) _current[action.Id] = action.Defaults;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Event-driven discrete check — the majority of call sites
    /// (Undo, M, Ctrl+R, Delete, F resize-gesture, etc.). True if ANY of the
    /// action's current bindings matches this KeyDown event.</summary>
    public static bool Matches(string actionId, Event e)
    {
        var bindings = GetBindings(actionId);
        for (var i = 0; i < bindings.Length; i++)
            if (bindings[i].MatchesEvent(e)) return true;
        return false;
    }

    /// <summary>Modifier-only check (Shift/Ctrl/Alt-while-scrolling,
    /// Ctrl-while-painting-erase, additive-select). True if ANY of the
    /// action's current bindings' modifier flags are currently held.</summary>
    public static bool ModifierHeld(string actionId, Event e)
    {
        var bindings = GetBindings(actionId);
        for (var i = 0; i < bindings.Length; i++)
            if (bindings[i].ModifiersHeld(e)) return true;
        return false;
    }

    // Input.GetKey/-Down/-Up polling (WASDQE hold, Objects' E-hold
    // live-rotate-while-placing) — deliberately NOT routed through Matches,
    // which is Event-driven and one-shot; these three preserve the original
    // continuous polling semantics of their call sites.
    public static bool GetKey(string actionId) => AnyKey(actionId, Input.GetKey);
    public static bool GetKeyDown(string actionId) => AnyKey(actionId, Input.GetKeyDown);
    public static bool GetKeyUp(string actionId) => AnyKey(actionId, Input.GetKeyUp);

    private static bool AnyKey(string actionId, Func<KeyCode, bool> poll)
    {
        var bindings = GetBindings(actionId);
        for (var i = 0; i < bindings.Length; i++)
            if (!bindings[i].IsModifierOnly && poll(bindings[i].Key)) return true;
        return false;
    }

    public static string Label(string actionId)
    {
        var bindings = GetBindings(actionId);
        return bindings.Length > 0 ? string.Join(" / ", Array.ConvertAll(bindings, b => b.ToString())) : "(unbound)";
    }

    /// <summary>Every pair of distinct actions whose current bindings share
    /// an exact Key+modifier combination — informational only for the
    /// rebind panel. Never blocks a rebind: a same-key collision across two
    /// contexts that are never active at once (e.g. Decals' G with no
    /// Objects equivalent) is harmless by construction.</summary>
    public static List<(string ActionA, string ActionB)> FindConflicts()
    {
        EnsureLoaded();
        var result = new List<(string, string)>();
        var ids = new List<string>(_current.Keys);
        for (var i = 0; i < ids.Count; i++)
        {
            for (var j = i + 1; j < ids.Count; j++)
            {
                if (!AnyCollision(_current[ids[i]], _current[ids[j]])) continue;
                result.Add((ids[i], ids[j]));
            }
        }
        return result;
    }

    // Modifier-only bindings are deliberately excluded from collision
    // detection: a bare "Ctrl" is reused by design across several
    // independent contexts (Costs erase, Props-brush erase, additive
    // select) that are never active at once, so flagging every one of
    // those against each other would just be noise the very first time
    // the panel opens, with nothing for the user to actually fix.
    private static bool AnyCollision(SC2HotkeyBinding[] a, SC2HotkeyBinding[] b)
    {
        foreach (var ba in a)
        {
            if (ba.IsModifierOnly) continue;
            foreach (var bb in b)
            {
                if (bb.IsModifierOnly) continue;
                if (ba.CollidesWith(bb)) return true;
            }
        }
        return false;
    }

    private static void Save()
    {
        var file = new HotkeysFile
        {
            Version = 1,
            Actions = _current.Select(kv => new ActionBindingsDto
            {
                ActionId = kv.Key,
                Bindings = Array.ConvertAll(kv.Value, ToDto),
            }).ToArray(),
        };
        PlayerPrefs.SetString(PrefKey, JsonUtility.ToJson(file));
        PlayerPrefs.Save();
    }

    private static BindingDto ToDto(SC2HotkeyBinding b) => new() { Key = b.Key, Ctrl = b.Ctrl, Shift = b.Shift, Alt = b.Alt };
    private static SC2HotkeyBinding ToBinding(BindingDto d) => new(d.Key, d.Ctrl, d.Shift, d.Alt);

    [Serializable] private class BindingDto { public KeyCode Key; public bool Ctrl; public bool Shift; public bool Alt; }
    [Serializable] private class ActionBindingsDto { public string ActionId; public BindingDto[] Bindings; }
    [Serializable] private class HotkeysFile { public int Version = 1; public ActionBindingsDto[] Actions; }
}
