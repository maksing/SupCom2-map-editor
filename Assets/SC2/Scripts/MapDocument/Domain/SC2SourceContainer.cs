using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SC2.MapEditor.Document
{
    public enum SC2SourceContainerKind
    {
        None,
        ZipArchive,
        Directory,
        /// <summary>SC2MultiSourceContainerReader below — a real game install
        /// splits one map's files across several sibling .scd archives
        /// (maps.scd/lua.scd/textures.scd/…), confirmed against a real
        /// installed copy: BDFs live in maps.scd, compiled per-map Lua lives
        /// in lua.scd, shared textures live in textures.scd. This kind marks
        /// "more than one underlying container, merged into one view."</summary>
        Composite
    }

    /// <summary>One entry as seen by a container reader — normalized path,
    /// not yet loaded into memory (see OpenEntry to stream it).</summary>
    public sealed class SC2SourceEntryInfo
    {
        public string NormalizedPath;
        public long UncompressedLength;
    }

    /// <summary>
    /// A uniform view over a
    /// packed .scd (zip) file or an unpacked "*.scd" directory, so the rest
    /// of the import pipeline never needs to know or check which one it's
    /// dealing with. Both implementations route every entry path through
    /// SC2SafePath, closing an unchecked zip-slip vulnerability present in the
    /// legacy SC2RuntimeScdImporter.
    /// </summary>
    public interface ISC2SourceContainerReader : IDisposable
    {
        SC2SourceContainerKind Kind { get; }
        string SourcePath { get; }
        IReadOnlyList<SC2SourceEntryInfo> Entries { get; }
        Stream OpenEntry(string normalizedPath);
    }

    public interface ISC2SourceContainerReaderFactory
    {
        ISC2SourceContainerReader Open(string scdFileOrDirectoryPath);
    }

    /// <summary>
    /// Decides ZipArchive vs Directory purely from what's on disk (a file
    /// vs. a directory whose name ends in ".scd", case-insensitive) —
    /// no scenario.lua is ever chosen by container
    /// name, only by explicit user selection among candidates found inside.
    /// </summary>
    public sealed class SC2SourceContainerReaderFactory : ISC2SourceContainerReaderFactory
    {
        public ISC2SourceContainerReader Open(string scdFileOrDirectoryPath)
        {
            if (string.IsNullOrEmpty(scdFileOrDirectoryPath))
                throw new ArgumentException("Path is required.", nameof(scdFileOrDirectoryPath));

            if (Directory.Exists(scdFileOrDirectoryPath))
                return new SC2DirectorySourceContainerReader(scdFileOrDirectoryPath);

            if (File.Exists(scdFileOrDirectoryPath))
                return new SC2ZipSourceContainerReader(scdFileOrDirectoryPath);

            throw new FileNotFoundException($"No such file or directory: '{scdFileOrDirectoryPath}'");
        }
    }

    public sealed class SC2ZipSourceContainerReader : ISC2SourceContainerReader
    {
        private readonly ZipArchive _archive;
        private readonly List<SC2SourceEntryInfo> _entries;
        private readonly Dictionary<string, ZipArchiveEntry> _byNormalizedPath;

        public SC2SourceContainerKind Kind => SC2SourceContainerKind.ZipArchive;
        public string SourcePath { get; }
        public IReadOnlyList<SC2SourceEntryInfo> Entries => _entries;

        public SC2ZipSourceContainerReader(string zipPath)
        {
            SourcePath = zipPath;
            _archive = ZipFile.OpenRead(zipPath);
            _entries = new List<SC2SourceEntryInfo>();
            _byNormalizedPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var entry in _archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry, not a file

                    // A malicious/corrupt zip entry name fails safe normalization
                    // here rather than later at extraction time — reject the
                    // whole archive rather than silently drop one entry, since a
                    // hostile entry name is a signal the archive itself isn't trustworthy.
                    var normalized = SC2SafePath.NormalizeRelativeOrThrow(entry.FullName);
                    _entries.Add(new SC2SourceEntryInfo { NormalizedPath = normalized, UncompressedLength = entry.Length });
                    _byNormalizedPath[normalized] = entry;
                }
            }
            catch
            {
                // Found while writing standalone tests for this file: a
                // rejected entry used to leave _archive's file handle open
                // forever, because the constructor never returns far enough
                // to hand a disposable reference back to the caller's `using`.
                // Exactly the untrusted-input path (zip-slip rejection) is
                // the one that must not leak a handle.
                _archive.Dispose();
                throw;
            }
        }

        public Stream OpenEntry(string normalizedPath)
        {
            var key = SC2SafePath.NormalizeRelativeOrThrow(normalizedPath);
            if (!_byNormalizedPath.TryGetValue(key, out var entry))
                throw new FileNotFoundException($"No such entry in archive '{SourcePath}': '{normalizedPath}'");
            return entry.Open();
        }

        public void Dispose() => _archive.Dispose();
    }

    public sealed class SC2DirectorySourceContainerReader : ISC2SourceContainerReader
    {
        private readonly string _root;
        private readonly List<SC2SourceEntryInfo> _entries;

        public SC2SourceContainerKind Kind => SC2SourceContainerKind.Directory;
        public string SourcePath { get; }
        public IReadOnlyList<SC2SourceEntryInfo> Entries => _entries;

        public SC2DirectorySourceContainerReader(string directoryPath)
        {
            SourcePath = directoryPath;
            _root = Path.GetFullPath(directoryPath);
            _entries = new List<SC2SourceEntryInfo>();

            // FollowSymlinks isn't a first-class .NET Framework/Standard
            // EnumerationOptions across every target here, so containment is
            // enforced the simple, portable way instead: every discovered
            // file's resolved full path is verified to still sit under the
            // root (SC2SafePath.ResolveContainedOrThrow) — a reparse point
            // pointing outside the root fails that check and is skipped
            // rather than silently followed.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(_root, file).Replace('\\', '/');
                string resolved;
                try
                {
                    resolved = SC2SafePath.ResolveContainedOrThrow(_root, relative);
                }
                catch (SC2UnsafePathException)
                {
                    continue; // reparse-point escape or similar — skip, don't fail the whole probe
                }

                var info = new FileInfo(resolved);
                _entries.Add(new SC2SourceEntryInfo { NormalizedPath = relative, UncompressedLength = info.Length });
            }
        }

        public Stream OpenEntry(string normalizedPath)
        {
            var resolved = SC2SafePath.ResolveContainedOrThrow(_root, normalizedPath);
            return File.OpenRead(resolved);
        }

        public void Dispose() { /* no unmanaged resources to release */ }
    }

    /// <summary>
    /// Unified "Import Map" pipeline — composite view over N sibling
    /// ISC2SourceContainerReader instances, so a real game install (one map's
    /// BDFs in maps.scd, its compiled _save/_scenario/_script.lua in a
    /// SEPARATE lua.scd, shared terrain textures in a THIRD textures.scd —
    /// confirmed against a real installed copy on disk, not hypothetical)
    /// can be searched as if it were one archive. Entries is the union of
    /// every wrapped reader's entries — first reader in the list wins on a
    /// path collision (the caller passes the primary/user-picked container
    /// first, siblings after, so an explicit pick always takes precedence
    /// over an auto-discovered sibling that happens to share a path).
    /// OpenEntry re-dispatches to whichever wrapped reader actually owns
    /// that path. Disposing this disposes every wrapped reader.
    /// </summary>
    public sealed class SC2MultiSourceContainerReader : ISC2SourceContainerReader
    {
        private readonly List<ISC2SourceContainerReader> _readers;
        private readonly List<SC2SourceEntryInfo> _entries;
        private readonly Dictionary<string, ISC2SourceContainerReader> _ownerByPath;

        public SC2SourceContainerKind Kind => SC2SourceContainerKind.Composite;
        public string SourcePath { get; }
        public IReadOnlyList<SC2SourceEntryInfo> Entries => _entries;

        /// <summary>Which archive (SourcePath) each wrapped reader represents —
        /// exposed for diagnostics/UI (e.g. "found in textures.scd").</summary>
        public IReadOnlyList<string> SourcePaths { get; }

        public SC2MultiSourceContainerReader(IEnumerable<ISC2SourceContainerReader> readers)
        {
            _readers = readers?.ToList() ?? new List<ISC2SourceContainerReader>();
            if (_readers.Count == 0)
                throw new ArgumentException("At least one reader is required.", nameof(readers));

            SourcePath = _readers[0].SourcePath;
            SourcePaths = _readers.Select(r => r.SourcePath).ToList();

            _entries = new List<SC2SourceEntryInfo>();
            _ownerByPath = new Dictionary<string, ISC2SourceContainerReader>(StringComparer.OrdinalIgnoreCase);
            foreach (var reader in _readers)
            {
                foreach (var entry in reader.Entries)
                {
                    if (_ownerByPath.ContainsKey(entry.NormalizedPath))
                        continue; // earlier (higher-priority) reader already claimed this path
                    _ownerByPath[entry.NormalizedPath] = reader;
                    _entries.Add(entry);
                }
            }
        }

        /// <summary>Which archive's SourcePath actually owns this path, or
        /// null if no wrapped reader has it — used by diagnostics/status text
        /// ("terrain.bdf found in maps.scd") without needing a full OpenEntry.</summary>
        public string FindOwnerSourcePath(string normalizedPath) =>
            _ownerByPath.TryGetValue(normalizedPath, out var owner) ? owner.SourcePath : null;

        public bool Contains(string normalizedPath) => _ownerByPath.ContainsKey(normalizedPath);

        public Stream OpenEntry(string normalizedPath)
        {
            if (_ownerByPath.TryGetValue(normalizedPath, out var owner))
                return owner.OpenEntry(normalizedPath);
            throw new FileNotFoundException($"No such entry across any scanned archive ({_readers.Count} scanned): '{normalizedPath}'");
        }

        public void Dispose()
        {
            foreach (var reader in _readers)
                reader.Dispose();
        }
    }

    /// <summary>
    /// Discovers sibling .scd archives/directories next to a user-picked
    /// path, for the "scan sibling archives" checklist — a real game install
    /// ships ~20 topic archives side by side in one gamedata/ folder (maps.scd,
    /// lua.scd, uncompiled_lua.scd, textures.scd, bp.scd, effects.scd, env.scd,
    /// meshes.scd, props.scd, units.scd, ui.scd, mods.scd, loc_*.scd, z_dlc*.scd
    /// — confirmed against a real installed copy). Only maps.scd/lua.scd/
    /// uncompiled_lua.scd/textures.scd are pre-checked by default (the ones a
    /// map import actually needs); everything else is discovered and shown,
    /// but left unchecked, so a DLC/mod archive can still be added by hand.
    /// </summary>
    public static class SC2SiblingArchiveDiscovery
    {
        private static readonly string[] DefaultCheckedStems = { "maps", "lua", "uncompiled_lua", "textures" };

        public sealed class SiblingArchive
        {
            public string Path;
            public string Stem;
            public bool DefaultChecked;
        }

        /// <summary>Shared with SC2GameInstallArchives.DiscoverArchives (the
        /// "scan a whole root folder" counterpart to FindSiblings below) so
        /// both agree on which stems are pre-checked without duplicating the
        /// list.</summary>
        public static bool IsDefaultCheckedStem(string stem) =>
            Array.Exists(DefaultCheckedStems, s => string.Equals(s, stem, StringComparison.OrdinalIgnoreCase));

        /// <summary>Scans pickedPath's containing folder for sibling entries
        /// that look like .scd archives — either a file named "*.scd" or a
        /// directory whose name ends in ".scd" (the same folder-vs-archive
        /// duality ISC2SourceContainerReaderFactory already handles for the
        /// primary pick). pickedPath itself is excluded from the result (the
        /// caller already treats it as the primary container, not a sibling).</summary>
        public static List<SiblingArchive> FindSiblings(string pickedPath)
        {
            var result = new List<SiblingArchive>();
            if (string.IsNullOrEmpty(pickedPath)) return result;

            string containingFolder;
            try
            {
                containingFolder = Directory.Exists(pickedPath)
                    ? Path.GetDirectoryName(pickedPath.TrimEnd('\\', '/'))
                    : Path.GetDirectoryName(pickedPath);
            }
            catch (ArgumentException) { return result; }

            if (string.IsNullOrEmpty(containingFolder) || !Directory.Exists(containingFolder))
                return result;

            var pickedFull = Path.GetFullPath(pickedPath.TrimEnd('\\', '/'));

            foreach (var entry in Directory.EnumerateFileSystemEntries(containingFolder))
            {
                var full = Path.GetFullPath(entry);
                if (string.Equals(full, pickedFull, StringComparison.OrdinalIgnoreCase))
                    continue; // the primary pick itself, not a sibling

                var isScdFile = File.Exists(entry) && string.Equals(Path.GetExtension(entry), ".scd", StringComparison.OrdinalIgnoreCase);
                var isScdDirectory = Directory.Exists(entry) && entry.TrimEnd('\\', '/').EndsWith(".scd", StringComparison.OrdinalIgnoreCase);
                if (!isScdFile && !isScdDirectory) continue;

                var stem = Path.GetFileNameWithoutExtension(entry.TrimEnd('\\', '/'));
                result.Add(new SiblingArchive
                {
                    Path = entry,
                    Stem = stem,
                    DefaultChecked = IsDefaultCheckedStem(stem)
                });
            }

            result.Sort((a, b) => string.Compare(a.Stem, b.Stem, StringComparison.OrdinalIgnoreCase));
            return result;
        }
    }
}
