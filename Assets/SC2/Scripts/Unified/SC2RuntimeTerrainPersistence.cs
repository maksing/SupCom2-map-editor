#if !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using SC2.MapEditor.Document;

/// <summary>
/// Real disk persistence for SC2TerrainData in standalone Build &amp; Run.
///
/// AssetDatabase.SaveAssets()/CreateAsset() are Editor-only APIs. The
/// UnityEditorRuntimeShim versions used outside the Editor are no-ops (there is
/// no project/asset database in a player), so before this class existed
/// "Save Terrain" silently did nothing in a build — every edit lived only in
/// an in-memory ScriptableObject and was lost the moment the app closed, with
/// no way to reopen it next session.
///
/// This serializes the fields that actually define the terrain (heights,
/// per-triangle pair assignment, materials, pair metadata incl. RNM mask
/// bytes) to a JSON file under Application.persistentDataPath. Texture2D
/// references (material Diffuse/Normal/Mask) can't be embedded in JSON, so
/// only their name is stored and re-resolved on load via
/// Resources.FindObjectsOfTypeAll — the same best-effort lookup the runtime
/// texture picker overlay already relies on for standalone builds.
/// </summary>
public static class SC2RuntimeTerrainPersistence
{
    private const int FormatVersion = 2;

    // Application.persistentDataPath may only be read on the main thread; read once there (PrimeMainThreadState) so the paths below
    // also work from the autosave's worker thread.
    private static string _saveDirectory;

    public static string SaveDirectory => _saveDirectory ??= Path.Combine(Application.persistentDataPath, "SC2Terrains");

    private const string FileSuffix = ".sc2terrain.json";

    public static string GetSavePath(string mapName)
    {
        var safe = SanitizeFileName(string.IsNullOrWhiteSpace(mapName) ? "Untitled" : mapName);
        return Path.Combine(SaveDirectory, safe + FileSuffix);
    }

    public static List<string> ListSavedTerrains()
    {
        var result = new List<string>();
        if (!Directory.Exists(SaveDirectory))
            return result;

        EnsureSharePackagesInstalled();

        foreach (var file in Directory.GetFiles(SaveDirectory, "*" + FileSuffix))
        {
            var fileName = Path.GetFileName(file);
            result.Add(fileName.Substring(0, fileName.Length - FileSuffix.Length));
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>Auto-installs any "&lt;MapName&gt;.sc2mapshare" package
    /// sitting directly under SaveDirectory — the real-world case being a
    /// user extracting a zip someone sent them (or dropping in the whole
    /// folder Save now produces, see RefreshShareablePackage's own doc
    /// comment) straight into SC2Terrains without knowing they need to
    /// reach inside it first. Confirmed real: those files never showed up
    /// in the startup screen's map list, since ListSavedTerrains (and
    /// TryLoad, and the blob store) only ever look directly under
    /// SaveDirectory, never one level into a "*.sc2mapshare" subfolder.
    /// Copies the package's contents up to the exact top-level layout
    /// those already expect, so nothing else needs to know a share
    /// package exists at all — same reasoning as RefreshShareablePackage
    /// choosing that layout in the first place. Skips a map name that
    /// already has a real top-level save: an auto-install must never
    /// silently overwrite the user's own in-progress work just because a
    /// received package happens to share its name.</summary>
    private static void EnsureSharePackagesInstalled()
    {
        foreach (var shareDir in Directory.GetDirectories(SaveDirectory, "*.sc2mapshare"))
        {
            var mapName = Path.GetFileNameWithoutExtension(shareDir);
            var destJsonPath = GetSavePath(mapName);
            if (File.Exists(destJsonPath))
                continue;

            try
            {
                var sourceJsonPath = Path.Combine(shareDir, Path.GetFileName(destJsonPath));
                if (!File.Exists(sourceJsonPath))
                    continue; // not a complete/valid share package — nothing safe to install

                File.Copy(sourceJsonPath, destJsonPath, overwrite: false);

                var sourceManifestPath = Path.Combine(shareDir, Path.GetFileName(DocumentPackageDirectory(mapName)), "manifest.json");
                if (File.Exists(sourceManifestPath))
                {
                    var destManifestDir = DocumentPackageDirectory(mapName);
                    Directory.CreateDirectory(destManifestDir);
                    File.Copy(sourceManifestPath, Path.Combine(destManifestDir, "manifest.json"), overwrite: true);
                }

                var sourceBlobsDir = Path.Combine(shareDir, "Blobs");
                if (Directory.Exists(sourceBlobsDir))
                {
                    // Same "<first 2 hex chars>/<hash>.bin" shard layout
                    // SC2DirectoryBlobStore itself writes/reads — merged in
                    // directly rather than going through that class, since
                    // a blob already present under the same hash is by
                    // definition identical content (that's what content-
                    // addressing means) and simply left alone.
                    foreach (var shardDir in Directory.GetDirectories(sourceBlobsDir))
                    {
                        var destShardDir = Path.Combine(SaveDirectory, "Blobs", Path.GetFileName(shardDir));
                        Directory.CreateDirectory(destShardDir);
                        foreach (var srcFile in Directory.GetFiles(shardDir, "*.bin"))
                        {
                            var destFile = Path.Combine(destShardDir, Path.GetFileName(srcFile));
                            if (!File.Exists(destFile))
                                File.Copy(srcFile, destFile);
                        }
                    }
                }

                Debug.Log($"[SC2RuntimeTerrainPersistence] Installed shareable package for '{mapName}' from {shareDir}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SC2RuntimeTerrainPersistence] Failed to auto-install share package '{shareDir}': {ex}");
            }
        }
    }

    /// <summary>Writes via a temp file +
    /// atomic replace/move instead of a direct File.WriteAllText onto the
    /// final path. The old direct-write left a real window where a crash or
    /// power loss mid-write truncated the save file in place — this way,
    /// the final path either still holds the complete previous save or the
    /// complete new one, never a half-written blend of both. File.Replace
    /// is used (not just File.Move) when a previous save already exists,
    /// since File.Move alone throws IOException if the destination is
    /// already present — File.Replace atomically substitutes it instead
    /// (this class already assumes Windows elsewhere, e.g.
    /// SC2RuntimeFileDialog's native file/folder pickers, so relying on the
    /// Win32-backed ReplaceFile semantics here is consistent).</summary>
    // Confirmed real via a dedicated MapDocument save/restore + Undo/Redo
    // audit: the document-package dual-write below always rebuilt
    // doc.Decals from RawData's original imported decals.bdf bytes (or
    // nothing, for a from-scratch map) — never from the active
    // SC2MapSession.Document.Decals a shell may have spent a whole session
    // authoring (catalog entries, placements, symmetry groups). Save Terrain
    // silently discarded all of it. `session` is optional (this class is
    // deliberately session-agnostic by design, per its own original doc
    // comment) — when absent, behavior is unchanged from before.
    // Every stage below is logged through SC2Log, whose file writer is
    // AutoFlush=true — each line lands on disk the instant it's written, not
    // just when this method returns. A prior crash with no way to tell which
    // step of Save it happened during (nothing here logged anything at all —
    // unlike SC2TerrainExporter, whose own per-stage SC2Log coverage already
    // made THAT class of bug traceable) is exactly what this closes: whatever
    // the last logged "Save: ..." line in Logs/SC2TerrainEditor.log says is
    // the last stage that actually completed before whatever happened next.
    public static void Save(SC2TerrainData data, SC2MapSession session = null)
    {
        if (data == null) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save started. MapName='{data.MapName}'.");

        Directory.CreateDirectory(SaveDirectory);
        var dto = ToDto(data);
        var json = JsonUtility.ToJson(dto);
        SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: legacy JSON built ({sw.ElapsedMilliseconds}ms, {json.Length:N0} chars).");
        var finalPath = GetSavePath(data.MapName);
        var tempPath = finalPath + ".tmp";
        File.WriteAllText(tempPath, json, Encoding.UTF8);
        if (File.Exists(finalPath))
            File.Replace(tempPath, finalPath, null);
        else
            File.Move(tempPath, finalPath);
        SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: legacy JSON written to '{finalPath}' ({sw.ElapsedMilliseconds}ms).");

        SaveDocumentPackage(data, session, json);
        SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save finished in {sw.ElapsedMilliseconds}ms.");
    }

    /// <summary>Autosave's ONLY effect — writes a rotating backup snapshot
    /// built from the CURRENT in-memory data, without touching any of the
    /// canonical save files (the legacy .sc2terrain.json, manifest.json, or
    /// the shareable package) that Save() above writes. The map's
    /// "last saved state" on disk must only ever change via an explicit
    /// user Save — leaving the app open for an hour without pressing Save
    /// still lets the user recover exactly what was on disk at that last
    /// Save; the autosave timer only adds safety-net backups alongside it,
    /// it never becomes the thing "Save" itself silently was.</summary>
    public static bool CreateAutosaveBackup(SC2TerrainData data, SC2MapSession session)
    {
        if (data == null || SC2SaveSettings.MaxBackupsPerMap <= 0) return false;
        PrimeMainThreadState();
        return WriteAutosaveBackup(data, session, ToDto(data));
    }

    /// <summary>The background-thread form of <see cref="CreateAutosaveBackup"/>, for SC2BusyOverlay.RunInBackground. Call it on the
    /// main thread: it copies the data into the save DTO there (that reads texture names) and returns the function that writes the
    /// backup, which returns the action that reports the outcome to <paramref name="onFinished"/> on the main thread. Null when
    /// backups are off.</summary>
    public static Func<Action> BeginAutosaveBackup(SC2TerrainData data, SC2MapSession session, Action<bool> onFinished)
    {
        if (data == null || SC2SaveSettings.MaxBackupsPerMap <= 0) return null;
        PrimeMainThreadState();
        var dto = ToDto(data);
        return () =>
        {
            SC2BusyOverlay.Report("Writing the backup");
            var ok = WriteAutosaveBackup(data, session, dto);
            return () => onFinished?.Invoke(ok);
        };
    }

    // Reads, on the main thread, everything the backup needs that only the main thread may read (the save folder, the setting that
    // limits how many backups are kept), so that the writing itself can run on a worker.
    private static void PrimeMainThreadState()
    {
        _ = SaveDirectory;
        _ = SC2SaveSettings.MaxBackupsPerMap;
    }

    private static bool WriteAutosaveBackup(SC2TerrainData data, SC2MapSession session, Dto dto)
    {
        try
        {
            var json = JsonUtility.ToJson(dto);
            var blobStore = CreateBlobStore();
            var document = BuildDocumentSnapshot(data, session, blobStore);
            var manifestJson = SC2MapDocumentSerializer.Serialize(document);
            CreateBackupSnapshot(data.MapName, json, manifestJson, blobStore);
            return true;
        }
        // A UnityException is an API that only the main thread may call: let it out, so the busy overlay runs the backup there.
        catch (Exception ex) when (!(ex is UnityException))
        {
            SC2Log.LogWarning($"[SC2RuntimeTerrainPersistence] Autosave backup failed: {ex}");
            return false;
        }
    }

    /// <summary>Dual-write — builds a full
    /// SC2MapDocument snapshot from this same `data` (via the existing
    /// LegacyBridge.BuildFromLegacy projections, the same ones
    /// SC2TerrainExporter already reads a few fields back from) and writes
    /// it as manifest.json + blobs alongside the legacy JSON above. Load
    /// does NOT read this format yet — this stage exists only
    /// to prove the write path works and start accumulating real document
    /// packages before anything depends on reading them back. A failure
    /// here must never take down the legacy save that already succeeded
    /// above, hence the blanket catch.</summary>
    private static void SaveDocumentPackage(SC2TerrainData data, SC2MapSession session, string legacyJson)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var blobStore = CreateBlobStore();
            var document = BuildDocumentSnapshot(data, session, blobStore);
            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: document snapshot built ({sw.ElapsedMilliseconds}ms).");
            var manifestJson = SC2MapDocumentSerializer.Serialize(document);
            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: manifest serialized ({sw.ElapsedMilliseconds}ms, {manifestJson.Length:N0} chars).");

            var packageDir = DocumentPackageDirectory(data.MapName);
            Directory.CreateDirectory(packageDir);
            var manifestPath = Path.Combine(packageDir, "manifest.json");
            var manifestTempPath = manifestPath + ".tmp";
            File.WriteAllText(manifestTempPath, manifestJson, Encoding.UTF8);
            if (File.Exists(manifestPath))
                File.Replace(manifestTempPath, manifestPath, null);
            else
                File.Move(manifestTempPath, manifestPath);
            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: manifest written to '{manifestPath}' ({sw.ElapsedMilliseconds}ms).");

            CollectUnreferencedBlobs(blobStore);
            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: blob GC done ({sw.ElapsedMilliseconds}ms).");
            RefreshShareablePackage(data.MapName, legacyJson, manifestJson, blobStore);
            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: shareable package refreshed ({sw.ElapsedMilliseconds}ms).");
            CreateBackupSnapshot(data.MapName, legacyJson, manifestJson, blobStore);
            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: backup snapshot step done ({sw.ElapsedMilliseconds}ms).");
        }
        catch (Exception ex)
        {
            SC2Log.LogWarning("[SC2RuntimeTerrainPersistence] Document package dual-write failed " +
                              $"(legacy JSON save above still succeeded): {ex}");
        }
    }

