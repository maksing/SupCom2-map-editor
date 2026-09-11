using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BdfIO.Core;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The shared Editor/Build
    /// import service. Reads directly from ISC2SourceContainerReader
    /// streams; never extracts a .scd to an arbitrary filesystem temp
    /// directory the way the legacy SC2RuntimeScdImporter/Editor importer do —
    /// that's both the zip-slip surface SC2SafePath
    /// was written to close and unnecessary I/O once entries can just be
    /// streamed.
    ///
    /// Scope actually covered here: Identity/Source/EntryPoint/Archive, with
    /// real container reading, scenario probing and per-entry classification
    /// into the selected map's closure vs. ForeignMapEntry. Deliberately NOT
    /// covered: parsing terrain/hfield/etc. BDF
    /// bytes into typed domain documents, or building any preview scene,
    /// explicitly a separate later step once a domain has something to
    /// render from a SC2MapDocument instead of legacy SC2TerrainData.
    ///
    /// Known, deliberately out-of-scope limitation, found while building this
    /// (not a hypothetical): real vanilla-shipped scenario.lua files are
    /// compiled Lua bytecode (signature bytes 0x1B 'L' 'u' 'a' — confirmed
    /// against the actual gamedata.scd corpus), not the plain-text
    /// `field = 'value'` form SC2ScdEntryPointResolver's regex-based
    /// extraction understands. This service can only read PLAIN-TEXT
    /// scenario.lua — which is exactly what this editor's own exporter
    /// writes (SC2ObjectsExport.RegenerateScenarioLua), so round-tripping a
    /// map this editor produced works; opening an original untouched vanilla
    /// .scd does not yet. A compiled scenario.lua is detected and reported as
    /// a diagnosed SC2ValidationSeverity.Error candidate (CanImport=false),
    /// never silently mis-parsed. Decoding that bytecode format is a real,
    /// separate undertaking left for later.
    /// </summary>
    public sealed class SC2MapImportService : ISC2MapImportService
    {
        private readonly ISC2SourceContainerReaderFactory _readerFactory;
        private readonly ISC2BlobStore _blobStore;

        private static readonly byte[] LuaBytecodeSignature = { 0x1B, (byte)'L', (byte)'u', (byte)'a' };

        private static readonly string[] BdfKindSuffixes =
        {
            "terrain", "hfield", "costs", "collision2", "mapobjs", "info", "decals"
        };

        public SC2MapImportService(ISC2SourceContainerReaderFactory readerFactory, ISC2BlobStore blobStore)
        {
            _readerFactory = readerFactory ?? throw new ArgumentNullException(nameof(readerFactory));
            _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        }

        public Task<SC2ContainerProbeResult> ProbeScdAsync(
            string scdFileOrDirectoryPath,
            IProgress<SC2OperationProgress> progress,
            CancellationToken cancellationToken)
            => Task.Run(() => ProbeScd(scdFileOrDirectoryPath, progress, cancellationToken), cancellationToken);

        public Task<SC2MapImportResult> ImportSelectedScenarioAsync(
            SC2ContainerProbeResult probe,
            string selectedScenarioEntryPath,
            SC2ImportOptions options,
            IProgress<SC2OperationProgress> progress,
            CancellationToken cancellationToken)
            => Task.Run(() => ImportSelectedScenario(
                probe ?? throw new ArgumentNullException(nameof(probe)),
                selectedScenarioEntryPath,
                options ?? new SC2ImportOptions(),
                progress,
                cancellationToken), cancellationToken);

        // ---- Probe ----

        private SC2ContainerProbeResult ProbeScd(
            string path,
            IProgress<SC2OperationProgress> progress,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path is required.", nameof(path));

            var diagnostics = new SC2ValidationReport();
            progress?.Report(new SC2OperationProgress { Stage = "Opening container", Progress01 = 0f });

            if (Directory.Exists(path) && !path.TrimEnd('\\', '/').EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(SC2ValidationSeverity.Warning, "source-not-scd-named", null, null,
                    $"Source directory '{path}' does not end in '.scd'. Importing it anyway.");
            }

            using var reader = _readerFactory.Open(path);
            ct.ThrowIfCancellationRequested();

            var scenarioEntries = reader.Entries
                .Where(e => e.NormalizedPath.EndsWith("scenario.lua", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (scenarioEntries.Count == 0)
            {
                diagnostics.Add(SC2ValidationSeverity.Error, "no-scenario-lua", null, null,
                    $"No *scenario.lua entry found in '{path}'. A container with no scenario.lua has no importable map.");
            }

            var candidates = new List<SC2ScenarioCandidate>();
            var candidateIndex = 0;
            foreach (var entry in scenarioEntries)
            {
                ct.ThrowIfCancellationRequested();
                candidates.Add(ProbeScenarioCandidate(reader, entry));
                candidateIndex++;
                progress?.Report(new SC2OperationProgress
                {
                    Stage = "Probing scenario candidates",
                    Progress01 = scenarioEntries.Count == 0 ? 1f : (float)candidateIndex / scenarioEntries.Count,
                    Message = entry.NormalizedPath,
                });
            }

            return new SC2ContainerProbeResult
            {
                SourcePath = path,
                ContainerKind = reader.Kind,
                SourceFingerprint = SC2SourceCatalogFingerprint.Compute(reader.Entries),
                ScenarioCandidates = candidates,
                Diagnostics = diagnostics,
            };
        }

        private SC2ScenarioCandidate ProbeScenarioCandidate(ISC2SourceContainerReader reader, SC2SourceEntryInfo entry)
        {
            var candidate = new SC2ScenarioCandidate { ScenarioEntryPath = entry.NormalizedPath };

            byte[] bytes;
            using (var stream = reader.OpenEntry(entry.NormalizedPath))
                bytes = ReadAllBytes(stream);

            if (IsCompiledLuaBytecode(bytes))
            {
                candidate.CanImport = false;
                candidate.Messages.Add(new SC2ValidationMessage
                {
                    Severity = SC2ValidationSeverity.Error,
                    Code = "compiled-lua-unsupported",
                    Message = $"'{entry.NormalizedPath}' is compiled Lua bytecode. This importer only reads " +
                              "plain-text scenario.lua (the form this editor's own exporter writes); reading " +
                              "vanilla shipped bytecode scenario files isn't supported yet.",
                });
                return candidate;
            }

            var text = Encoding.UTF8.GetString(bytes);
            var scenario = SC2ScdEntryPointResolver.ParseScenarioPaths(text);
            var displayName = SC2ScdEntryPointResolver.ExtractField(text, "name");
            var bdfStem = SC2ScdEntryPointResolver.ResolveMapStem(scenario.Map);

            candidate.DisplayName = string.IsNullOrEmpty(displayName) ? null : displayName;
            candidate.MapPath = scenario.Map;
            candidate.SavePath = scenario.Save;
            candidate.ScriptPath = scenario.Script;
            candidate.BdfStem = bdfStem;

            if (string.IsNullOrEmpty(scenario.Map))
            {
                candidate.Messages.Add(Error("missing-map-field", $"'{entry.NormalizedPath}' has no 'map' field."));
            }
            else if (string.IsNullOrEmpty(bdfStem))
            {
                candidate.Messages.Add(Error("unresolvable-bdf-stem",
                    $"Could not derive a BDF stem from '{entry.NormalizedPath}''s map field ('{scenario.Map}')."));
            }
            else
            {
                string bdfDirectory;
                try
                {
                    bdfDirectory = GetNormalizedDirectory(scenario.Map);
                }
                catch (SC2UnsafePathException ex)
                {
                    candidate.Messages.Add(Error("unsafe-map-path", $"'{entry.NormalizedPath}''s map field is unsafe: {ex.Message}"));
                    candidate.CanImport = false;
                    return candidate;
                }

                var terrainBdfPath = CombineNormalized(bdfDirectory, $"{bdfStem}.terrain.win.bdf");
                var hfieldBdfPath = CombineNormalized(bdfDirectory, $"{bdfStem}.hfield.win.bdf");

                if (!ContainsPath(reader, terrainBdfPath))
                    candidate.Messages.Add(Error("missing-terrain-bdf", $"Required '{terrainBdfPath}' not found in container."));
                if (!ContainsPath(reader, hfieldBdfPath))
                    candidate.Messages.Add(Error("missing-hfield-bdf", $"Required '{hfieldBdfPath}' not found in container."));
            }

            candidate.CanImport = candidate.Messages.All(m => m.Severity != SC2ValidationSeverity.Error);
            return candidate;
        }

        // ---- Import ----

        private SC2MapImportResult ImportSelectedScenario(
            SC2ContainerProbeResult probe,
            string selectedScenarioEntryPath,
            SC2ImportOptions options,
            IProgress<SC2OperationProgress> progress,
            CancellationToken ct)
        {
            var diagnostics = new SC2ValidationReport();

            var selected = probe.ScenarioCandidates.FirstOrDefault(
                c => string.Equals(c.ScenarioEntryPath, selectedScenarioEntryPath, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
                throw new InvalidOperationException(
                    $"'{selectedScenarioEntryPath}' is not one of the scenario candidates in the given probe result.");
            if (!selected.CanImport)
                throw new InvalidOperationException(
                    $"'{selectedScenarioEntryPath}' cannot be imported: " +
                    string.Join("; ", selected.Messages.Select(m => m.Message)));

            progress?.Report(new SC2OperationProgress { Stage = "Re-opening container", Progress01 = 0f });
            using var reader = _readerFactory.Open(probe.SourcePath);
            ct.ThrowIfCancellationRequested();

            var currentFingerprint = SC2SourceCatalogFingerprint.Compute(reader.Entries);
            if (!string.Equals(currentFingerprint, probe.SourceFingerprint, StringComparison.Ordinal))
            {
                diagnostics.Add(SC2ValidationSeverity.Warning, "source-changed-since-probe", null, null,
                    $"'{probe.SourcePath}' changed since Probe ran. Importing against its current contents; " +
                    "re-run Probe if the scenario candidate list may now be different.");
            }

            var bdfDirectory = GetNormalizedDirectory(selected.MapPath);
            var bdfStem = selected.BdfStem;

            var document = new SC2MapDocument();
            document.Identity.DisplayName = selected.DisplayName ?? bdfStem ?? "Untitled";
            document.Identity.MapStem = bdfStem ?? "";

            document.Source.Kind = probe.ContainerKind == SC2SourceContainerKind.ZipArchive
                ? SC2MapSourceKind.ScdArchiveFile
                : SC2MapSourceKind.ScdDirectory;
            document.Source.ContainerKind = probe.ContainerKind;
            document.Source.OriginalPath = probe.SourcePath;
            document.Source.OriginalFileName = Path.GetFileName(probe.SourcePath.TrimEnd('\\', '/'));
            document.Source.OriginalFingerprint = currentFingerprint;
            document.Source.ImportedUtc = DateTime.UtcNow;
            document.Source.OriginalStillAvailable = true;

            document.EntryPoint.ScenarioEntryPath = selected.ScenarioEntryPath;
            document.EntryPoint.MapPath = selected.MapPath;
            document.EntryPoint.SavePath = selected.SavePath;
            document.EntryPoint.ScriptPath = selected.ScriptPath;
            document.EntryPoint.BdfDirectory = bdfDirectory;
            document.EntryPoint.BdfStem = bdfStem;
            document.EntryPoint.ResolutionMethod = "scenario.lua";

            using (var scenarioStream = reader.OpenEntry(selected.ScenarioEntryPath))
                document.EntryPoint.ScenarioEntrySha256 = ComputeSha256Hex(scenarioStream);

            var terrainBdfPath = CombineNormalized(bdfDirectory, $"{bdfStem}.terrain.win.bdf");
            document.EntryPoint.Platform = DetectPlatform(reader, terrainBdfPath);

            document.Archive.SourceContainerFingerprint = currentFingerprint;
            document.Archive.SourceContainerKind = probe.ContainerKind;
            document.Archive.SelectedScenarioEntryPath = selected.ScenarioEntryPath;

            var total = reader.Entries.Count;
            var processed = 0;

            foreach (var entryInfo in reader.Entries)
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(new SC2OperationProgress
                {
                    Stage = "Classifying entries",
                    Progress01 = total == 0 ? 1f : (float)processed / total,
                    Message = entryInfo.NormalizedPath,
                });

                // Belongs if it's the scenario entry itself, sits directly in
                // the map's own directory, or sits in a subdirectory nested
                // under it (real vanilla maps do nest files further, e.g.
                // 'maps/<STEM>/lighting/mask01.dds' for RNM masks — a bare
                // directory-equality check would wrongly file that under
                // ForeignMapEntry). A bdfDirectory-startswith prefix match
                // alone would be wrong for a root-level map (bdfDirectory ==
                // "", where every path "starts with" the empty prefix), so
                // that case is handled by the exact-match branch only —
                // a known, narrow limitation for the (real-world rare)
                // root-level-map layout rather than a general rule.
                var entryDir = GetNormalizedDirectory(entryInfo.NormalizedPath);
                var belongsToSelectedMap =
                    string.Equals(entryInfo.NormalizedPath, selected.ScenarioEntryPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entryDir, bdfDirectory, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(bdfDirectory) &&
                     entryDir.StartsWith(bdfDirectory + "/", StringComparison.OrdinalIgnoreCase));

                if (!belongsToSelectedMap)
                {
                    document.Archive.Entries.Add(new SC2ArchiveEntry
                    {
                        OriginalPath = entryInfo.NormalizedPath,
                        NormalizedPath = entryInfo.NormalizedPath,
                        UncompressedLength = entryInfo.UncompressedLength,
                        Role = SC2ArchiveEntryRole.ForeignMapEntry,
                    });
                    continue;
                }

                SC2BlobReference blobRef;
                using (var entryStream = reader.OpenEntry(entryInfo.NormalizedPath))
                    blobRef = _blobStore.Put(entryStream);

                document.Archive.Entries.Add(new SC2ArchiveEntry
                {
                    OriginalPath = entryInfo.NormalizedPath,
                    NormalizedPath = entryInfo.NormalizedPath,
                    UncompressedLength = entryInfo.UncompressedLength,
                    Sha256 = blobRef.ContentHash,
                    Role = ClassifyRole(entryInfo.NormalizedPath, selected, bdfStem),
                    OriginalContent = blobRef,
                });
            }

            progress?.Report(new SC2OperationProgress { Stage = "Import complete", Progress01 = 1f });

            return new SC2MapImportResult { Document = document, Diagnostics = diagnostics };
        }

        private static SC2ArchiveEntryRole ClassifyRole(string normalizedPath, SC2ScenarioCandidate selected, string bdfStem)
        {
            if (string.Equals(normalizedPath, selected.ScenarioEntryPath, StringComparison.OrdinalIgnoreCase))
                return SC2ArchiveEntryRole.ScenarioLua;
            if (PathsEqual(normalizedPath, selected.SavePath))
                return SC2ArchiveEntryRole.SaveLua;
            if (PathsEqual(normalizedPath, selected.ScriptPath))
                return SC2ArchiveEntryRole.ScriptLua;

            if (!string.IsNullOrEmpty(bdfStem))
            {
                var fileName = GetFileName(normalizedPath);
                foreach (var kind in BdfKindSuffixes)
                {
                    if (!string.Equals(fileName, $"{bdfStem}.{kind}.win.bdf", StringComparison.OrdinalIgnoreCase))
                        continue;
                    return kind switch
                    {
                        "terrain" => SC2ArchiveEntryRole.TerrainBdf,
                        "hfield" => SC2ArchiveEntryRole.HfieldBdf,
                        "costs" => SC2ArchiveEntryRole.CostsBdf,
                        "collision2" => SC2ArchiveEntryRole.Collision2Bdf,
                        "mapobjs" => SC2ArchiveEntryRole.MapObjsBdf,
                        "info" => SC2ArchiveEntryRole.InfoBdf,
                        "decals" => SC2ArchiveEntryRole.DecalsBdf,
                        _ => SC2ArchiveEntryRole.Unknown,
                    };
                }
            }

            if (normalizedPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                return SC2ArchiveEntryRole.MapTexture;

            return SC2ArchiveEntryRole.CustomAsset;
        }

        private static bool PathsEqual(string normalizedPath, string rawFieldValue)
        {
            if (string.IsNullOrEmpty(rawFieldValue)) return false;
            try
            {
                return string.Equals(normalizedPath, SC2SafePath.NormalizeRelativeOrThrow(rawFieldValue), StringComparison.OrdinalIgnoreCase);
            }
            catch (SC2UnsafePathException)
            {
                return false;
            }
        }

        private static bool ContainsPath(ISC2SourceContainerReader reader, string normalizedPath)
            => reader.Entries.Any(e => string.Equals(e.NormalizedPath, normalizedPath, StringComparison.OrdinalIgnoreCase));

        private static BdfPlatform DetectPlatform(ISC2SourceContainerReader reader, string terrainBdfPath)
        {
            if (!ContainsPath(reader, terrainBdfPath))
                return BdfPlatform.Pc;

            using var stream = reader.OpenEntry(terrainBdfPath);
            Span<byte> header = stackalloc byte[4];
            var read = stream.Read(header);
            if (read < 4)
                return BdfPlatform.Pc;

            try
            {
                return BdfMagic.Detect(header);
            }
            catch (InvalidDataException)
            {
                // Recoverable: an unrecognized header byte
                // order shouldn't hard-fail the whole import — PC is the
                // reference/default platform this codebase actually
                // supports end-to-end (see BdfPlatform.cs's own doc comment:
                // Xbox is "theoretically supported... not verified against
                // real files").
                return BdfPlatform.Pc;
            }
        }

        private static string GetNormalizedDirectory(string mapField)
        {
            var normalized = SC2SafePath.NormalizeRelativeOrThrow(mapField);
            var lastSlash = normalized.LastIndexOf('/');
            return lastSlash >= 0 ? normalized.Substring(0, lastSlash) : "";
        }

        private static string GetFileName(string normalizedPath)
        {
            var lastSlash = normalizedPath.LastIndexOf('/');
            return lastSlash >= 0 ? normalizedPath.Substring(lastSlash + 1) : normalizedPath;
        }

        private static string CombineNormalized(string directory, string fileName)
            => string.IsNullOrEmpty(directory) ? fileName : directory + "/" + fileName;

        private static bool IsCompiledLuaBytecode(byte[] bytes)
        {
            if (bytes.Length < LuaBytecodeSignature.Length) return false;
            for (var i = 0; i < LuaBytecodeSignature.Length; i++)
                if (bytes[i] != LuaBytecodeSignature[i])
                    return false;
            return true;
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static string ComputeSha256Hex(Stream stream)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            return SC2Hex.ToLowerHex(hash);
        }

        private static SC2ValidationMessage Error(string code, string message) => new()
        {
            Severity = SC2ValidationSeverity.Error,
            Code = code,
            Message = message,
        };
    }
}
