using System;
using System.IO;
using BdfIO.Core;
using BdfIO.Formats.Decals;
using SC2.MapEditor.Document;

public static class SC2DecalsExportService
{
    /// <param name="liveTerrainSampleHeight">Optional — same live-core-
    /// terrain height source SC2DecalsShell wires for interactive placement
    /// (SC2LiveHeightGrid, via SC2TerrainShell), passed in explicitly here
    /// since this export path has no live shell reference of its own —
    /// the caller (SC2TerrainExporter.BuildExportArtifacts) builds a
    /// one-shot sampler from the hfieldModel it has already computed for
    /// hfield.bdf. Without this, a decal placed on the live procedural
    /// terrain (no queryable Collider — see SC2DecalSurfaceProvider's own
    /// doc comment) would export with no clip-target geometry at all.</param>
    public static byte[] Build(SC2MapSession session, string mapName, out byte[] diffuseAtlas, out byte[] normalAtlas,
        System.Func<float, float, float> liveTerrainSampleHeight = null)
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
        var authored = SC2DecalGeometryBuilder.Build(exportDocument, surfaces.Collect(exportDocument, liveTerrainSampleHeight));
        if (document.KeepOriginalBakedLayer && original != null)
            MergeOriginal(authored, original);

        DecalsSpatialBuilder.Rebuild(authored, useVanillaCandidateSplit: true);
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