    /// <summary>Mirrors this one map's own files — the legacy
    /// .sc2terrain.json, its manifest.json, and only the blobs its manifest
    /// actually references (out of the pool shared by every OTHER saved map
    /// too, see SC2BlobStoreGarbageCollector's own doc comment on why that
    /// pool can't just be shipped wholesale) — into a single self-contained
    /// "&lt;MapName&gt;.sc2mapshare" folder the user can zip and hand to
    /// someone else. Deliberately the SAME relative layout as SaveDirectory
    /// itself (&lt;MapName&gt;.sc2terrain.json + &lt;MapName&gt;.sc2mapdoc/
    /// manifest.json + Blobs/&lt;hash[0:2]&gt;/&lt;hash&gt;.bin), so
    /// "installing" a received
    /// package back on another machine is just copying its contents
    /// straight into that machine's own SaveDirectory — no new load code
    /// needed, TryLoad already reads exactly this layout. Rebuilt fresh
    /// (old copy deleted first) on every save rather than merged into,
    /// so a blob a since-edited map no longer references doesn't linger
    /// in the package forever. Runs inside SaveDocumentPackage's own
    /// try/catch — this is a convenience mirror of an already-successful
    /// save, never allowed to make the save itself look like it failed.</summary>
    private static void RefreshShareablePackage(string mapName, string legacyJson, string manifestJson, ISC2BlobStore blobStore)
    {
        var shareDir = SharePackageDirectory(mapName);
        if (Directory.Exists(shareDir))
            Directory.Delete(shareDir, recursive: true);
        WriteSelfContainedPackage(shareDir, mapName, legacyJson, manifestJson, blobStore);
        SC2Log.Log($"[SC2RuntimeTerrainPersistence] Shareable package refreshed: {shareDir}");
    }

    /// <summary>Sibling of DocumentPackageDirectory — see
    /// RefreshShareablePackage's own doc comment for what lives here.</summary>
    public static string SharePackageDirectory(string mapName) =>
        Path.Combine(SaveDirectory, SanitizeFileName(string.IsNullOrWhiteSpace(mapName) ? "Untitled" : mapName) + ".sc2mapshare");

    /// <summary>Shared by RefreshShareablePackage and CreateBackupSnapshot —
    /// writes a complete, self-contained copy of one map's save (the legacy
    /// .sc2terrain.json + its manifest.json + only the blobs that manifest
    /// actually references) into `destDir`, in the same relative layout
    /// SaveDirectory itself uses. Self-contained on purpose: a backup must
    /// keep working even after the shared blob pool's own GC (see
    /// CollectUnreferencedBlobs) has long since swept whatever the CURRENT
    /// save stopped referencing.</summary>
    private static void WriteSelfContainedPackage(string destDir, string mapName, string legacyJson, string manifestJson, ISC2BlobStore blobStore)
    {
        Directory.CreateDirectory(destDir);

        var savePath = GetSavePath(mapName);
        File.WriteAllText(Path.Combine(destDir, Path.GetFileName(savePath)), legacyJson, Encoding.UTF8);

        var manifestDir = Path.Combine(destDir, Path.GetFileName(DocumentPackageDirectory(mapName)));
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(Path.Combine(manifestDir, "manifest.json"), manifestJson, Encoding.UTF8);

        foreach (var hash in SC2BlobStoreGarbageCollector.CollectReferencedHashes(manifestJson))
        {
            if (!blobStore.Contains(hash)) continue; // referenced but somehow missing from the store — nothing to copy
            var blobDestDir = Path.Combine(destDir, "Blobs", hash.Substring(0, 2));
            Directory.CreateDirectory(blobDestDir);
            using var src = blobStore.OpenRead(hash);
            using var dest = File.Create(Path.Combine(blobDestDir, hash + ".bin"));
            src.CopyTo(dest);
        }
    }

    // ==================================================================
    // Backups — a rotating history of self-contained snapshots per map
    // (see WriteSelfContainedPackage), independent of the shared blob
    // pool's own GC, so a corrupted/lost current save can be recovered from
    // whatever was last known-good. Count is configurable via
    // SC2SaveSettings.MaxBackupsPerMap (0 disables backups outright).
    // ==================================================================

    // Local time, filesystem-safe, and lexically sortable in chronological
    // order — the exact same string IS both the folder name (the backup's
    // id) and its human-readable label in the "Restore Backup..." menu, so
    // no separate id<->label mapping is needed anywhere that lists them.
    private const string BackupStampFormat = "yyyy-MM-dd_HH-mm-ss";

    public static string BackupsDirectory(string mapName) =>
        Path.Combine(SaveDirectory, "Backups", SanitizeFileName(string.IsNullOrWhiteSpace(mapName) ? "Untitled" : mapName));

    /// <summary>Called from SaveDocumentPackage's own try/catch (a real
    /// Save) and from CreateAutosaveBackup's (the autosave timer, which
    /// never touches the canonical save files at all) — either way a
    /// backup failure must never make its caller look like it failed,
    /// same reasoning as every other step in SaveDocumentPackage.</summary>
    private static void CreateBackupSnapshot(string mapName, string legacyJson, string manifestJson, ISC2BlobStore blobStore)
    {
        if (SC2SaveSettings.MaxBackupsPerMap <= 0) return;
        try
        {
            var stamp = DateTime.Now.ToString(BackupStampFormat);
            var backupDir = Path.Combine(BackupsDirectory(mapName), stamp);
            // Two saves within the same second (autosave racing a manual Ctrl+S,
            // say) would otherwise collide on the same folder name.
            var suffix = 1;
            while (Directory.Exists(backupDir))
                backupDir = Path.Combine(BackupsDirectory(mapName), $"{stamp}_{suffix++}");

            WriteSelfContainedPackage(backupDir, mapName, legacyJson, manifestJson, blobStore);
            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Backup snapshot created: {backupDir}");
            PruneOldBackups(mapName);
        }
        catch (Exception ex)
        {
            SC2Log.LogWarning($"[SC2RuntimeTerrainPersistence] Backup snapshot failed (save itself still succeeded): {ex}");
        }
    }

    private static void PruneOldBackups(string mapName)
    {
        var dir = BackupsDirectory(mapName);
        if (!Directory.Exists(dir)) return;
        var entries = Directory.GetDirectories(dir);
        // Folder names are the BackupStampFormat timestamp — ordinal string
        // order already matches chronological order.
        Array.Sort(entries, StringComparer.Ordinal);
        var excess = entries.Length - SC2SaveSettings.MaxBackupsPerMap;
        for (var i = 0; i < excess; i++)
        {
            try { Directory.Delete(entries[i], recursive: true); }
            catch (Exception ex) { SC2Log.LogWarning($"[SC2RuntimeTerrainPersistence] Could not prune old backup '{entries[i]}': {ex.Message}"); }
        }
    }

    /// <summary>Newest first — what the "Restore Backup..." dropdown lists.</summary>
    public static List<string> ListBackups(string mapName)
    {
        var result = new List<string>();
        var dir = BackupsDirectory(mapName);
        if (!Directory.Exists(dir)) return result;
        foreach (var entry in Directory.GetDirectories(dir))
            result.Add(Path.GetFileName(entry));
        result.Sort(StringComparer.Ordinal);
        result.Reverse();
        return result;
    }

