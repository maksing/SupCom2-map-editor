using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The single "path to your game installation" setting. Replaces the
    /// separate, private root fields components used to keep for themselves
    /// (SC2UnitPropBrowser.GameDataRoot chief among them) with one shared
    /// place every archive-aware system in the editor reads. PlayerPrefs-
    /// backed - already confirmed elsewhere in this codebase to work
    /// identically in the Editor and a real Standalone Build, no
    /// #if UNITY_EDITOR needed.
    /// </summary>
    public static class SC2GameInstallSettings
    {
        private const string RootPreferenceKey = "SC2.GameInstall.RootPath";

        // Read only as a one-time migration fallback when the new key has
        // never been set - a user who already configured
        // SC2UnitPropBrowser's own root shouldn't have to reconfigure it
        // just because the setting moved to one shared place.
        private const string LegacyUnitPropBrowserRootKey = "SC2.UnitPropBrowser.GameDataRoot";

        public static string RootPath
        {
            get
            {
                // The legacy fallback must only ever apply before this
                // setting has been touched at all - gating on HasKey (not
                // just "is the value empty") so an explicit clear (e.g. the
                // startup screen's "Change game path..." button, which
                // writes "" here) stays cleared instead of immediately
                // resurrecting the old SC2UnitPropBrowser path on the very
                // next read.
                if (!PlayerPrefs.HasKey(RootPreferenceKey))
                    return PlayerPrefs.GetString(LegacyUnitPropBrowserRootKey, string.Empty);
                return PlayerPrefs.GetString(RootPreferenceKey, string.Empty);
            }
            set
            {
                PlayerPrefs.SetString(RootPreferenceKey, value ?? string.Empty);
                PlayerPrefs.Save();
            }
        }

        public static bool IsConfigured => !string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath);
    }

    /// <summary>
    /// The missing "scan a whole root folder for every .scd in it" counterpart
    /// to SC2SiblingArchiveDiscovery.FindSiblings (which needs a primary file
    /// already picked, then only scans ITS containing folder). Reuses the same
    /// well-known-stem classification so a game-install root
    /// (gamedata/maps.scd, gamedata/lua.scd, gamedata/textures.scd, ... all
    /// side by side - confirmed against a real installed copy this session)
    /// yields the identical SiblingArchive shape the Import Map panel already
    /// knows how to render as a checklist.
    /// </summary>
    public static class SC2GameInstallArchives
    {
        public static List<SC2SiblingArchiveDiscovery.SiblingArchive> DiscoverArchives(string rootPath)
        {
            var result = new List<SC2SiblingArchiveDiscovery.SiblingArchive>();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return result;

            foreach (var entry in Directory.EnumerateFileSystemEntries(rootPath))
            {
                var isScdFile = File.Exists(entry) && string.Equals(Path.GetExtension(entry), ".scd", StringComparison.OrdinalIgnoreCase);
                var isScdDirectory = Directory.Exists(entry) && entry.TrimEnd('\\', '/').EndsWith(".scd", StringComparison.OrdinalIgnoreCase);
                if (!isScdFile && !isScdDirectory) continue;

                var stem = Path.GetFileNameWithoutExtension(entry.TrimEnd('\\', '/'));
                result.Add(new SC2SiblingArchiveDiscovery.SiblingArchive
                {
                    Path = entry,
                    Stem = stem,
                    DefaultChecked = SC2SiblingArchiveDiscovery.IsDefaultCheckedStem(stem)
                });
            }

            result.Sort((a, b) => string.Compare(a.Stem, b.Stem, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }

    /// <summary>
    /// Owns the one live composite reader over every archive discovered under
    /// SC2GameInstallSettings.RootPath - opened once (a .scd's central-
    /// directory listing is cheap to index even for a large archive,
    /// confirmed empirically this session; only actual entry decompression,
    /// via OpenEntry, is deferred) and reused by map discovery, texture
    /// resolution, and blueprint discovery alike instead of each maintaining
    /// its own separately-opened reader. Every discovered archive is opened
    /// (not just the "default checked" subset that the Import Map sibling
    /// checklist uses) - this registry has no manual per-archive opt-out UI,
    /// and later consumers (e.g. Units/Props blueprint discovery) need
    /// archives the map-import checklist doesn't pre-check by default, such
    /// as bp.scd/props.scd/units.scd.
    /// </summary>
    public static class SC2GameArchiveRegistry
    {
        public readonly struct OpenFailure
        {
            public readonly string Path;
            public readonly string Error;
            public OpenFailure(string path, string error) { Path = path; Error = error; }
        }

        private static ISC2SourceContainerReader _reader;
        private static string _openedRootPath;
        private static List<OpenFailure> _lastOpenFailures = new List<OpenFailure>();

        /// <summary>Archives found under the configured root that failed to
        /// open on the most recent (re)open — e.g. a community map-pack .scd
        /// built with a zip variant .NET's ZipArchive can't read (LZMA/BZip2
        /// compression, ZIP64 quirks, etc.). Previously these were only
        /// logged via Debug.LogWarning, invisible to a user who never checks
        /// the console/Player.log - surfaced here so the startup screen can
        /// show a real, actionable message instead of silently dropping that
        /// archive's maps.</summary>
        public static IReadOnlyList<OpenFailure> LastOpenFailures => _lastOpenFailures;

        public static bool TryGetReader(out ISC2SourceContainerReader reader)
        {
            EnsureOpen();
            reader = _reader;
            return reader != null;
        }

        /// <summary>Forces a fresh open against the currently configured root -
        /// call after the user changes SC2GameInstallSettings.RootPath.</summary>
        public static void Reopen()
        {
            Close();
            EnsureOpen();
        }

        public static void Close()
        {
            _reader?.Dispose();
            _reader = null;
            _openedRootPath = null;
            _lastOpenFailures = new List<OpenFailure>();
        }

        private static void EnsureOpen()
        {
            var root = SC2GameInstallSettings.RootPath;
            if (_reader != null && string.Equals(_openedRootPath, root, StringComparison.OrdinalIgnoreCase))
                return;

            Close();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

            var archives = SC2GameInstallArchives.DiscoverArchives(root);
            if (archives.Count == 0) return;

            var factory = new SC2SourceContainerReaderFactory();
            var readers = new List<ISC2SourceContainerReader>();
            var failures = new List<OpenFailure>();
            foreach (var archive in archives)
            {
                try { readers.Add(factory.Open(archive.Path)); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[SC2 Game Install] Skipping archive '{archive.Path}': {ex.Message}");
                    failures.Add(new OpenFailure(archive.Path, ex.Message));
                }
            }
            _lastOpenFailures = failures;
            if (readers.Count == 0) return;

            _reader = readers.Count == 1 ? readers[0] : new SC2MultiSourceContainerReader(readers);
            _openedRootPath = root;
        }
    }
}
