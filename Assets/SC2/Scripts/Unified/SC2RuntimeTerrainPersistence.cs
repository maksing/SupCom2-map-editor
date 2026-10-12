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
/// A map is saved as one folder under Application.persistentDataPath, "&lt;Map&gt;.sc2mapshare": the document
/// (manifest.json plus its content-addressed blobs) and, unless switched off, a legacy JSON copy of the
/// whole map (see SC2SaveLayout for the exact layout). The folder is self-contained — copying it to another
/// machine's save directory is all it takes to move the map — and it is the only thing written. The earlier
/// layout (the same files directly in the save directory, one shared Blobs pool) is still read for maps that
/// have no folder yet. Texture2D references (material Diffuse/Normal/Mask) can't be embedded in JSON, so
/// only their name is stored and re-resolved on load via Resources.FindObjectsOfTypeAll — the same
/// best-effort lookup the runtime texture picker overlay already relies on for standalone builds.
/// </summary>
public static class SC2RuntimeTerrainPersistence
{
    private const int FormatVersion = 2;

    // The save directory is read once on the main thread (SC2SaveStorage.Prime) so the paths below also work from the autosave's
    // worker thread.
    public static string SaveDirectory => SC2SaveStorage.SaveDirectory;

    /// <summary>The file whose OS dates and size stand for a saved map: the manifest in the map's own folder, or for a map that only
    /// exists in the earlier layout its JSON (or its manifest). The path of the map's own manifest when nothing exists yet.</summary>
    public static string GetSavePath(string mapName)
    {
        var shareManifest = SC2SaveLayout.ShareManifestPath(SaveDirectory, mapName);
        if (File.Exists(shareManifest)) return shareManifest;
        var shareJson = SC2SaveLayout.ShareJsonPath(SaveDirectory, mapName);
        if (File.Exists(shareJson)) return shareJson;
        var legacyJson = SC2SaveLayout.LegacyJsonPath(SaveDirectory, mapName);
        if (File.Exists(legacyJson)) return legacyJson;
        var legacyManifest = SC2SaveLayout.LegacyManifestPath(SaveDirectory, mapName);
        return File.Exists(legacyManifest) ? legacyManifest : shareManifest;
    }

    /// <summary>The saved maps: every folder "&lt;Map&gt;.sc2mapshare" that holds a manifest or a JSON copy, then the maps that only
    /// exist as files of the earlier layout directly in the save directory (read only; a map with a folder of its own is listed once).</summary>
    public static List<string> ListSavedTerrains()
    {
        if (!Directory.Exists(SaveDirectory))
            return new List<string>();

        var shareStems = new List<string>();
        foreach (var dir in Directory.GetDirectories(SaveDirectory, "*" + SC2SaveLayout.ShareSuffix))
        {
            var stem = SC2SaveLayout.StemOf(Path.GetFileName(dir), SC2SaveLayout.ShareSuffix);
            if (stem == null) continue;
            if (File.Exists(SC2SaveLayout.ShareManifestPath(SaveDirectory, stem)) || File.Exists(SC2SaveLayout.ShareJsonPath(SaveDirectory, stem)))
                shareStems.Add(stem);
        }

        var legacyStems = new List<string>();
        foreach (var file in Directory.GetFiles(SaveDirectory, "*" + SC2SaveLayout.LegacyJsonSuffix))
        {
            var stem = SC2SaveLayout.StemOf(Path.GetFileName(file), SC2SaveLayout.LegacyJsonSuffix);
            if (stem != null) legacyStems.Add(stem);
        }

        return SC2SaveLayout.MergeMapNames(shareStems, legacyStems);
    }