    /// <summary>Copies one backup snapshot's files back over the live save
    /// (legacy JSON + manifest.json + its blobs merged back into the shared
    /// pool — Put() is content-addressed, so re-adding a blob that's already
    /// there is a safe no-op). The caller is expected to reload the map
    /// afterward (e.g. LoadFromDisk) to bring the in-memory state in line
    /// with what was just restored on disk.</summary>
    public static bool RestoreBackup(string mapName, string backupId)
    {
        var backupDir = Path.Combine(BackupsDirectory(mapName), backupId);
        var backupJson = Path.Combine(backupDir, Path.GetFileName(GetSavePath(mapName)));
        if (!File.Exists(backupJson))
        {
            SC2Log.LogWarning($"[SC2RuntimeTerrainPersistence] RestoreBackup: no backup json at '{backupJson}'.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(SaveDirectory);
            File.Copy(backupJson, GetSavePath(mapName), overwrite: true);

            var backupManifest = Path.Combine(backupDir, Path.GetFileName(DocumentPackageDirectory(mapName)), "manifest.json");
            if (File.Exists(backupManifest))
            {
                var packageDir = DocumentPackageDirectory(mapName);
                Directory.CreateDirectory(packageDir);
                File.Copy(backupManifest, Path.Combine(packageDir, "manifest.json"), overwrite: true);

                var backupBlobsDir = Path.Combine(backupDir, "Blobs");
                if (Directory.Exists(backupBlobsDir))
                {
                    var blobStore = CreateBlobStore();
                    foreach (var shardDir in Directory.GetDirectories(backupBlobsDir))
                        foreach (var blobFile in Directory.GetFiles(shardDir, "*.bin"))
                            using (var s = File.OpenRead(blobFile))
                                blobStore.Put(s);
                }
            }

            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Restored backup '{backupId}' for '{mapName}'.");
            return true;
        }
        catch (Exception ex)
        {
            SC2Log.LogException(ex);
            return false;
        }
    }

    /// <summary>Runs after every successful
    /// document-package write, scoped inside the SAME try/catch as the
    /// write itself: a GC bug must never break Save. Reads every OTHER
    /// saved map's manifest.json too (the blob store is one pool shared
    /// across every *.sc2mapdoc directory under SaveDirectory, not one
    /// store per map — see SC2BlobStoreGarbageCollector's own doc comment
    /// for why "just this map's references" would be wrong), so a blob
    /// only ever gets deleted once NOTHING currently saved anywhere still
    /// points at it.</summary>
    private static void CollectUnreferencedBlobs(ISC2BlobStore blobStore)
    {
        if (!Directory.Exists(SaveDirectory))
            return;

        var manifestJsons = new List<string>();
        foreach (var packageDir in Directory.GetDirectories(SaveDirectory, "*.sc2mapdoc"))
        {
            var manifestPath = Path.Combine(packageDir, "manifest.json");
            if (File.Exists(manifestPath))
                manifestJsons.Add(File.ReadAllText(manifestPath, Encoding.UTF8));
        }

        var deleted = SC2BlobStoreGarbageCollector.CollectUnreferencedBlobs(blobStore, manifestJsons);
        if (deleted > 0)
            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Blob GC: deleted {deleted} unreferenced blob(s).");
    }

    /// <summary>Same root SC2MapSession.BlobStore already uses
    /// (SC2GameViewEditorUI.OnEnable) — consolidated to one
    /// shared, content-addressed store; constructing a second ISC2BlobStore
    /// instance pointed at the same directory is safe (it's stateless
    /// besides the directory itself) and avoids this Editor-agnostic file
    /// needing a live SC2MapSession reference just to save.</summary>
    private static ISC2BlobStore CreateBlobStore() =>
        new SC2DirectoryBlobStore(Path.Combine(SaveDirectory, "Blobs"));

    public static string DocumentPackageDirectory(string mapName) =>
        Path.Combine(SaveDirectory, SanitizeFileName(string.IsNullOrWhiteSpace(mapName) ? "Untitled" : mapName) + ".sc2mapdoc");

    /// <summary>Builds a full SC2MapDocument snapshot from live SC2TerrainData
    /// using every domain's existing BuildFromLegacy projection. Scenario/
    /// Decals need raw bytes SC2TerrainData itself doesn't carry (there is
    /// no "original scenario.lua" for a from-scratch procedural map at
    /// all) — sourced from data.RawData when the map was originally
    /// imported, left at each bridge's own honest default (State=Missing)
    /// otherwise, same as every other "imported map passthrough" fallback
    /// already used throughout this codebase (e.g. SC2TerrainExporter.
    /// BuildInfoModel's info.bdf fallback).</summary>
    private static SC2MapDocument BuildDocumentSnapshot(SC2TerrainData data, SC2MapSession session, ISC2BlobStore blobStore)
    {
        var doc = new SC2MapDocument();
        doc.Identity.DisplayName = data.MapName ?? "";
        doc.Identity.MapStem = data.MapName ?? "";

        doc.Terrain = SC2TerrainLegacyBridge.BuildFromLegacy(data, blobStore);
        // BuildFromLegacy has no concept of imported terrain.bdf objects at
        // all (they live entirely outside SC2TerrainData, in
        // SC2GameViewEditorUI._importedTerrains) - it always returns an
        // empty ImportedEntries list. SyncImportedTerrainsToDocument (called
        // via SC2TerrainShell.PreSaveSync, right before this method runs)
        // already wrote the real, current list into the ACTIVE session's
        // own document - copy it across onto this freshly-built `doc`
        // before it gets serialized, or every imported terrain silently
        // vanishes on the very next load. Same bug class, same fix shape,
        // as the Decals passthrough right below (confirmed real by the user
        // reloading a saved map with imported terrain and finding it gone).
        if (session?.Document?.Terrain?.ImportedEntries != null)
        {
            doc.Terrain.ImportedEntries = new List<SC2ImportedTerrainEntryDocument>(session.Document.Terrain.ImportedEntries.Count);
            foreach (var e in session.Document.Terrain.ImportedEntries)
                doc.Terrain.ImportedEntries.Add(e.Clone());
        }
        // Same passthrough for the imported-layer RNM masks (Lighting Bake >
        // Imported Objects): they exist only on the session's document, the
        // fresh BuildFromLegacy above knows nothing about them.
        if (session?.Document?.Terrain?.ImportedRnm != null)
            doc.Terrain.ImportedRnm = new SC2RnmDocument { MaskSets = session.Document.Terrain.ImportedRnm.CloneMaskSets() };
        doc.Hfield = SC2HfieldLegacyBridge.BuildFromLegacy(data, blobStore);
        doc.Costs = SC2CostsLegacyBridge.BuildFromLegacy(data, blobStore);
        doc.Collision2 = SC2CollisionLegacyBridge.BuildFromLegacy(data);
        doc.Objects = SC2ObjectsLegacyBridge.BuildFromLegacy(data);
        doc.Environment = SC2EnvironmentLegacyBridge.BuildFromLegacy(data);

        var rawData = data.RawData;
        var scenarioLuaBytes = rawData != null && !string.IsNullOrEmpty(rawData.ScenarioLua)
            ? Encoding.UTF8.GetBytes(rawData.ScenarioLua) : Array.Empty<byte>();
        var saveLuaText = rawData != null ? rawData.GetSaveLua() : "";
        var saveLuaBytes = !string.IsNullOrEmpty(saveLuaText) ? Encoding.UTF8.GetBytes(saveLuaText) : Array.Empty<byte>();
        var scriptLuaBytes = rawData != null && !string.IsNullOrEmpty(rawData.ScriptLua)
            ? Encoding.UTF8.GetBytes(rawData.ScriptLua) : Array.Empty<byte>();
        doc.Scenario = SC2ScenarioLegacyBridge.BuildFromLegacy(scenarioLuaBytes, saveLuaBytes, scriptLuaBytes, blobStore);
        // The Display Name field edits data.DisplayName; the name parsed from the original scenario
        // lua above is only what an untouched imported map started with.
        if (!string.IsNullOrEmpty(data.DisplayName))
            doc.Scenario.KnownFields.Name = data.DisplayName;
        else if (string.IsNullOrEmpty(doc.Scenario.KnownFields.Name))
            doc.Scenario.KnownFields.Name = data.MapName ?? "";
        // The lobby-minimap positions exist only on the session's document
        // (no SC2TerrainData field mirrors them, like Decals below) — the
        // fresh Scenario built above knows nothing about them.
        if (session?.Document?.Scenario?.LobbyStartPositions != null)
            doc.Scenario.LobbyStartPositions = SC2LobbyStartPosition.CloneList(session.Document.Scenario.LobbyStartPositions);

        // The preview pictures and the Collision2 tab's meshes/flags have no counterpart in the
        // legacy bridges above; without these blobs a normal load (which reads this package, not
        // the legacy JSON) would drop them.
        doc.Terrain.PointCloudSkyColor = data.PointCloudSkyColor;
        doc.Terrain.PointCloudSkyIntensity = data.PointCloudSkyIntensity;
        doc.Terrain.PointCloudGroundReflection = data.PointCloudGroundReflection;
        doc.Terrain.PointCloudSkyInGround = data.PointCloudSkyInGround;
        doc.Terrain.PointCloudBlurTexels = data.PointCloudBlurTexels;
        doc.Terrain.PointCloudSunShadow = data.PointCloudSunShadow;
        doc.Terrain.PointCloudBoxOrigin = data.PointCloudBoxOrigin;
        doc.Terrain.PointCloudBoxSize = data.PointCloudBoxSize;
        doc.Terrain.PointCloudUp = PutBlobBytes(blobStore, data.PointCloudUpDds);
        doc.Terrain.PointCloudDown = PutBlobBytes(blobStore, data.PointCloudDownDds);
        doc.Terrain.ImportedRnmBakedRgba = PutBlobBytes(blobStore, data.ImportedRnmBakedRgba);
        doc.Terrain.ImportedRnmWidth = data.ImportedRnmWidth;
        doc.Terrain.ImportedRnmHeight = data.ImportedRnmHeight;
        doc.Terrain.ImportedRnmWorldOriginX = data.ImportedRnmWorldOriginX;
        doc.Terrain.ImportedRnmWorldOriginZ = data.ImportedRnmWorldOriginZ;
        doc.Terrain.ImportedRnmWorldSpanX = data.ImportedRnmWorldSpanX;
        doc.Terrain.ImportedRnmWorldSpanZ = data.ImportedRnmWorldSpanZ;
        doc.Scenario.MapShotBgra = PutBlobBytes(blobStore, data.MapShotBgraPixels);
        doc.Scenario.CustomMinimapPng = PutBlobBytes(blobStore, data.CustomMinimapPng);
        doc.Collision2.PreviewMeshBytes = PutBlobBytes(blobStore, data.Collision2PreviewBytes);
        doc.Collision2.CustomMeshBytes = PutBlobBytes(blobStore, data.CustomCollision2Bytes);
        doc.Collision2.UseCustomMesh = data.UseCustomCollision2;
        doc.Collision2.HidePreview = data.Collision2HidePreview;
        doc.Collision2.Wireframe = data.Collision2Wireframe;
        doc.Collision2.ExcludedMaterialNames = data.Collision2ExcludedMaterialNames != null
            ? new List<string>(data.Collision2ExcludedMaterialNames) : new List<string>();

        // Prefer the ACTIVE session's Decals document when one exists —
        // SC2DecalsShell has no legacy SC2TerrainData field to fall back to
        // at all, so rebuilding from RawData's original imported bytes here
        // unconditionally silently discarded every catalog entry/placement/
        // symmetry group authored this session (confirmed real, see this
        // method's own Save-side comment). Only fall back to the legacy
        // passthrough reconstruction when no session is available (this
        // class is deliberately usable without one) or the session's own
        // document has never been touched (HasAuthoredChanges false) — in
        // that case the legacy reconstruction is strictly more informative
        // (it can recover an untouched imported map's original baked
        // decals, which a brand-new empty session document knows nothing
        // about).
        var sessionDecals = session?.Document?.Decals;
        if (sessionDecals != null && sessionDecals.HasAuthoredChanges)
        {
            doc.Decals = sessionDecals.Clone();
        }
        else
        {
            var decalsBytes = rawData != null ? rawData.GetDecalsData() : Array.Empty<byte>();
            doc.Decals = SC2DecalsLegacyBridge.BuildFromLegacy(decalsBytes, blobStore);
        }

        return doc;
    }

    private static SC2BlobReference PutBlobBytes(ISC2BlobStore blobStore, byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return SC2BlobReference.Empty;
        return blobStore.Put(new MemoryStream(bytes));
    }

    /// <summary>Deletes a saved terrain's file from disk. Returns false if it didn't exist.
    /// Also removes the matching .sc2mapdoc package and .sc2mapshare folder — leaving the
    /// latter behind resurrected the just-deleted JSON, since EnsureSharePackagesInstalled
    /// (run by every ListSavedTerrains call, including the one right after this) reinstalls
    /// any .sc2mapshare folder whose JSON is missing, treating a leftover share package as
    /// "not yet installed" rather than "its map was deleted".</summary>
    public static bool Delete(string mapName)
    {
        var path = GetSavePath(mapName);
        if (!File.Exists(path))
            return false;

        File.Delete(path);

        var docDir = DocumentPackageDirectory(mapName);
        if (Directory.Exists(docDir))
            Directory.Delete(docDir, recursive: true);

        var shareDir = SharePackageDirectory(mapName);
        if (Directory.Exists(shareDir))
            Directory.Delete(shareDir, recursive: true);

        return true;
    }

    /// <summary>Duplicates a saved map's legacy JSON and, if present, its document package under
    /// a new name. Blob-backed data (heights, textures, baked lighting) is content-addressed and
    /// shared as-is — the copy's manifest references the same blob hashes, nothing under Blobs/
    /// needs copying (see PutBlobBytes's own doc comment) — only the small identity fields that
    /// key a save to its name are rewritten (the legacy Dto's MapName, and the document package's
    /// Identity.MapStem, which ApplyToLegacy/ReconstructMapName prefers over the legacy JSON
    /// whenever a document package exists — leaving it stale would make the duplicate load back
    /// under the OLD name instead of the new one). Returns false if the source doesn't exist or a
    /// save already exists at newMapName.</summary>
    public static bool Duplicate(string sourceMapName, string newMapName)
    {
        var sourcePath = GetSavePath(sourceMapName);
        if (!File.Exists(sourcePath))
            return false;
        var destPath = GetSavePath(newMapName);
        if (File.Exists(destPath))
            return false;

        var json = File.ReadAllText(sourcePath, Encoding.UTF8);
        var dto = JsonUtility.FromJson<Dto>(json);
        if (dto == null)
            return false;
        dto.MapName = newMapName;
        File.WriteAllText(destPath, JsonUtility.ToJson(dto), Encoding.UTF8);

        var sourceManifestPath = Path.Combine(DocumentPackageDirectory(sourceMapName), "manifest.json");
        if (File.Exists(sourceManifestPath))
        {
            try
            {
                var document = SC2MapDocumentSerializer.Deserialize(File.ReadAllText(sourceManifestPath, Encoding.UTF8));
                if (document.Identity == null)
                    document.Identity = new SC2MapIdentity();
                document.Identity.MapStem = newMapName;
                document.Identity.MapId = Guid.NewGuid().ToString("N");
                document.Identity.CreatedUtc = DateTime.UtcNow;
                document.Identity.ModifiedUtc = DateTime.UtcNow;
                var destDocDir = DocumentPackageDirectory(newMapName);
                Directory.CreateDirectory(destDocDir);
                File.WriteAllText(Path.Combine(destDocDir, "manifest.json"), SC2MapDocumentSerializer.Serialize(document), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                SC2Log.LogWarning($"[SC2RuntimeTerrainPersistence] Duplicate: document package copy failed for " +
                                   $"'{sourceMapName}' -> '{newMapName}', duplicate keeps legacy data only: {ex}");
            }
        }

        return true;
    }

    // `document` is populated only when the document-package path succeeded
    // (null on the legacy-JSON fallback, which predates SC2MapDocument
    // entirely) — confirmed real via the MapDocument audit: the deserialized
    // document (with its Decals catalog/placements, once Save actually
    // writes them — see BuildDocumentSnapshot's own comment) was being
    // built here and then discarded, since ApplyToLegacy only ever
    // returned SC2TerrainData. Callers that need the richer document
    // (currently: restoring Session.Document.Decals after a load) now can.
    public static bool TryLoad(string mapName, out SC2TerrainData data, out SC2MapDocument document)
    {
        if (TryLoadDocumentPackage(mapName, out data, out document))
            return true;

        document = null;
        data = null;
        var path = GetSavePath(mapName);
        if (!File.Exists(path))
            return false;

        var json = File.ReadAllText(path, Encoding.UTF8);
        var dto = JsonUtility.FromJson<Dto>(json);
        if (dto == null)
            return false;

        // Dto.FormatVersion has been written on
        // every save since this class existed, but nothing ever read it back
        // — a save from a NEWER version of this class (one this build
        // doesn't know how to interpret) would previously be handed straight
        // to FromDto and misread silently. dto.FormatVersion == 0 covers
        // saves written before this field existed at all (JsonUtility
        // defaults a missing int field to 0), treated the same as version 1
        // since the DTO shape hasn't changed yet — there is nothing to
        // migrate FROM until a real version 2 exists.
        if (dto.FormatVersion > FormatVersion)
        {
            Debug.LogError($"[SC2RuntimeTerrainPersistence] Save '{mapName}' is FormatVersion {dto.FormatVersion}, " +
                            $"newer than this build understands ({FormatVersion}). Refusing to load it rather than " +
                            "risk misinterpreting fields this version doesn't know about.");
            return false;
        }

        data = FromDto(dto);
        return data != null;
    }

    /// <summary>Prefers the
    /// document package (manifest.json, written by Save's dual-write)
    /// over the legacy flat JSON handled below. This is
    /// the highest-risk part of this class — a bug here reads a
    /// real saved map wrong or empty — so ANY failure at all (missing
    /// manifest, corrupt JSON, a blob the store can't resolve, a
    /// FormatVersion this build has no migration path for, any exception
    /// the reverse bridge itself throws) falls straight through to the
    /// proven legacy path instead of surfacing an error: a bug in this
    /// brand-new code path must never be able to make a real save "not
    /// load" when the old format sitting right next to it still would. The
    /// legacy .sc2terrain.json is never read, modified, or deleted by this
    /// method regardless of outcome.</summary>
    private static bool TryLoadDocumentPackage(string mapName, out SC2TerrainData data, out SC2MapDocument document)
    {
        data = null;
        document = null;
        try
        {
            var manifestPath = Path.Combine(DocumentPackageDirectory(mapName), "manifest.json");
            if (!File.Exists(manifestPath))
                return false;

            var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
            document = SC2MapDocumentSerializer.Deserialize(manifestJson);
            var blobStore = CreateBlobStore();
            data = SC2MapDocumentToLegacyBridge.ApplyToLegacy(document, blobStore);
            Debug.Log($"[SC2RuntimeTerrainPersistence] Loaded '{mapName}' from document package: {manifestPath}");
            return data != null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SC2RuntimeTerrainPersistence] Document package load failed for '{mapName}' " +
                              $"(falling back to legacy JSON): {ex}");
            data = null;
            document = null;
            return false;
        }
    }

