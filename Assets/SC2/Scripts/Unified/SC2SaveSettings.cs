using UnityEngine;

/// <summary>App-wide Save/Autosave/Backup preferences — PlayerPrefs-backed,
/// same pattern as every other cross-session setting in this project (see
/// SC2Hotkeys's own doc comment on why PlayerPrefs + JsonUtility/primitives
/// is "the" settings mechanism here, there being no project/EditorPrefs
/// available in a standalone build).
///
/// Deliberately compiled in both the Editor and standalone builds, and
/// deliberately NOT living inside SC2RuntimeTerrainPersistence (which is
/// Build-only): the autosave TIMER itself (SC2GameViewEditorUI.TickAutosave)
/// just calls SC2TerrainShell.RequestSave(), which already does the right
/// thing in both (AssetDatabase.SaveAssets in the Editor, the real JSON +
/// backup persistence in a build) — only the BACKUP ROTATION
/// (SC2RuntimeTerrainPersistence.CreateBackupSnapshot) is Build-only, since
/// that's the only place real save files exist on disk at all.</summary>
public static class SC2SaveSettings
{
    private const string AutosaveEnabledPrefKey = "SC2.Autosave.Enabled";
    private const string AutosaveMinutesPrefKey = "SC2.Autosave.IntervalMinutes";
    private const string BackupCountPrefKey = "SC2.Backups.MaxCount";
    private const string WriteLegacyJsonPrefKey = "SC2.Save.WriteLegacyJson";

    public const float DefaultAutosaveIntervalMinutes = 15f;
    public const int DefaultMaxBackupsPerMap = 2;
    private const float MinAutosaveIntervalMinutes = 1f;
    private const float MaxAutosaveIntervalMinutes = 120f;
    private const int MaxBackupsCap = 100;

    // -1/-1f = "not loaded from PlayerPrefs yet" — same lazy-static-field
    // pattern SC2GameViewEditorUI.ZoomSpeed already uses.
    private static int _autosaveEnabled = -1;
    private static float _autosaveIntervalMinutes = -1f;
    private static int _maxBackupsPerMap = -1;
    private static int _writeLegacyJson = -1;

    /// <summary>Whether a save (and every backup) also writes the legacy JSON copy of the whole map next to the document. On by
    /// default: it is the fallback when the document or one of its blobs cannot be read, and what older versions of the editor read.
    /// It is the largest file of a save (hundreds of megabytes on a big map), so it can be switched off once saves are trusted.</summary>
    public static bool WriteLegacyJson
    {
        get
        {
            if (_writeLegacyJson < 0) _writeLegacyJson = PlayerPrefs.GetInt(WriteLegacyJsonPrefKey, 1);
            return _writeLegacyJson != 0;
        }
        set
        {
            _writeLegacyJson = value ? 1 : 0;
            PlayerPrefs.SetInt(WriteLegacyJsonPrefKey, _writeLegacyJson);
            PlayerPrefs.Save();
        }
    }

    /// <summary>On by default (matches the requested "15 minutes by default,
    /// the user can configure or turn it off" — off is a deliberate choice,
    /// not the shipped default).</summary>
    public static bool AutosaveEnabled
    {
        get
        {
            if (_autosaveEnabled < 0) _autosaveEnabled = PlayerPrefs.GetInt(AutosaveEnabledPrefKey, 1);
            return _autosaveEnabled != 0;
        }
        set
        {
            _autosaveEnabled = value ? 1 : 0;
            PlayerPrefs.SetInt(AutosaveEnabledPrefKey, _autosaveEnabled);
            PlayerPrefs.Save();
        }
    }

    /// <summary>Minutes between autosaves. Kept separate from
    /// AutosaveEnabled (not "0 = disabled") so turning autosave off and back
    /// on remembers whatever interval the user had set.</summary>
    public static float AutosaveIntervalMinutes
    {
        get
        {
            if (_autosaveIntervalMinutes < 0f)
                _autosaveIntervalMinutes = Mathf.Clamp(PlayerPrefs.GetFloat(AutosaveMinutesPrefKey, DefaultAutosaveIntervalMinutes),
                    MinAutosaveIntervalMinutes, MaxAutosaveIntervalMinutes);
            return _autosaveIntervalMinutes;
        }
        set
        {
            _autosaveIntervalMinutes = Mathf.Clamp(value, MinAutosaveIntervalMinutes, MaxAutosaveIntervalMinutes);
            PlayerPrefs.SetFloat(AutosaveMinutesPrefKey, _autosaveIntervalMinutes);
            PlayerPrefs.Save();
        }
    }

    /// <summary>How many rotating backup snapshots
    /// SC2RuntimeTerrainPersistence.Save keeps per map (oldest pruned
    /// first); 0 disables backups entirely.</summary>
    public static int MaxBackupsPerMap
    {
        get
        {
            if (_maxBackupsPerMap < 0)
                _maxBackupsPerMap = Mathf.Clamp(PlayerPrefs.GetInt(BackupCountPrefKey, DefaultMaxBackupsPerMap), 0, MaxBackupsCap);
            return _maxBackupsPerMap;
        }
        set
        {
            _maxBackupsPerMap = Mathf.Clamp(value, 0, MaxBackupsCap);
            PlayerPrefs.SetInt(BackupCountPrefKey, _maxBackupsPerMap);
            PlayerPrefs.Save();
        }
    }
}
