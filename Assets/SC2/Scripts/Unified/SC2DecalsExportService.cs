using System;
using System.IO;
using BdfIO.Core;
using BdfIO.Formats.Decals;
using BdfIO.Formats.Terrain;
using SC2.MapEditor.Document;

public static class SC2DecalsExportService
{
    /// <summary>What the most recent Build wrote - the Decals panel shows it. Vanilla maps ship
    /// roughly 15-40 thousand triangles in decals.bdf in total.</summary>
    public struct ExportStats
    {
        public int Triangles;
        public int Vertices;
        public int SourceTriangles;
    }

    public static ExportStats LastStats;

    /// <summary>Above this many triangles the panel warns - several times a
    /// vanilla map's total, where decals start to cost real frame time.</summary>
    public const int WarnTriangleCount = 100000;

    /// <param name="terrainModel">The final terrain model the export is about to write
    /// (SC2TerrainExporter.BuildExportArtifacts already merged the live plane and every
    /// imported terrain.bdf into it and applied Simplify). Decals are clipped against its
    /// own triangles, so they lie on exactly the surface the game renders. Null (nothing
    /// exported yet) falls back to whatever real meshes exist in the scene.</param>
    public static byte[] Build(SC2MapSession session, string mapName, out byte[] diffuseAtlas, out byte[] normalAtlas,
        TerrainModel terrainModel = null)
    {
        diffuseAtlas = null;
        normalAtlas = null;
        var document = session?.Document?.Decals;
        if (document == null)
            return null;

        var original = ReadOriginal(session, document);
        if (document.Placements.Count == 0)
            return original ?? BuildEmptyStub();

        var surfaces = new SC2DecalSurfaceProvider();
        diffuseAtlas = ReadBlob(session, document.DiffuseAtlasContent);
        normalAtlas = ReadBlob(session, document.NormalAtlasContent);
        var exportDocument = document.Clone();
        // An atlas picked from a vanilla game archive is referenced by its
        // real game path and NOT written into the map (a null byte[] here is
        // the caller's "nothing to write" signal) — unless this atlas's own
        // "copy into map on export" box is checked. Anything else (custom,
        // generated, non-vanilla archive) is exported as its own copy exactly
        // as before.
        var diffuseEntry = SC2VanillaArchiveClassifier.EntryPathFromTag(document.DiffuseAtlasSourceTag);
        if (diffuseAtlas != null && diffuseEntry != null &&
            SC2VanillaArchiveClassifier.ShouldSkipCopy(diffuseEntry, document.DiffuseAtlasForceExport))
        {
            exportDocument.DiffuseAtlasPath = SC2VanillaArchiveClassifier.ToGamePath(diffuseEntry);
            diffuseAtlas = null;
        }
        else if (diffuseAtlas != null) exportDocument.DiffuseAtlasPath = $"/maps/{mapName}/decals/decals_diffuse.dds";
        var normalEntry = SC2VanillaArchiveClassifier.EntryPathFromTag(document.NormalAtlasSourceTag);
        if (normalAtlas != null && normalEntry != null &&
            SC2VanillaArchiveClassifier.ShouldSkipCopy(normalEntry, document.NormalAtlasForceExport))
        {
            exportDocument.NormalAtlasPath = SC2VanillaArchiveClassifier.ToGamePath(normalEntry);
            normalAtlas = null;
        }
        else if (normalAtlas != null) exportDocument.NormalAtlasPath = $"/maps/{mapName}/decals/decals_normal.dds";
        exportDocument.DiffuseAtlasPath = NormalizeAtlasPath(exportDocument.DiffuseAtlasPath);
        exportDocument.NormalAtlasPath = NormalizeAtlasPath(exportDocument.NormalAtlasPath);
        var surfaceTriangles = terrainModel != null
            ? surfaces.CollectFromTerrainModel(exportDocument, terrainModel)
            : surfaces.Collect(exportDocument);
        // Only clipped-from-the-terrain-model sources carry the terrain tangent frame the game lights a
        // decal's normal map in (see SC2DecalGeometryBuilder.Build).
        var authored = SC2DecalGeometryBuilder.Build(exportDocument, surfaceTriangles, terrainTangentSpace: terrainModel != null);
        if (document.KeepOriginalBakedLayer && original != null)
            MergeOriginal(authored, original);

        DecalsSpatialBuilder.Rebuild(authored, useVanillaCandidateSplit: true);
        LastStats = new ExportStats
        {
            Triangles = authored.Indices.Count / 3,
            Vertices = authored.Vertices.Count,
            SourceTriangles = surfaceTriangles.Count
        };
        SC2Log.Log($"[SC2DecalsExportService] decals.bdf: {LastStats.Triangles:N0} triangles, {LastStats.Vertices:N0} vertices " +
                   $"(clipped from {LastStats.SourceTriangles:N0} terrain triangles under the decals).");

        var serialized = DecalsBdf.Serialize(authored);
        return new BdfContainer
        {
            Platform = BdfPlatform.Pc,
            Data = serialized.Data,
            OffsetTable = serialized.OffsetTable,
            NumFiles = serialized.NumFiles,
            TrailingData = serialized.TrailingData
        }.Write(DecalsBdf.Padding);
    }