    // ---- DTOs: JsonUtility can't serialize Texture2D refs, short[], or nested
    // arrays-of-arrays, so everything is flattened to primitive fields/arrays. ----

    [Serializable]
    private class MaterialDto
    {
        public string Name;
        public float R, G, B, A;
        public string DiffuseName, NormalName, MaskName;
        // Resources-relative path (rooted at SC2TerrainMaterialFactory.
        // ResourcesTextureRoot) each texture actually loaded from, when
        // known — see ResolveTexture's own doc comment for why bare *Name
        // alone is ambiguous (two different textures in different source
        // folders can share the same leaf file name) and why this is
        // preferred when present. Left null/empty for saves made before this
        // field existed, or for textures assigned without going through
        // SC2TerrainMaterialFactory's import path (e.g. picked directly via
        // the generic texture picker) — JsonUtility defaults missing fields
        // to null on older JSON, so this is a safe additive change.
        public string DiffuseResourcePath, NormalResourcePath, MaskResourcePath;
    }

    [Serializable]
    private class ShaderFlagDto
    {
        public string Key;
        public bool Value;
    }

    [Serializable]
    private class PairDto
    {
        public string Name;
        public int Mat1Index, Mat2Index;
        public float ColorR, ColorG, ColorB, ColorA;
        public string RnmMaskName;
        public ShaderFlagDto[] ShaderFlags;
        public float TilingScale;
        public float SimplifyRatio;