    /// <summary>Writes a text file through a temp file and an atomic replace, so the final path always holds either the whole previous
    /// content or the whole new one, never a half-written mix after a crash or a power loss. File.Replace is used when the file
    /// already exists (File.Move alone throws then).</summary>
    private static void WriteTextAtomic(string finalPath, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath));
        var tempPath = finalPath + ".tmp";
        File.WriteAllText(tempPath, text, Encoding.UTF8);
        ReplaceFile(tempPath, finalPath);
    }

    private static void ReplaceFile(string tempPath, string finalPath)
    {
        if (File.Exists(finalPath))
            File.Replace(tempPath, finalPath, null);
        else
            File.Move(tempPath, finalPath);
    }

    /// <summary>Saves the map into its own folder, "&lt;Map&gt;.sc2mapshare" (see SC2SaveLayout), which is the single source of truth:
    /// the blobs the document needs are added first (a blob already there is left alone), then the legacy JSON copy when it is
    /// switched on (SC2SaveSettings.WriteLegacyJson), and the manifest last, atomically, so the folder always holds a manifest whose
    /// blobs are all present. Blobs the new manifest no longer refers to are then removed from the folder. Nothing is written to the
    /// earlier layout in the save directory, and nothing in it is touched.
    ///
    /// `session` is optional (this class is deliberately session-agnostic): when present, the data only the session's document
    /// holds (decals, imported terrain objects, lobby positions) is saved from it. A failure throws: the save is the folder itself.
    /// Every stage is logged through SC2Log, whose file writer is AutoFlush=true — each line lands on disk the instant it is
    /// written, so the last "Save: ..." line in Logs/SC2TerrainEditor.log is the last stage that completed.</summary>
    public static void Save(SC2TerrainData data, SC2MapSession session = null)
    {
        if (data == null) return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mapName = data.MapName;
        SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save started. MapName='{mapName}'.");
        SC2SaveStorage.Prime();

        string json = null;
        if (SC2SaveSettings.WriteLegacyJson)
        {
            json = JsonUtility.ToJson(ToDto(data));
            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: legacy JSON built ({sw.ElapsedMilliseconds}ms, {json.Length:N0} chars).");
        }

        var blobStore = SC2SaveStorage.CreateSessionBlobStore();
        var document = BuildDocumentSnapshot(data, session, blobStore);
        SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: document snapshot built ({sw.ElapsedMilliseconds}ms).");
        var manifestJson = SC2MapDocumentSerializer.Serialize(document);
        SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: manifest serialized ({sw.ElapsedMilliseconds}ms, {manifestJson.Length:N0} chars).");

        Directory.CreateDirectory(SC2SaveLayout.ShareDirectory(SaveDirectory, mapName));
        var shareBlobs = SC2SaveStorage.OpenShareBlobs(mapName, create: true);
        var referenced = SC2BlobStoreGarbageCollector.CollectReferencedHashes(manifestJson);
        CopyBlobs(referenced, blobStore, shareBlobs);
        SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: {referenced.Count} blob(s) in the map folder ({sw.ElapsedMilliseconds}ms).");

        if (json != null)
        {
            WriteTextAtomic(SC2SaveLayout.ShareJsonPath(SaveDirectory, mapName), json);
            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: legacy JSON written ({sw.ElapsedMilliseconds}ms).");
        }

        var manifestPath = SC2SaveLayout.ShareManifestPath(SaveDirectory, mapName);
        WriteTextAtomic(manifestPath, manifestJson);
        SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: manifest written to '{manifestPath}' ({sw.ElapsedMilliseconds}ms).");

        // The save is done; what follows only tidies up and must not make it look like it failed.
        SC2SaveStorage.RegisterMap(mapName);
        try
        {
            // A manifest that names no blob at all (or one that could not be read back) is no reason to empty the folder.
            if (referenced.Count > 0)
            {
                var deleted = SC2BlobStoreGarbageCollector.CollectUnreferencedBlobs(shareBlobs, new[] { manifestJson });
                if (deleted > 0) SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save: {deleted} unused blob(s) removed from the map folder.");
            }
            CreateBackupSnapshot(mapName, json, manifestJson, blobStore);
            // What the folder now holds no longer needs its copy in the working cache.
            var cache = SC2SaveStorage.OpenCache();
            foreach (var hash in referenced)
                if (shareBlobs.Contains(hash)) cache.TryDelete(hash);
        }
        catch (Exception ex)
        {
            SC2Log.LogWarning($"[SC2RuntimeTerrainPersistence] Save: tidy-up after the save failed (the save itself succeeded): {ex}");
        }
        SC2Log.Log($"[SC2RuntimeTerrainPersistence] Save finished in {sw.ElapsedMilliseconds}ms.");
    }

    /// <summary>Copies the blobs <paramref name="hashes"/> from <paramref name="source"/> into <paramref name="target"/>, skipping the
    /// ones already there. A blob the source cannot supply is logged and left out (the manifest may refer to data an older save lost).</summary>
    private static void CopyBlobs(IEnumerable<string> hashes, ISC2BlobStore source, ISC2BlobStore target)
    {
        foreach (var hash in hashes)
        {
            if (string.IsNullOrEmpty(hash) || target.Contains(hash)) continue;
            if (!source.Contains(hash))
            {
                SC2Log.LogWarning($"[SC2RuntimeTerrainPersistence] Blob {hash} is referenced but missing from every store; it is left out.");
                continue;
            }
            using var stream = source.OpenRead(hash);
            var stored = target.Put(stream);
            if (stored.ContentHash != hash)
                SC2Log.LogWarning($"[SC2RuntimeTerrainPersistence] Blob {hash} was read back as {stored.ContentHash}; its file is damaged.");
        }
    }

    /// <summary>Autosave's ONLY effect — writes a rotating backup snapshot
    /// built from the CURRENT in-memory data, without touching the map's own
    /// folder that Save() above writes. The map's "last saved state" on disk
    /// must only ever change via an explicit user Save — leaving the app open
    /// for an hour without pressing Save still lets the user recover exactly
    /// what was on disk at that last Save; the autosave timer only adds
    /// safety-net backups alongside it, it never becomes the thing "Save"
    /// itself silently was.</summary>
    public static bool CreateAutosaveBackup(SC2TerrainData data, SC2MapSession session)
    {
        if (data == null || SC2SaveSettings.MaxBackupsPerMap <= 0) return false;
        PrimeMainThreadState();
        return WriteAutosaveBackup(data, session, SC2SaveSettings.WriteLegacyJson ? ToDto(data) : null);
    }

    /// <summary>The background-thread form of <see cref="CreateAutosaveBackup"/>, for SC2BusyOverlay.RunInBackground. Call it on the
    /// main thread: it copies the data into the save DTO there (that reads texture names) and returns the function that writes the
    /// backup, which returns the action that reports the outcome to <paramref name="onFinished"/> on the main thread. Null when
    /// backups are off.</summary>
    public static Func<Action> BeginAutosaveBackup(SC2TerrainData data, SC2MapSession session, Action<bool> onFinished)
    {
        if (data == null || SC2SaveSettings.MaxBackupsPerMap <= 0) return null;
        PrimeMainThreadState();
        var dto = SC2SaveSettings.WriteLegacyJson ? ToDto(data) : null;
        return () =>
        {
            SC2BusyOverlay.Report("Writing the backup");
            var ok = WriteAutosaveBackup(data, session, dto);
            return () => onFinished?.Invoke(ok);
        };
    }

    // Reads, on the main thread, everything the backup needs that only the main thread may read (the save folders, the settings that
    // say how many backups are kept and whether the legacy JSON is written), so that the writing itself can run on a worker.
    private static void PrimeMainThreadState()
    {
        SC2SaveStorage.Prime();
        _ = SC2SaveSettings.MaxBackupsPerMap;
        _ = SC2SaveSettings.WriteLegacyJson;
    }

    private static bool WriteAutosaveBackup(SC2TerrainData data, SC2MapSession session, Dto dto)
    {
        try
        {
            var json = dto != null ? JsonUtility.ToJson(dto) : null;
            var blobStore = SC2SaveStorage.CreateSessionBlobStore();
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

    /// <summary>The folder of one map (see SC2SaveLayout).</summary>
    public static string SharePackageDirectory(string mapName) => SC2SaveLayout.ShareDirectory(SaveDirectory, mapName);

    /// <summary>Writes a complete, self-contained copy of one map's save — the legacy JSON when there is one, the manifest and only the
    /// blobs that manifest actually references — into `destDir`, in the same layout as the map's own folder. Self-contained on
    /// purpose: a backup must keep working whatever later happens to the map's folder.</summary>
    private static void WriteSelfContainedPackage(string destDir, string mapName, string legacyJson, string manifestJson, ISC2BlobStore blobStore)
    {
        Directory.CreateDirectory(destDir);

        if (legacyJson != null)
            File.WriteAllText(Path.Combine(destDir, SC2SaveLayout.SafeName(mapName) + SC2SaveLayout.LegacyJsonSuffix), legacyJson, Encoding.UTF8);

        var manifestDir = Path.Combine(destDir, SC2SaveLayout.SafeName(mapName) + SC2SaveLayout.DocumentSuffix);
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(Path.Combine(manifestDir, SC2SaveLayout.ManifestFileName), manifestJson, Encoding.UTF8);

        foreach (var hash in SC2BlobStoreGarbageCollector.CollectReferencedHashes(manifestJson))
        {
            if (!blobStore.Contains(hash)) continue; // referenced but somehow missing from every store — nothing to copy
            var blobDestDir = Path.Combine(destDir, SC2SaveLayout.BlobsFolderName, hash.Substring(0, 2));
            Directory.CreateDirectory(blobDestDir);
            using var src = blobStore.OpenRead(hash);
            using var dest = File.Create(Path.Combine(blobDestDir, hash + ".bin"));
            src.CopyTo(dest);
        }
    }

    // ==================================================================
    // Backups — a rotating history of self-contained snapshots per map
    // (see WriteSelfContainedPackage), independent of the map's own
    // folder, so a corrupted/lost current save can be recovered from
    // whatever was last known-good. Count is configurable via
    // SC2SaveSettings.MaxBackupsPerMap (0 disables backups outright).
    // ==================================================================

    // Local time, filesystem-safe, and lexically sortable in chronological
    // order — the exact same string IS both the folder name (the backup's
    // id) and its human-readable label in the "Restore Backup..." menu, so
    // no separate id<->label mapping is needed anywhere that lists them.
    private const string BackupStampFormat = "yyyy-MM-dd_HH-mm-ss";

    public static string BackupsDirectory(string mapName) => SC2SaveLayout.BackupsDirectory(SaveDirectory, mapName);

    /// <summary>Called from Save (a real Save) and from CreateAutosaveBackup's
    /// (the autosave timer, which never touches the map's folder at all) —
    /// either way a backup failure must never make its caller look like it
    /// failed.</summary>
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

    /// <summary>Copies one backup snapshot back into the map's own folder: its blobs first, then its JSON copy when it has one, and its
    /// manifest last, atomically (the same order as Save, so the folder stays consistent). A backup without a manifest cannot be
    /// restored. The caller is expected to reload the map afterward (e.g. LoadFromDisk) to bring the in-memory state in line with
    /// what was just restored on disk.</summary>
    public static bool RestoreBackup(string mapName, string backupId)
    {
        var backupDir = Path.Combine(BackupsDirectory(mapName), backupId);
        var safe = SC2SaveLayout.SafeName(mapName);
        var backupManifest = Path.Combine(backupDir, safe + SC2SaveLayout.DocumentSuffix, SC2SaveLayout.ManifestFileName);
        var backupJson = Path.Combine(backupDir, safe + SC2SaveLayout.LegacyJsonSuffix);
        if (!File.Exists(backupManifest))
        {
            SC2Log.LogWarning($"[SC2RuntimeTerrainPersistence] RestoreBackup: no backup manifest at '{backupManifest}'.");
            return false;
        }

        try
        {
            SC2SaveStorage.Prime();
            Directory.CreateDirectory(SC2SaveLayout.ShareDirectory(SaveDirectory, mapName));

            var shareBlobs = SC2SaveStorage.OpenShareBlobs(mapName, create: true);
            var backupBlobsDir = Path.Combine(backupDir, SC2SaveLayout.BlobsFolderName);
            if (Directory.Exists(backupBlobsDir))
            {
                foreach (var shardDir in Directory.GetDirectories(backupBlobsDir))
                    foreach (var blobFile in Directory.GetFiles(shardDir, "*.bin"))
                        using (var s = File.OpenRead(blobFile))
                            shareBlobs.Put(s);
            }

            if (File.Exists(backupJson))
            {
                var shareJson = SC2SaveLayout.ShareJsonPath(SaveDirectory, mapName);
                File.Copy(backupJson, shareJson + ".tmp", overwrite: true);
                ReplaceFile(shareJson + ".tmp", shareJson);
            }

            var shareManifest = SC2SaveLayout.ShareManifestPath(SaveDirectory, mapName);
            Directory.CreateDirectory(Path.GetDirectoryName(shareManifest));
            File.Copy(backupManifest, shareManifest + ".tmp", overwrite: true);
            ReplaceFile(shareManifest + ".tmp", shareManifest);

            SC2SaveStorage.RegisterMap(mapName);
            SC2Log.Log($"[SC2RuntimeTerrainPersistence] Restored backup '{backupId}' for '{mapName}'.");
            return true;
        }
        catch (Exception ex)
        {
            SC2Log.LogException(ex);
            return false;
        }
    }

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
        // The textures that live only on this machine (imported from a file, or named by an imported terrain.bdf) travel with the map.
        SC2MapshareTextureCapture.Capture(doc, blobStore);
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
        doc.Collision2.FloorEnabled = data.Collision2FloorEnabled;
        doc.Collision2.FloorHeight = data.Collision2FloorHeight;
        doc.Collision2.CustomArea = data.Collision2CustomArea;
        doc.Collision2.AreaMinX = data.Collision2AreaMinX;
        doc.Collision2.AreaMinZ = data.Collision2AreaMinZ;
        doc.Collision2.AreaSizeX = data.Collision2AreaSizeX;
        doc.Collision2.AreaSizeZ = data.Collision2AreaSizeZ;

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

    /// <summary>Deletes a saved map from disk: its own folder and, so the map does not come back in the list, the files of the earlier
    /// layout in the save directory. Backups are kept. Returns false if there was nothing to delete.</summary>
    public static bool Delete(string mapName)
    {
        var deleted = false;

        var shareDir = SC2SaveLayout.ShareDirectory(SaveDirectory, mapName);
        if (Directory.Exists(shareDir))
        {
            Directory.Delete(shareDir, recursive: true);
            deleted = true;
        }

        var legacyJson = SC2SaveLayout.LegacyJsonPath(SaveDirectory, mapName);
        if (File.Exists(legacyJson))
        {
            File.Delete(legacyJson);
            deleted = true;
        }

        var legacyDocDir = SC2SaveLayout.LegacyDocumentDirectory(SaveDirectory, mapName);
        if (Directory.Exists(legacyDocDir))
        {
            Directory.Delete(legacyDocDir, recursive: true);
            deleted = true;
        }

        return deleted;
    }

    /// <summary>Duplicates a saved map under a new name, as a folder of its own: the manifest with the identity fields that key a save
    /// to its name rewritten (Identity.MapStem, which ApplyToLegacy/ReconstructMapName prefers over the legacy JSON — leaving it stale
    /// would make the duplicate load back under the OLD name — and a new id), the blobs the manifest needs (a map's folder holds its
    /// own, so they are copied), and the JSON copy with the new MapName when the source has one. The source can be a map with a
    /// folder of its own or one that only exists in the earlier layout. Returns false if the source doesn't exist or a save already
    /// exists at newMapName.</summary>
    public static bool Duplicate(string sourceMapName, string newMapName)
    {
        var sourceHasShare = File.Exists(SC2SaveLayout.ShareManifestPath(SaveDirectory, sourceMapName))
                             || File.Exists(SC2SaveLayout.ShareJsonPath(SaveDirectory, sourceMapName));
        var sourceManifestPath = sourceHasShare
            ? SC2SaveLayout.ShareManifestPath(SaveDirectory, sourceMapName)
            : SC2SaveLayout.LegacyManifestPath(SaveDirectory, sourceMapName);
        var sourceJsonPath = sourceHasShare
            ? SC2SaveLayout.ShareJsonPath(SaveDirectory, sourceMapName)
            : SC2SaveLayout.LegacyJsonPath(SaveDirectory, sourceMapName);
        var hasManifest = File.Exists(sourceManifestPath);
        var hasJson = File.Exists(sourceJsonPath);
        if (!hasManifest && !hasJson)
            return false;

        if (Directory.Exists(SC2SaveLayout.ShareDirectory(SaveDirectory, newMapName))
            || File.Exists(SC2SaveLayout.LegacyJsonPath(SaveDirectory, newMapName)))
            return false;

        try
        {
            SC2SaveStorage.Prime();
            SC2SaveStorage.RegisterMap(sourceMapName);
            Directory.CreateDirectory(SC2SaveLayout.ShareDirectory(SaveDirectory, newMapName));

            string manifestJson = null;
            if (hasManifest)
            {
                var document = SC2MapDocumentSerializer.Deserialize(File.ReadAllText(sourceManifestPath, Encoding.UTF8));
                if (document.Identity == null)
                    document.Identity = new SC2MapIdentity();
                document.Identity.MapStem = newMapName;
                document.Identity.MapId = Guid.NewGuid().ToString("N");
                document.Identity.CreatedUtc = DateTime.UtcNow;
                document.Identity.ModifiedUtc = DateTime.UtcNow;
                manifestJson = SC2MapDocumentSerializer.Serialize(document);

                var destBlobs = SC2SaveStorage.OpenShareBlobs(newMapName, create: true);
                CopyBlobs(SC2BlobStoreGarbageCollector.CollectReferencedHashes(manifestJson), SC2SaveStorage.CreateSessionBlobStore(), destBlobs);
            }

            if (hasJson)
            {
                var dto = JsonUtility.FromJson<Dto>(File.ReadAllText(sourceJsonPath, Encoding.UTF8));
                if (dto != null)
                {
                    dto.MapName = newMapName;
                    WriteTextAtomic(SC2SaveLayout.ShareJsonPath(SaveDirectory, newMapName), JsonUtility.ToJson(dto));
                }
            }

            if (manifestJson != null)
                WriteTextAtomic(SC2SaveLayout.ShareManifestPath(SaveDirectory, newMapName), manifestJson);
            return true;
        }
        catch (Exception ex)
        {
            SC2Log.LogWarning($"[SC2RuntimeTerrainPersistence] Duplicate '{sourceMapName}' -> '{newMapName}' failed: {ex}");
            return false;
        }
    }

    // `document` is populated only when a manifest was read (null on the JSON fallback, which predates SC2MapDocument entirely) —
    // callers that need the richer document (currently: restoring Session.Document.Decals after a load) use it when present.
    /// <summary>Loads a saved map. The map's own folder wins: its manifest, then its JSON copy; only then the earlier layout in the
    /// save directory (manifest, then JSON). A source that fails to read (missing blob, corrupt file, a FormatVersion this build has
    /// no migration path for) falls through to the next one instead of surfacing an error, so a bug in one format cannot make a
    /// save that another still holds "not load". Nothing is written or deleted here.</summary>
    public static bool TryLoad(string mapName, out SC2TerrainData data, out SC2MapDocument document)
    {
        data = null;
        document = null;
        SC2SaveStorage.Prime();
        // The blobs in the map's own folder are read by whatever opens them later too (the session's store).
        SC2SaveStorage.RegisterMap(mapName);

        var shareManifest = SC2SaveLayout.ShareManifestPath(SaveDirectory, mapName);
        var shareJson = SC2SaveLayout.ShareJsonPath(SaveDirectory, mapName);
        var legacyManifest = SC2SaveLayout.LegacyManifestPath(SaveDirectory, mapName);
        var legacyJson = SC2SaveLayout.LegacyJsonPath(SaveDirectory, mapName);
        var order = SC2SaveLayout.LoadOrder(File.Exists(shareManifest), File.Exists(shareJson), File.Exists(legacyManifest), File.Exists(legacyJson));

        foreach (var source in order)
        {
            switch (source)
            {
                case SC2SaveLayout.Source.ShareManifest:
                    if (TryLoadManifest(mapName, shareManifest, out data, out document)) return true;
                    break;
                case SC2SaveLayout.Source.LegacyManifest:
                    if (TryLoadManifest(mapName, legacyManifest, out data, out document)) return true;
                    break;
                case SC2SaveLayout.Source.ShareJson:
                    if (TryLoadJson(mapName, shareJson, out data)) { document = null; return true; }
                    break;
                case SC2SaveLayout.Source.LegacyJson:
                    if (TryLoadJson(mapName, legacyJson, out data)) { document = null; return true; }
                    break;
            }
        }

        data = null;
        document = null;
        return false;
    }

    /// <summary>Reads the document and rebuilds the map from it. This is the highest-risk part of this class — a bug here reads a real
    /// saved map wrong or empty — so ANY failure at all (corrupt JSON, a blob that no store can resolve, any exception the reverse
    /// bridge itself throws) returns false and lets TryLoad try the next source.</summary>
    private static bool TryLoadManifest(string mapName, string manifestPath, out SC2TerrainData data, out SC2MapDocument document)
    {
        data = null;
        document = null;
        try
        {
            var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
            document = SC2MapDocumentSerializer.Deserialize(manifestJson);
            var sessionStore = SC2SaveStorage.CreateSessionBlobStore();
            // Textures are looked up as they always were; the ones the map carries are only a fallback for what this machine lacks.
            SC2MapshareTextures.Register(document.Terrain?.MapshareTextures, sessionStore);
            data = SC2MapDocumentToLegacyBridge.ApplyToLegacy(document, sessionStore);
            Debug.Log($"[SC2RuntimeTerrainPersistence] Loaded '{mapName}' from document package: {manifestPath}");
            return data != null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SC2RuntimeTerrainPersistence] Document package load failed for '{mapName}' from '{manifestPath}' " +
                              $"(trying the next source): {ex}");
            data = null;
            document = null;
            return false;
        }
    }

    private static bool TryLoadJson(string mapName, string jsonPath, out SC2TerrainData data)
    {
        data = null;
        try
        {
            var json = File.ReadAllText(jsonPath, Encoding.UTF8);
            var dto = JsonUtility.FromJson<Dto>(json);
            if (dto == null)
                return false;

            // Dto.FormatVersion has been written on every save since this class existed. dto.FormatVersion == 0 covers saves
            // written before the field existed at all (JsonUtility defaults a missing int field to 0), treated the same as
            // version 1 since the DTO shape hasn't changed yet. A save from a NEWER version is refused rather than misread.
            if (dto.FormatVersion > FormatVersion)
            {
                Debug.LogError($"[SC2RuntimeTerrainPersistence] Save '{mapName}' is FormatVersion {dto.FormatVersion}, " +
                                $"newer than this build understands ({FormatVersion}). Refusing to load it rather than " +
                                "risk misinterpreting fields this version doesn't know about.");
                return false;
            }

            data = FromDto(dto);
            if (data != null) Debug.Log($"[SC2RuntimeTerrainPersistence] Loaded '{mapName}' from legacy JSON: {jsonPath}");
            return data != null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SC2RuntimeTerrainPersistence] JSON load failed for '{mapName}' from '{jsonPath}': {ex}");
            data = null;
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
        public byte[] BlockedMask; // absent in older saves
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
        // Collision2 tab > Floor plane. Absent in older saves (off).
        public bool Collision2FloorEnabled;
        public float Collision2FloorHeight;
        // Collision2 tab > Custom area. Absent in older saves (off).
        public bool Collision2CustomArea;
        public float Collision2AreaMinX, Collision2AreaMinZ, Collision2AreaSizeX, Collision2AreaSizeZ;
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
        public string[] RnmShadowExclusions;
        // Environment > Point Clouds. PointCloudStored tells a save that has these from an older one where
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
            dto.CostsBlocks[i] = new CostsBlockDto { Name = costsBlocks[i].Name, Navmesh = costsBlocks[i].Navmesh ?? Array.Empty<byte>(), OutlineOpenMask = costsBlocks[i].OutlineOpenMask ?? Array.Empty<byte>(), BlockedMask = costsBlocks[i].BlockedMask ?? Array.Empty<byte>() };

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
        dto.Collision2FloorEnabled = data.Collision2FloorEnabled;
        dto.Collision2FloorHeight = data.Collision2FloorHeight;
        dto.Collision2CustomArea = data.Collision2CustomArea;
        dto.Collision2AreaMinX = data.Collision2AreaMinX; dto.Collision2AreaMinZ = data.Collision2AreaMinZ;
        dto.Collision2AreaSizeX = data.Collision2AreaSizeX; dto.Collision2AreaSizeZ = data.Collision2AreaSizeZ;
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
        dto.RnmShadowExclusions = data.RnmShadowExclusions != null ? data.RnmShadowExclusions.ToArray() : Array.Empty<string>();
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
            data.CostsBlocks.Add(new SC2CostsBlockData { Name = b.Name, Navmesh = b.Navmesh ?? Array.Empty<byte>(), OutlineOpenMask = b.OutlineOpenMask ?? Array.Empty<byte>(), BlockedMask = b.BlockedMask ?? Array.Empty<byte>() });

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
        data.Collision2FloorEnabled = dto.Collision2FloorEnabled;
        data.Collision2FloorHeight = dto.Collision2FloorHeight;
        data.Collision2CustomArea = dto.Collision2CustomArea;
        data.Collision2AreaMinX = dto.Collision2AreaMinX; data.Collision2AreaMinZ = dto.Collision2AreaMinZ;
        data.Collision2AreaSizeX = dto.Collision2AreaSizeX; data.Collision2AreaSizeZ = dto.Collision2AreaSizeZ;
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
        data.RnmShadowExclusions = dto.RnmShadowExclusions != null ? new List<string>(dto.RnmShadowExclusions) : new List<string>();
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
}
#endif
