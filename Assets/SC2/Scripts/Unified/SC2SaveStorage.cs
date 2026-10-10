using System.Collections.Generic;
using System.IO;
using UnityEngine;
using SC2.MapEditor.Document;

/// <summary>
/// Folders of the saved maps and the blob store of an editing session, shared by the Editor and the standalone build (the session
/// is created in both, the saving itself only exists in the build: SC2RuntimeTerrainPersistence). Where each map's files go is
/// SC2SaveLayout.
///
/// A session writes its new blobs to a cache in the temporary folder. The blobs of a map that was opened or saved are in that map's
/// own folder, so every folder visited in this session is also a place to read from, and so is the shared Blobs pool of the earlier
/// layout, which is never written.
/// </summary>
public static class SC2SaveStorage
{
    // Application.persistentDataPath and Application.temporaryCachePath may only be read on the main thread; they are read there
    // once (Prime) so the autosave's worker thread can use the paths too.
    private static string _saveDirectory;
    private static string _cacheBlobsDirectory;

    private static readonly object _lock = new object();
    private static readonly List<string> _visitedMaps = new List<string>();
    private static readonly Dictionary<string, ISC2BlobStore> _storesByDirectory = new Dictionary<string, ISC2BlobStore>();

    public static string SaveDirectory => _saveDirectory ??= Path.Combine(Application.persistentDataPath, "SC2Terrains");

    /// <summary>The working cache of new blobs; anything in it is either part of a saved map too or can be made again.</summary>
    public static string CacheBlobsDirectory => _cacheBlobsDirectory ??= Path.Combine(Application.temporaryCachePath, "SC2Blobs");

    /// <summary>Reads the paths that only the main thread may read.</summary>
    public static void Prime()
    {
        _ = SaveDirectory;
        _ = CacheBlobsDirectory;
    }

    /// <summary>Makes the blobs in a map's folder readable by the session. Call it for a map that was opened or just saved.</summary>
    public static void RegisterMap(string mapName)
    {
        if (string.IsNullOrEmpty(mapName)) return;
        lock (_lock)
        {
            if (!_visitedMaps.Contains(mapName)) _visitedMaps.Add(mapName);
        }
    }

    /// <summary>The blob store of an editing session.</summary>
    public static ISC2BlobStore CreateSessionBlobStore()
    {
        Prime();
        return new SC2LayeredBlobStore(StoreFor(CacheBlobsDirectory, create: true), ReadFallbacks);
    }

    /// <summary>The working cache on its own, for the save, which copies what a map needs into the map's folder.</summary>
    public static ISC2BlobStore OpenCache()
    {
        Prime();
        return StoreFor(CacheBlobsDirectory, create: true);
    }

    /// <summary>The store of one map's own folder, or null when the folder has no blobs yet.</summary>
    public static ISC2BlobStore OpenShareBlobs(string mapName, bool create)
    {
        Prime();
        return StoreFor(SC2SaveLayout.ShareBlobsDirectory(SaveDirectory, mapName), create);
    }

    private static IEnumerable<ISC2BlobStore> ReadFallbacks()
    {
        var stores = new List<ISC2BlobStore>();
        string[] maps;
        lock (_lock) maps = _visitedMaps.ToArray();
        foreach (var map in maps)
        {
            var store = StoreFor(SC2SaveLayout.ShareBlobsDirectory(SaveDirectory, map), create: false);
            if (store != null) stores.Add(store);
        }
        var legacy = StoreFor(SC2SaveLayout.LegacyBlobsDirectory(SaveDirectory), create: false);
        if (legacy != null) stores.Add(legacy);
        return stores;
    }

    private static ISC2BlobStore StoreFor(string directory, bool create)
    {
        lock (_lock)
        {
            if (_storesByDirectory.TryGetValue(directory, out var existing)) return existing;
            // The store creates its directory when it is made; a folder that is not there is left alone, so that reading never
            // leaves empty Blobs folders behind.
            if (!create && !Directory.Exists(directory)) return null;
            var store = new SC2DirectoryBlobStore(directory);
            _storesByDirectory[directory] = store;
            return store;
        }
    }
}