        // Export-only material settings and the Ramp/Environment slots. ExtrasStored tells a save that has them
        // from an older one where JsonUtility left these at zero/empty (so AlphaTestThreshold is not read as 0).
        public bool ExtrasStored;
        public string State, ShaderFile, ShaderPrefix;
        public float TextureScrollX, TextureScrollY, AlphaTestThreshold;
        public string RampPath, RampSourceTag, EnvironmentPath, EnvironmentSourceTag;
        public bool RampForceExport, EnvironmentForceExport;

        // These
        // 6 fields used to hold this PAIR's own full copy of the mask/baked-
        // lighting bytes — but multiple pairs routinely SHARE one
        // RnmMaskName (e.g. every land pair on a map usually paints into the
        // same mask01), so the exact same tens-of-MB blob got duplicated
        // once per pair sharing it. The document-package format already
        // dedupes this correctly (SC2RnmMaskSetDocument, keyed by mask
        // name) — Dto.RnmMaskSets below now does the same for this legacy
        // format. ToDto leaves these empty on every new save; FromDto only
        // reads them as a fallback for files saved BEFORE this fix (ones
        // with no Dto.RnmMaskSets at all). The fields stay declared, not
        // removed, purely so those old files still deserialize correctly.
        public byte[] RnmMaskBytes;
        public int MaskWidth, MaskHeight;
        public byte[] RnmBakedRgb0;
        public byte[] RnmBakedRgb1;
        public byte[] RnmBakedRgb2;
        public byte[] RnmBakedShadowAlpha;
    }

    /// <summary>One shared RNM mask + baked-lighting blob per
    /// distinct RnmMaskName across data.Pairs, written ONCE regardless of
    /// how many pairs reference it — see PairDto's own doc comment for why
    /// this exists. Mirrors SC2RnmMaskSetDocument's shape (the document-
    /// package format's own, already-correct equivalent).</summary>
    [Serializable]
    private class RnmMaskSetDto
    {
        public string Name;
        public int Width, Height;
        public byte[] MaskBytes;
        public byte[] BakedRgb0;
        public byte[] BakedRgb1;
        public byte[] BakedRgb2;
        public byte[] BakedShadowAlpha;
    }

    /// <summary>One blend mask editing layer; see SC2BlendMaskLayer. Data is value + coverage, two bytes per pixel.</summary>
    [Serializable]
    private class BlendLayerDto
    {
        public string Id;
        public string Name;
        public bool Visible = true;
        public float Opacity = 1f;
        public byte[] Data;
    }

    [Serializable]
    private class BlendLayerStackDto
    {
        public string MaskName;
        public int Width, Height;
        public int ActiveIndex;
        public BlendLayerDto[] Layers;
    }

    [Serializable]
    private class CostsBlockDto
    {
        public string Name;
        public byte[] Navmesh;
        public byte[] OutlineOpenMask; // absent in older saves
    }

    [Serializable]
    private class Dto
    {
        public int FormatVersion;
        public int DataVersion;
        public string MapName;
        public int GridWidth, GridHeight;
        public float CellSize;
        public bool IsInitialized;
        public float[] Heights;
        public byte[] TrianglePairIndexBytes; // short[] via Buffer.BlockCopy (JsonUtility has no short[] support)
        public int TrianglePairIndexCount;
        public int Symmetry;
        public float SymCenterX, SymCenterY, SymCenterZ;
        public int RadialCount;
        public MaterialDto[] Materials;
        public PairDto[] Pairs;
        // Shared mask/baked-lighting blobs, deduplicated by
        // RnmMaskName — see RnmMaskSetDto's own doc comment. Empty/absent on
        // a file saved before this fix; FromDto falls back to each PairDto's
        // own inline bytes in that case.
        public RnmMaskSetDto[] RnmMaskSets;
        // Blend mask editing layers, one stack per mask name. Absent in a save made before layers existed; the mask
        // then becomes a one-layer stack the first time it is edited.
        public BlendLayerStackDto[] BlendLayerStacks;
        // Without these three, a Build save/load round-trip silently drops
        // back to "no import-only, subdivided SAH BSP" regardless of what
        // was actually set — these bools have no other persisted backing in
        // a standalone Build (only Unity's own ScriptableObject serialization
        // covers them in the Editor's .asset save path).
        public bool ImportOnlyMode;
        public bool SingleBspNodePerLayer;
        public bool VanillaCandidateBspSplit;
        // Without this, a saved map using a non-default
        // grid size would reload with CostsGridSize back at its in-memory
        // default (1024), and EnsureCostsAndTerrainTypes' own mismatched-length
        // guard would then destructively resize the just-loaded CostsBlocks/
        // TerrainTypes arrays back down/up to 1024, silently discarding them.
        public int CostsGridSize;
        public CostsBlockDto[] CostsBlocks;
        public byte[] TerrainTypesBytes; // ushort[] via Buffer.BlockCopy, same reason as TrianglePairIndexBytes
        public int TerrainTypesCount;
        // Cascading Costs generation's shore-hint override mask (never
        // exported) — already byte[], so no BlockCopy workaround needed
        // (JsonUtility supports byte[] natively, same as EnvironmentHeader).
        public byte[] CostsShoreHintOverrideMask;
        public float Collision2HeightOffset;
        // Collision2 tab: custom mesh used for export, and the preview mesh + its view flags.
        public bool UseCustomCollision2;
        public byte[] CustomCollision2Bytes;
        public byte[] Collision2PreviewBytes;
        public bool Collision2HidePreview;
        public bool Collision2Wireframe;
        // Collision2 tab > Exclude from Collision2: material names left out of the generated mesh. Absent in older saves.
        public string[] Collision2ExcludedMaterialNames;
        public string DisplayName;
        // Scenario > Preview: the map-select preview pixels and an imported custom minimap.
        public byte[] MapShotBgraPixels;
        public byte[] CustomMinimapPng;
        public byte[] EnvironmentHeader; // JsonUtility supports byte[] natively (base64), no BlockCopy workaround needed
        public string EnvironmentMap, SkyMap, TexturePath3, ColorCorrectionCube, FogOfWarCorrectionCube;
        public string[] EnvironmentTextureSourceTags;
        public bool[] EnvironmentTextureForceExport;

        // Lighting Bake Inputs (SC2TerrainData.RnmBakeSync*/RnmBakeOverride*/
        // RnmBakeShadowSoftRadius) — had no home in this DTO at all, so a
        // Build save/reload silently reset every one of these back to its
        // in-memory field default regardless of what the user had
        // configured, same gap class as ImportOnlyMode/BSP options above
        // (Vector3/Color are natively JsonUtility-serializable, same as
        // every other struct field in this DTO — no BlockCopy workaround
        // needed).
        public bool RnmBakeSyncSunDirection = true;
        public bool RnmBakeSyncSunColor = true;
        public bool RnmBakeSyncSunIntensity = true;
        public bool RnmBakeSyncShadowColor = true;
        public Vector3 RnmBakeOverrideSunDirection = new Vector3(0.3f, 0.8f, 0.3f);
        public Color RnmBakeOverrideSunColor = Color.white;
        public float RnmBakeOverrideSunIntensity = 1f;
        public Color RnmBakeOverrideShadowColor = new Color(0.64f, 0.77f, 0.92f, 1f);
        public int RnmBakeShadowSoftRadius = 6;
        // Lighting Bake > Point Cloud. PointCloudStored tells a save that has these from an older one where
        // JsonUtility left the settings at zero.
        public bool PointCloudStored;
        public float PointCloudSkyR, PointCloudSkyG, PointCloudSkyB;
        public float PointCloudSkyIntensity, PointCloudGroundReflection, PointCloudSkyInGround, PointCloudBlurTexels;
        public bool PointCloudSunShadow;
        public Vector3 PointCloudBoxOrigin, PointCloudBoxSize;
        public byte[] PointCloudUpDds, PointCloudDownDds;

        // Decal lighting consistency — see SC2RnmBakeService.BakeImportedWorldRnm.
        public byte[] ImportedRnmBakedRgba = Array.Empty<byte>();
        public int ImportedRnmWidth, ImportedRnmHeight;
        public float ImportedRnmWorldOriginX, ImportedRnmWorldOriginZ;
        public float ImportedRnmWorldSpanX = 1f, ImportedRnmWorldSpanZ = 1f;

        // Objects (Objects Editor Plan) — these are plain [Serializable] POCO
        // lists (SC2ObjectsModel.cs) with only JsonUtility-friendly field
        // types (string/float/int/bool/Vector3/Quaternion/List<T>), so unlike
        // TrianglePairIndex/TerrainTypes above they need no BlockCopy/byte[]
        // workaround — JsonUtility serializes them directly.
        public List<SC2ObjProp> ObjProps;
        public List<SC2ObjMarker> ObjMarkers;
        public List<SC2ObjGraph> ObjGraphs;
        public List<SC2ObjArmy> ObjArmies;
        public List<SC2ObjUnitNode> ObjUnits;
        public List<SC2ObjZone> ObjZones;
        public List<SC2ObjChain> ObjChains;
        public string ObjSaveLuaTextOriginal;
        public string ObjScenarioLuaTextOriginal;
        public int NextObjectId;
        public List<SC2ObjSymmetryGroup> ObjSymmetryGroups;
        public List<SC2ObjReferenceUnit> ObjReferenceUnits;
    }