    // Real vanilla maps with zero decals still ship a decals.win.bdf — a
    // genuine empty DecalsModel (Version=3, ExtraDataOffset=-1, geometry
    // sub-header VertexCount=0/IndexCount=0/offsets=-1), not an absent file.
    // Confirmed by byte-level inspection of a real reference file.
    private static byte[] BuildEmptyStub()
    {
        var serialized = DecalsBdf.Serialize(new DecalsModel());
        return new BdfContainer
        {
            Platform = BdfPlatform.Pc,
            Data = serialized.Data,
            OffsetTable = serialized.OffsetTable,
            NumFiles = serialized.NumFiles,
            TrailingData = serialized.TrailingData
        }.Write(DecalsBdf.Padding);
    }

    private static byte[] ReadOriginal(SC2MapSession session, SC2DecalsDocument document)
    {
        if (session.BlobStore == null || document.OriginalBdf.IsEmpty)
            return null;
        using var stream = session.BlobStore.OpenRead(document.OriginalBdf.ContentHash);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static byte[] ReadBlob(SC2MapSession session, SC2BlobReference reference)
    {
        if (session?.BlobStore == null || reference.IsEmpty) return null;
        using var stream = session.BlobStore.OpenRead(reference.ContentHash);
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }

    private static void MergeOriginal(DecalsModel target, byte[] originalBytes)
    {
        var original = DecalsBdf.Parse(BdfContainer.Read(originalBytes, DecalsBdf.Padding));
        if ((!string.IsNullOrEmpty(target.DiffuseAtlasPath) && !string.Equals(NormalizeAtlasPath(target.DiffuseAtlasPath), NormalizeAtlasPath(original.DiffuseAtlasPath), StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(target.NormalAtlasPath) && !string.Equals(NormalizeAtlasPath(target.NormalAtlasPath), NormalizeAtlasPath(original.NormalAtlasPath), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Original baked decals cannot be merged with authored decals that use a different atlas. Disable 'Keep original baked layer' or use the original atlas paths.");
        var offset = target.Vertices.Count;
        target.Vertices.AddRange(original.Vertices);
        foreach (var index in original.Indices)
            target.Indices.Add(index + offset);

        if (string.IsNullOrEmpty(target.DiffuseAtlasPath))
            target.DiffuseAtlasPath = NormalizeAtlasPath(original.DiffuseAtlasPath);
        if (string.IsNullOrEmpty(target.NormalAtlasPath))
            target.NormalAtlasPath = NormalizeAtlasPath(original.NormalAtlasPath);
    }

    private static string NormalizeAtlasPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? "";
        return path.EndsWith(".win.dds", StringComparison.OrdinalIgnoreCase)
            ? path.Substring(0, path.Length - ".win.dds".Length) + ".dds"
            : path;
    }
}