    private static Dto ToDto(SC2TerrainData data)
    {
        var dto = new Dto
        {
            FormatVersion = FormatVersion,
            DataVersion = data.DataVersion,
            MapName = data.MapName,
            GridWidth = data.GridWidth,
            GridHeight = data.GridHeight,
            CellSize = data.CellSize,
            IsInitialized = data.IsInitialized,
            Heights = data.Heights != null ? (float[])data.Heights.Clone() : Array.Empty<float>(),
            Symmetry = (int)data.Symmetry,
            SymCenterX = data.SymmetryCenter.x,
            SymCenterY = data.SymmetryCenter.y,
            SymCenterZ = data.SymmetryCenter.z,
            RadialCount = data.RadialCount
        };

        var shortCount = data.TrianglePairIndex?.Length ?? 0;
        var bytes = new byte[shortCount * sizeof(short)];
        if (shortCount > 0)
            Buffer.BlockCopy(data.TrianglePairIndex, 0, bytes, 0, bytes.Length);
        dto.TrianglePairIndexBytes = bytes;
        dto.TrianglePairIndexCount = shortCount;

        var materials = data.Materials ?? new List<SC2Material>();
        dto.Materials = new MaterialDto[materials.Count];
        for (var i = 0; i < materials.Count; i++)
        {
            var m = materials[i];
            dto.Materials[i] = new MaterialDto
            {
                Name = m.Name,
                R = m.Color.r, G = m.Color.g, B = m.Color.b, A = m.Color.a,
                DiffuseName = m.Diffuse != null ? m.Diffuse.name : null,
                NormalName = m.Normal != null ? m.Normal.name : null,
                MaskName = m.Mask != null ? m.Mask.name : null,
                // ResolveResourcePath (not just TryGetResourcePath) so materials
                // whose texture was assigned via the generic runtime picker —
                // not terrain.bdf import — still get a real, unambiguous path
                // saved, via SC2TextureManifest's reverse lookup.
                DiffuseResourcePath = SC2TerrainMaterialFactory.ResolveResourcePath(m.Diffuse),
                NormalResourcePath = SC2TerrainMaterialFactory.ResolveResourcePath(m.Normal),
                MaskResourcePath = SC2TerrainMaterialFactory.ResolveResourcePath(m.Mask),
            };
        }

        var pairs = data.Pairs ?? new List<SC2MaterialPair>();
        dto.Pairs = new PairDto[pairs.Count];
        // One shared RnmMaskSetDto per distinct RnmMaskName,
        // first pair with that name wins — mirrors SC2TerrainLegacyBridge.
        // BuildPair's identical "first pair seen with this name defines the
        // shared blob" rule for the document-package format, so both
        // persistence formats dedupe the exact same way.
        var maskSets = new Dictionary<string, RnmMaskSetDto>(StringComparer.Ordinal);
        for (var i = 0; i < pairs.Count; i++)
        {
            var p = pairs[i];
            var flags = p.ShaderFlags ?? new List<SC2ShaderFlag>();
            var flagDtos = new ShaderFlagDto[flags.Count];
            for (var f = 0; f < flags.Count; f++)
                flagDtos[f] = new ShaderFlagDto { Key = flags[f].Key, Value = flags[f].Value };

            if (!string.IsNullOrEmpty(p.RnmMaskName) && (p.RnmMaskBytes?.Length ?? 0) > 0 && !maskSets.ContainsKey(p.RnmMaskName))
            {
                maskSets[p.RnmMaskName] = new RnmMaskSetDto
                {
                    Name = p.RnmMaskName,
                    Width = p.MaskWidth,
                    Height = p.MaskHeight,
                    MaskBytes = p.RnmMaskBytes,
                    BakedRgb0 = p.RnmBakedRgb0 ?? Array.Empty<byte>(),
                    BakedRgb1 = p.RnmBakedRgb1 ?? Array.Empty<byte>(),
                    BakedRgb2 = p.RnmBakedRgb2 ?? Array.Empty<byte>(),
                    BakedShadowAlpha = p.RnmBakedShadowAlpha ?? Array.Empty<byte>(),
                };
            }

            dto.Pairs[i] = new PairDto
            {
                Name = p.Name,
                Mat1Index = p.Mat1Index,
                Mat2Index = p.Mat2Index,
                ColorR = p.PairColor.r, ColorG = p.PairColor.g, ColorB = p.PairColor.b, ColorA = p.PairColor.a,
                RnmMaskName = p.RnmMaskName,
                ShaderFlags = flagDtos,
                TilingScale = p.TilingScale,
                SimplifyRatio = p.SimplifyRatio,
                ExtrasStored = true,
                State = p.State ?? "",
                ShaderFile = p.ShaderFile ?? "",
                ShaderPrefix = p.ShaderPrefix ?? "",
                TextureScrollX = p.TextureScroll.x,
                TextureScrollY = p.TextureScroll.y,
                AlphaTestThreshold = p.AlphaTestThreshold,
                RampPath = p.RampPath ?? "",
                RampSourceTag = p.RampSourceTag ?? "",
                RampForceExport = p.RampForceExport,
                EnvironmentPath = p.EnvironmentPath ?? "",
                EnvironmentSourceTag = p.EnvironmentSourceTag ?? "",
                EnvironmentForceExport = p.EnvironmentForceExport,
                // Deliberately left at their C# defaults (null/0) — the
                // real bytes now live once in dto.RnmMaskSets above,
                // resolved by RnmMaskName on load. See PairDto's own doc
                // comment.
            };
        }
        dto.RnmMaskSets = new RnmMaskSetDto[maskSets.Count];
        maskSets.Values.CopyTo(dto.RnmMaskSets, 0);

        var layerStacks = data.BlendLayerStacks ?? new List<SC2BlendMaskLayerStack>();
        dto.BlendLayerStacks = new BlendLayerStackDto[layerStacks.Count];
        for (var i = 0; i < layerStacks.Count; i++)
        {
            var stack = layerStacks[i];
            var layerDtos = new BlendLayerDto[stack.Layers.Count];
            for (var l = 0; l < layerDtos.Length; l++)
            {
                var layer = stack.Layers[l];
                layerDtos[l] = new BlendLayerDto { Id = layer.Id, Name = layer.Name, Visible = layer.Visible, Opacity = layer.Opacity, Data = layer.Data ?? Array.Empty<byte>() };
            }
            dto.BlendLayerStacks[i] = new BlendLayerStackDto { MaskName = stack.MaskName, Width = stack.Width, Height = stack.Height, ActiveIndex = stack.ActiveIndex, Layers = layerDtos };
        }

        dto.ImportOnlyMode = data.ImportOnlyMode;
        dto.SingleBspNodePerLayer = data.SingleBspNodePerLayer;
        dto.VanillaCandidateBspSplit = data.VanillaCandidateBspSplit;

        dto.CostsGridSize = data.CostsGridSize;
        var costsBlocks = data.CostsBlocks ?? new List<SC2CostsBlockData>();
        dto.CostsBlocks = new CostsBlockDto[costsBlocks.Count];
        for (var i = 0; i < costsBlocks.Count; i++)
            dto.CostsBlocks[i] = new CostsBlockDto { Name = costsBlocks[i].Name, Navmesh = costsBlocks[i].Navmesh ?? Array.Empty<byte>(), OutlineOpenMask = costsBlocks[i].OutlineOpenMask ?? Array.Empty<byte>() };

        var terrainTypesCount = data.TerrainTypes?.Length ?? 0;
        var terrainTypesBytes = new byte[terrainTypesCount * sizeof(ushort)];
        if (terrainTypesCount > 0)
            Buffer.BlockCopy(data.TerrainTypes, 0, terrainTypesBytes, 0, terrainTypesBytes.Length);
        dto.TerrainTypesBytes = terrainTypesBytes;
        dto.TerrainTypesCount = terrainTypesCount;
        dto.CostsShoreHintOverrideMask = data.CostsShoreHintOverrideMask ?? Array.Empty<byte>();

        dto.Collision2HeightOffset = data.Collision2HeightOffset;
        dto.UseCustomCollision2 = data.UseCustomCollision2;
        dto.CustomCollision2Bytes = data.CustomCollision2Bytes ?? Array.Empty<byte>();
        dto.Collision2PreviewBytes = data.Collision2PreviewBytes ?? Array.Empty<byte>();
        dto.Collision2HidePreview = data.Collision2HidePreview;
        dto.Collision2Wireframe = data.Collision2Wireframe;
        dto.Collision2ExcludedMaterialNames = data.Collision2ExcludedMaterialNames != null ? data.Collision2ExcludedMaterialNames.ToArray() : Array.Empty<string>();
        dto.DisplayName = data.DisplayName ?? "";
        dto.MapShotBgraPixels = data.MapShotBgraPixels ?? Array.Empty<byte>();
        dto.CustomMinimapPng = data.CustomMinimapPng ?? Array.Empty<byte>();
        dto.EnvironmentHeader = data.EnvironmentHeader != null ? (byte[])data.EnvironmentHeader.Clone() : new byte[508];
        dto.EnvironmentMap = data.EnvironmentMap;
        dto.SkyMap = data.SkyMap;
        dto.TexturePath3 = data.TexturePath3;
        dto.ColorCorrectionCube = data.ColorCorrectionCube;
        dto.FogOfWarCorrectionCube = data.FogOfWarCorrectionCube;
        dto.EnvironmentTextureSourceTags = (string[])data.EnvironmentTextureSourceTags?.Clone();
        dto.EnvironmentTextureForceExport = (bool[])data.EnvironmentTextureForceExport?.Clone();

        dto.RnmBakeSyncSunDirection = data.RnmBakeSyncSunDirection;
        dto.RnmBakeSyncSunColor = data.RnmBakeSyncSunColor;
        dto.RnmBakeSyncSunIntensity = data.RnmBakeSyncSunIntensity;
        dto.RnmBakeSyncShadowColor = data.RnmBakeSyncShadowColor;
        dto.RnmBakeOverrideSunDirection = data.RnmBakeOverrideSunDirection;
        dto.RnmBakeOverrideSunColor = data.RnmBakeOverrideSunColor;
        dto.RnmBakeOverrideSunIntensity = data.RnmBakeOverrideSunIntensity;
        dto.RnmBakeOverrideShadowColor = data.RnmBakeOverrideShadowColor;
        dto.RnmBakeShadowSoftRadius = data.RnmBakeShadowSoftRadius;
        dto.PointCloudStored = true;
        dto.PointCloudSkyR = data.PointCloudSkyColor.r; dto.PointCloudSkyG = data.PointCloudSkyColor.g; dto.PointCloudSkyB = data.PointCloudSkyColor.b;
        dto.PointCloudSkyIntensity = data.PointCloudSkyIntensity;
        dto.PointCloudGroundReflection = data.PointCloudGroundReflection;
        dto.PointCloudSkyInGround = data.PointCloudSkyInGround;
        dto.PointCloudBlurTexels = data.PointCloudBlurTexels;
        dto.PointCloudSunShadow = data.PointCloudSunShadow;
        dto.PointCloudBoxOrigin = data.PointCloudBoxOrigin;
        dto.PointCloudBoxSize = data.PointCloudBoxSize;
        dto.PointCloudUpDds = data.PointCloudUpDds ?? Array.Empty<byte>();
        dto.PointCloudDownDds = data.PointCloudDownDds ?? Array.Empty<byte>();
        dto.ImportedRnmBakedRgba = data.ImportedRnmBakedRgba ?? Array.Empty<byte>();
        dto.ImportedRnmWidth = data.ImportedRnmWidth;
        dto.ImportedRnmHeight = data.ImportedRnmHeight;
        dto.ImportedRnmWorldOriginX = data.ImportedRnmWorldOriginX;
        dto.ImportedRnmWorldOriginZ = data.ImportedRnmWorldOriginZ;
        dto.ImportedRnmWorldSpanX = data.ImportedRnmWorldSpanX;
        dto.ImportedRnmWorldSpanZ = data.ImportedRnmWorldSpanZ;

        data.EnsureObjectsLists();
        dto.ObjProps = new List<SC2ObjProp>(data.ObjProps);
        dto.ObjMarkers = new List<SC2ObjMarker>(data.ObjMarkers);
        dto.ObjGraphs = new List<SC2ObjGraph>(data.ObjGraphs);
        dto.ObjArmies = new List<SC2ObjArmy>(data.ObjArmies);
        dto.ObjUnits = new List<SC2ObjUnitNode>(data.ObjUnits);
        dto.ObjZones = new List<SC2ObjZone>(data.ObjZones);
        dto.ObjChains = new List<SC2ObjChain>(data.ObjChains);
        dto.ObjSaveLuaTextOriginal = data.ObjSaveLuaTextOriginal ?? "";
        dto.ObjScenarioLuaTextOriginal = data.ObjScenarioLuaTextOriginal ?? "";
        dto.NextObjectId = data.NextObjectId;
        dto.ObjSymmetryGroups = new List<SC2ObjSymmetryGroup>(data.ObjSymmetryGroups);
        dto.ObjReferenceUnits = new List<SC2ObjReferenceUnit>(data.ObjReferenceUnits);

        return dto;
    }

    private static SC2TerrainData FromDto(Dto dto)
    {
        var data = ScriptableObject.CreateInstance<SC2TerrainData>();
        data.name = string.IsNullOrEmpty(dto.MapName) ? "RuntimeTerrainData" : dto.MapName;
        data.MapName = dto.MapName;
        data.GridWidth = dto.GridWidth;
        data.GridHeight = dto.GridHeight;
        data.CellSize = dto.CellSize;
        data.IsInitialized = dto.IsInitialized;
        data.Heights = dto.Heights ?? Array.Empty<float>();
        // Ordinals 4-7 were the pre-consolidation fixed Radial2/4/6/8 values,
        // saved before RadialCount existed (JsonUtility defaults a missing
        // int field to 0, which is otherwise never a valid RadialCount) —
        // translate old saves into the new Radial + RadialCount pair instead
        // of letting them deserialize as an unnamed, silently-ignored enum value.
        if (dto.Symmetry >= 4 && dto.Symmetry <= 7)
        {
            data.Symmetry = SymmetryMode.Radial;
            data.RadialCount = dto.Symmetry switch { 4 => 2, 5 => 4, 6 => 6, 7 => 8, _ => 4 };
        }
        else
        {
            data.Symmetry = (SymmetryMode)dto.Symmetry;
            data.RadialCount = dto.RadialCount > 0 ? dto.RadialCount : 4;
        }
        data.SymmetryCenter = new Vector3(dto.SymCenterX, dto.SymCenterY, dto.SymCenterZ);
        data.DataVersion = dto.DataVersion;

        var shortCount = dto.TrianglePairIndexCount;
        var shorts = new short[shortCount];
        if (shortCount > 0 && dto.TrianglePairIndexBytes != null)
        {
            var byteCount = Mathf.Min(dto.TrianglePairIndexBytes.Length, shorts.Length * sizeof(short));
            Buffer.BlockCopy(dto.TrianglePairIndexBytes, 0, shorts, 0, byteCount);
        }
        data.TrianglePairIndex = shorts;

        data.Materials = new List<SC2Material>();
        foreach (var m in dto.Materials ?? Array.Empty<MaterialDto>())
        {
            data.Materials.Add(new SC2Material
            {
                Name = m.Name,
                Color = new Color(m.R, m.G, m.B, m.A),
                Diffuse = ResolveTexture(m.DiffuseName, m.DiffuseResourcePath, m.Name, "Diffuse"),
                Normal = ResolveTexture(m.NormalName, m.NormalResourcePath, m.Name, "Normal"),
                Mask = ResolveTexture(m.MaskName, m.MaskResourcePath, m.Name, "Mask")
            });
        }

        // Resolve each pair's mask/baked-lighting bytes from the
        // shared dto.RnmMaskSets (new-format saves, see ToDto) by
        // RnmMaskName; a file with no RnmMaskSets at all (saved before this
        // fix) falls back to each PairDto's own still-populated inline
        // fields below, so old saves keep loading exactly as before.
        var maskSetsByName = new Dictionary<string, RnmMaskSetDto>(StringComparer.Ordinal);
        foreach (var ms in dto.RnmMaskSets ?? Array.Empty<RnmMaskSetDto>())
            if (!string.IsNullOrEmpty(ms.Name) && !maskSetsByName.ContainsKey(ms.Name))
                maskSetsByName[ms.Name] = ms;

        data.Pairs = new List<SC2MaterialPair>();
        foreach (var p in dto.Pairs ?? Array.Empty<PairDto>())
        {
            var flags = new List<SC2ShaderFlag>();
            foreach (var f in p.ShaderFlags ?? Array.Empty<ShaderFlagDto>())
                flags.Add(new SC2ShaderFlag { Key = f.Key, Value = f.Value });

            var maskSet = !string.IsNullOrEmpty(p.RnmMaskName) && maskSetsByName.TryGetValue(p.RnmMaskName, out var found) ? found : null;

            data.Pairs.Add(new SC2MaterialPair
            {
                Name = p.Name,
                Mat1Index = p.Mat1Index,
                Mat2Index = p.Mat2Index,
                PairColor = new Color(p.ColorR, p.ColorG, p.ColorB, p.ColorA),
                RnmMaskName = p.RnmMaskName,
                ShaderFlags = flags,
                RnmMaskBytes = maskSet?.MaskBytes ?? p.RnmMaskBytes ?? Array.Empty<byte>(),
                MaskWidth = maskSet?.Width ?? p.MaskWidth,
                MaskHeight = maskSet?.Height ?? p.MaskHeight,
                TilingScale = p.TilingScale,
                SimplifyRatio = p.SimplifyRatio,
                RnmBakedRgb0 = maskSet?.BakedRgb0 ?? p.RnmBakedRgb0 ?? Array.Empty<byte>(),
                RnmBakedRgb1 = maskSet?.BakedRgb1 ?? p.RnmBakedRgb1 ?? Array.Empty<byte>(),
                RnmBakedRgb2 = maskSet?.BakedRgb2 ?? p.RnmBakedRgb2 ?? Array.Empty<byte>(),
                RnmBakedShadowAlpha = maskSet?.BakedShadowAlpha ?? p.RnmBakedShadowAlpha ?? Array.Empty<byte>(),
                State = p.ExtrasStored ? p.State ?? "" : "",
                ShaderFile = p.ExtrasStored ? p.ShaderFile ?? "" : "",
                ShaderPrefix = p.ExtrasStored ? p.ShaderPrefix ?? "" : "",
                TextureScroll = p.ExtrasStored ? new Vector2(p.TextureScrollX, p.TextureScrollY) : Vector2.zero,
                AlphaTestThreshold = p.ExtrasStored ? p.AlphaTestThreshold : 16f,
                RampPath = p.ExtrasStored ? p.RampPath ?? "" : "",
                RampSourceTag = p.ExtrasStored ? p.RampSourceTag ?? "" : "",
                RampForceExport = p.ExtrasStored && p.RampForceExport,
                EnvironmentPath = p.ExtrasStored ? p.EnvironmentPath ?? "" : "",
                EnvironmentSourceTag = p.ExtrasStored ? p.EnvironmentSourceTag ?? "" : "",
                EnvironmentForceExport = p.ExtrasStored && p.EnvironmentForceExport,
            });
        }

        data.BlendLayerStacks = new List<SC2BlendMaskLayerStack>();
        foreach (var stackDto in dto.BlendLayerStacks ?? Array.Empty<BlendLayerStackDto>())
        {
            if (stackDto?.Layers == null || stackDto.Layers.Length == 0) continue;
            var stack = new SC2BlendMaskLayerStack
            {
                MaskName = stackDto.MaskName ?? "mask01",
                Width = stackDto.Width > 0 ? stackDto.Width : 2048,
                Height = stackDto.Height > 0 ? stackDto.Height : 2048,
                ActiveIndex = stackDto.ActiveIndex,
            };
            foreach (var layerDto in stackDto.Layers)
                stack.Layers.Add(new SC2BlendMaskLayer
                {
                    Id = layerDto.Id ?? "",
                    Name = layerDto.Name ?? "Layer",
                    Visible = layerDto.Visible,
                    Opacity = layerDto.Opacity,
                    Data = layerDto.Data ?? Array.Empty<byte>(),
                });
            stack.Normalize();
            data.BlendLayerStacks.Add(stack);
        }

        // Field-missing default (false) is already the correct behavior for
        // saves written before these existed — no inference needed, unlike
        // CostsGridSize below.
        data.ImportOnlyMode = dto.ImportOnlyMode;
        data.SingleBspNodePerLayer = dto.SingleBspNodePerLayer;
        data.VanillaCandidateBspSplit = dto.VanillaCandidateBspSplit;

        data.CostsBlocks = new List<SC2CostsBlockData>();
        foreach (var b in dto.CostsBlocks ?? Array.Empty<CostsBlockDto>())
            data.CostsBlocks.Add(new SC2CostsBlockData { Name = b.Name, Navmesh = b.Navmesh ?? Array.Empty<byte>(), OutlineOpenMask = b.OutlineOpenMask ?? Array.Empty<byte>() });

        // dto.CostsGridSize is 0 for saves written before this field existed —
        // fall back to whatever size the loaded Navmesh/TerrainTypes arrays
        // actually imply (both always store exactly gridSize*gridSize
        // entries), defaulting to the historical fixed 1024 only if no data
        // is present at all to infer it from.
        if (dto.CostsGridSize > 0)
        {
            data.CostsGridSize = dto.CostsGridSize;
        }
        else
        {
            var firstNavmeshLen = data.CostsBlocks.Count > 0 ? data.CostsBlocks[0].Navmesh?.Length ?? 0 : 0;
            var inferredCells = firstNavmeshLen > 0 ? firstNavmeshLen : dto.TerrainTypesCount;
            var inferredSize = inferredCells > 0 ? Mathf.RoundToInt(Mathf.Sqrt(inferredCells)) : 0;
            data.CostsGridSize = inferredSize > 0 ? inferredSize : 1024;
        }

        var terrainTypesCount = dto.TerrainTypesCount;
        var terrainTypes = new ushort[terrainTypesCount];
        if (terrainTypesCount > 0 && dto.TerrainTypesBytes != null)
        {
            var byteCount = Mathf.Min(dto.TerrainTypesBytes.Length, terrainTypes.Length * sizeof(ushort));
            Buffer.BlockCopy(dto.TerrainTypesBytes, 0, terrainTypes, 0, byteCount);
        }
        data.TerrainTypes = terrainTypes;
        // EnsureCostsAndTerrainTypes resizes/zero-fills this if missing or
        // mismatched (older save, or a since-changed CostsGridSize) — same
        // guard already relied on for CostsBlocks/TerrainTypes above.
        data.CostsShoreHintOverrideMask = dto.CostsShoreHintOverrideMask ?? Array.Empty<byte>();

        data.Collision2HeightOffset = dto.Collision2HeightOffset;
        data.UseCustomCollision2 = dto.UseCustomCollision2 && dto.CustomCollision2Bytes is { Length: > 0 };
        data.CustomCollision2Bytes = dto.CustomCollision2Bytes is { Length: > 0 } ? dto.CustomCollision2Bytes : null;
        data.Collision2PreviewBytes = dto.Collision2PreviewBytes is { Length: > 0 } ? dto.Collision2PreviewBytes : null;
        data.Collision2HidePreview = dto.Collision2HidePreview;
        data.Collision2Wireframe = dto.Collision2Wireframe;
        data.Collision2ExcludedMaterialNames = dto.Collision2ExcludedMaterialNames != null ? new List<string>(dto.Collision2ExcludedMaterialNames) : new List<string>();
        data.DisplayName = dto.DisplayName ?? "";
        data.MapShotBgraPixels = dto.MapShotBgraPixels is { Length: > 0 } ? dto.MapShotBgraPixels : null;
        data.CustomMinimapPng = dto.CustomMinimapPng is { Length: > 0 } ? dto.CustomMinimapPng : null;
        data.EnvironmentHeader = dto.EnvironmentHeader != null && dto.EnvironmentHeader.Length == 508 ? dto.EnvironmentHeader : new byte[508];
        data.EnvironmentMap = dto.EnvironmentMap ?? "";
        data.SkyMap = dto.SkyMap ?? "";
        data.TexturePath3 = dto.TexturePath3 ?? "";
        data.ColorCorrectionCube = dto.ColorCorrectionCube ?? "";
        data.FogOfWarCorrectionCube = dto.FogOfWarCorrectionCube ?? "";
        // Older saves have neither array — keep SC2TerrainData's own
        // defaults (5 empty tags / 5 unchecked) instead of nulling them.
        if (dto.EnvironmentTextureSourceTags != null && dto.EnvironmentTextureSourceTags.Length == 5)
            data.EnvironmentTextureSourceTags = dto.EnvironmentTextureSourceTags;
        if (dto.EnvironmentTextureForceExport != null && dto.EnvironmentTextureForceExport.Length == 5)
            data.EnvironmentTextureForceExport = dto.EnvironmentTextureForceExport;

        // A save written before these existed deserializes with the Dto
        // field initializers above (JsonUtility only overwrites fields
        // actually present in the JSON), which already match
        // SC2TerrainData's own defaults — no extra fallback needed here.
        data.RnmBakeSyncSunDirection = dto.RnmBakeSyncSunDirection;
        data.RnmBakeSyncSunColor = dto.RnmBakeSyncSunColor;
        data.RnmBakeSyncSunIntensity = dto.RnmBakeSyncSunIntensity;
        data.RnmBakeSyncShadowColor = dto.RnmBakeSyncShadowColor;
        data.RnmBakeOverrideSunDirection = dto.RnmBakeOverrideSunDirection;
        data.RnmBakeOverrideSunColor = dto.RnmBakeOverrideSunColor;
        data.RnmBakeOverrideSunIntensity = dto.RnmBakeOverrideSunIntensity;
        data.RnmBakeOverrideShadowColor = dto.RnmBakeOverrideShadowColor;
        data.RnmBakeShadowSoftRadius = dto.RnmBakeShadowSoftRadius;
        if (dto.PointCloudStored)
        {
            data.PointCloudSkyColor = new Color(dto.PointCloudSkyR, dto.PointCloudSkyG, dto.PointCloudSkyB, 1f);
            data.PointCloudSkyIntensity = dto.PointCloudSkyIntensity;
            data.PointCloudGroundReflection = dto.PointCloudGroundReflection;
            data.PointCloudSkyInGround = dto.PointCloudSkyInGround;
            data.PointCloudBlurTexels = dto.PointCloudBlurTexels;
            data.PointCloudSunShadow = dto.PointCloudSunShadow;
            data.PointCloudBoxOrigin = dto.PointCloudBoxOrigin;
            data.PointCloudBoxSize = dto.PointCloudBoxSize;
        }
        data.PointCloudUpDds = dto.PointCloudUpDds is { Length: > 0 } ? dto.PointCloudUpDds : null;
        data.PointCloudDownDds = dto.PointCloudDownDds is { Length: > 0 } ? dto.PointCloudDownDds : null;
        // Array.Empty<byte>(), not null — matches this field's own "empty = never baked" idiom
        // (SC2MaterialPair.RnmBakedRgb0's convention), unlike PointCloudUpDds's null-means-absent one.
        data.ImportedRnmBakedRgba = dto.ImportedRnmBakedRgba ?? Array.Empty<byte>();
        data.ImportedRnmWidth = dto.ImportedRnmWidth;
        data.ImportedRnmHeight = dto.ImportedRnmHeight;
        data.ImportedRnmWorldOriginX = dto.ImportedRnmWorldOriginX;
        data.ImportedRnmWorldOriginZ = dto.ImportedRnmWorldOriginZ;
        data.ImportedRnmWorldSpanX = dto.ImportedRnmWorldSpanX;
        data.ImportedRnmWorldSpanZ = dto.ImportedRnmWorldSpanZ;

        data.ObjProps = dto.ObjProps ?? new List<SC2ObjProp>();
        foreach (var prop in data.ObjProps)
            if (!prop.RenderPriorityStored) { prop.RenderPriority = 1f; prop.RenderPriorityStored = true; }
        data.ObjMarkers = dto.ObjMarkers ?? new List<SC2ObjMarker>();
        data.ObjGraphs = dto.ObjGraphs ?? new List<SC2ObjGraph>();
        data.ObjArmies = dto.ObjArmies ?? new List<SC2ObjArmy>();
        data.ObjUnits = dto.ObjUnits ?? new List<SC2ObjUnitNode>();
        data.ObjZones = dto.ObjZones ?? new List<SC2ObjZone>();
        data.ObjChains = dto.ObjChains ?? new List<SC2ObjChain>();
        data.ObjSaveLuaTextOriginal = dto.ObjSaveLuaTextOriginal ?? "";
        data.ObjScenarioLuaTextOriginal = dto.ObjScenarioLuaTextOriginal ?? "";
        data.NextObjectId = Mathf.Max(1, dto.NextObjectId);
        data.ObjSymmetryGroups = dto.ObjSymmetryGroups ?? new List<SC2ObjSymmetryGroup>();
        data.ObjReferenceUnits = dto.ObjReferenceUnits ?? new List<SC2ObjReferenceUnit>();

        data.EnsureSerializableState();
        data.InvalidateGeometryCache();
        return data;
    }

    /// <summary>
    /// Resolves a saved material texture reference back to a live Texture2D.
    /// Prefers <paramref name="resourcePath"/> (the exact Resources-relative
    /// path SC2TerrainMaterialFactory recorded when it originally loaded this
    /// texture — see its TryGetResourcePath) when one was saved: Resources.Load
    /// with a full path is unambiguous by construction (Unity doesn't allow
    /// two different assets at the same Resources path in one build).
    ///
    /// Falls back to the old by-<paramref name="name"/> search over every
    /// loaded Texture2D for saves made before DiffuseResourcePath/etc. existed,
    /// or for a texture that was never loaded through the factory (e.g.
    /// assigned directly via the generic texture picker instead of import) —
    /// but that fallback is genuinely ambiguous if two DIFFERENT textures
    /// happen to share the same bare name (two source folders each containing
    /// their own "diffuse.dds", for instance): FindObjectsOfTypeAll makes no
    /// ordering guarantee, so this could return either one. There is no way
    /// to fully resolve that case without a saved path, which is exactly what
    /// the resourcePath-first branch above is for.
    /// </summary>
    /// <param name="materialName">Diagnostic only — identifies which
    /// material this slot belongs to in the log line below.</param>
    /// <param name="slotLabel">Diagnostic only — "Diffuse"/"Normal"/"Mask",
    /// so a failure log line says exactly which slot didn't resolve. Added
    /// after a report that Normal/Mask came back missing after a reload
    /// while Diffuse resolved correctly on the same material, with no
    /// obvious asymmetry in this method's own logic between the three slots
    /// — logging the exact attempted name/path on failure turns the next
    /// occurrence into concrete evidence instead of another guess.</param>
    /// <summary>The actual resolution logic moved to
    /// SC2TerrainMaterialFactory.ResolveTextureByPathOrName so
    /// SC2MapDocumentToLegacyBridge's reverse mapping can share it instead
    /// of duplicating it — see that method's own doc comment for the full
    /// resourcePath/name/catalog fallback chain. This wrapper exists only
    /// so every existing call site below keeps reading exactly as before.</summary>
    private static Texture2D ResolveTexture(string name, string resourcePath = null, string materialName = null, string slotLabel = null) =>
        SC2TerrainMaterialFactory.ResolveTextureByPathOrName(name, resourcePath, materialName, slotLabel);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }
}
#endif
