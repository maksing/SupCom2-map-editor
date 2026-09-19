using System;
using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    public enum SC2DecalAtlasSource { None, Existing, Generated }
    public enum SC2DecalEntrySource { Manual, Generated }
    public enum SC2DecalsSpatialState { Missing, Original, Stale, Current }

    public sealed class SC2DecalAtlasEntry
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "Decal";
        public int X;
        public int Y;
        public int Width = 64;
        public int Height = 64;
        public float DefaultWorldWidth = 8f;
        public float DefaultWorldLength = 8f;
        public float DefaultProjectionDepth = 32f;
        public SC2DecalEntrySource Source;
        public List<string> Tags = new();

        public SC2DecalAtlasEntry Clone() => new()
        {
            Id = Id, Name = Name, X = X, Y = Y, Width = Width, Height = Height,
            DefaultWorldWidth = DefaultWorldWidth, DefaultWorldLength = DefaultWorldLength,
            DefaultProjectionDepth = DefaultProjectionDepth, Source = Source,
            Tags = new List<string>(Tags ?? new List<string>())
        };
    }

    public sealed class SC2DecalPlacement
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "Decal";
        public string AtlasEntryId = "";
        public float[] Position = new float[3];
        public float[] SurfaceNormal = new[] { 0f, 1f, 0f };
        public float Rotation;
        public float Width = 8f;
        public float Length = 8f;
        public float ProjectionDepth = 32f;
        public float HeightOffset = 0.03f;
        public bool AlignToSurface = true;
        public bool Enabled = true;
        // Requested directly: decals should respect the map's Symmetry
        // setting — placing one creates its mirrored/rotated siblings
        // together, and deleting/editing any member updates the whole set.
        // Empty SymmetryGroupId = not part of a group (e.g. Symmetry was
        // None at placement time, or the decal was duplicated singly).
        // SymmetrySlotIndex matches SC2DecalsShell.BuildSymmetrySlots' own
        // fixed slot order for whatever symmetry mode was active at
        // placement time (0 = the original click) — lets editing ANY
        // member (not just the original) correctly recover the group's
        // seed transform and regenerate every sibling from it.
        public string SymmetryGroupId = "";
        public int SymmetrySlotIndex;

        public SC2DecalPlacement Clone() => new()
        {
            Id = Id, Name = Name, AtlasEntryId = AtlasEntryId,
            Position = Clone3(Position, 0f, 0f, 0f),
            SurfaceNormal = Clone3(SurfaceNormal, 0f, 1f, 0f),
            Rotation = Rotation, Width = Width, Length = Length,
            ProjectionDepth = ProjectionDepth, HeightOffset = HeightOffset,
            AlignToSurface = AlignToSurface, Enabled = Enabled,
            SymmetryGroupId = SymmetryGroupId, SymmetrySlotIndex = SymmetrySlotIndex
        };

        private static float[] Clone3(float[] v, float x, float y, float z) =>
            v != null && v.Length >= 3 ? new[] { v[0], v[1], v[2] } : new[] { x, y, z };
    }

    /// <summary>Document-first decal state. Imported baked geometry is an
    /// immutable source layer; Entries/Placements are the authored state.</summary>
    public sealed class SC2DecalsDocument
    {
        public SC2BlobReference OriginalBdf;
        public bool IsEditable;
        public bool HasDecals;
        public int VertexCount;
        public int IndexCount;
        public string DiffuseAtlasPath = "";
        public string NormalAtlasPath = "";
        public SC2BlobReference DiffuseAtlasContent;
        public SC2BlobReference NormalAtlasContent;
        // Where the picked atlas texture originally came from, as the texture
        // resource tag ("archive:<entryPath>", "file:<path>") — the atlas
        // bytes themselves are content-hashed into DiffuseAtlasContent at pick
        // time, which loses that. Empty for anything that isn't an archive
        // pick. Lets export reference a vanilla-archive atlas by its real game
        // path instead of copying it (see SC2VanillaArchiveClassifier).
        public string DiffuseAtlasSourceTag = "";
        public string NormalAtlasSourceTag = "";
        // Per-atlas opt-in: copy the atlas into the exported map even when it
        // comes from a vanilla game archive. Default false.
        public bool DiffuseAtlasForceExport;
        public bool NormalAtlasForceExport;
        public int AtlasWidth;
        public int AtlasHeight;
        public SC2DecalAtlasSource AtlasSource;
        public bool KeepOriginalBakedLayer = true;
        public SC2DecalsSpatialState SpatialState;
        public List<SC2DecalAtlasEntry> Entries = new();
        public List<SC2DecalPlacement> Placements = new();
        public SC2DerivedState State;

        public bool HasAuthoredChanges => Entries.Count > 0 || Placements.Count > 0;

        public SC2DecalsDocument Clone() => new()
        {
            OriginalBdf = OriginalBdf, IsEditable = IsEditable, HasDecals = HasDecals,
            VertexCount = VertexCount, IndexCount = IndexCount,
            DiffuseAtlasPath = DiffuseAtlasPath, NormalAtlasPath = NormalAtlasPath,
            DiffuseAtlasContent = DiffuseAtlasContent, NormalAtlasContent = NormalAtlasContent,
            DiffuseAtlasSourceTag = DiffuseAtlasSourceTag, NormalAtlasSourceTag = NormalAtlasSourceTag,
            DiffuseAtlasForceExport = DiffuseAtlasForceExport, NormalAtlasForceExport = NormalAtlasForceExport,
            AtlasWidth = AtlasWidth, AtlasHeight = AtlasHeight, AtlasSource = AtlasSource,
            KeepOriginalBakedLayer = KeepOriginalBakedLayer, SpatialState = SpatialState,
            Entries = Entries.ConvertAll(x => x.Clone()),
            Placements = Placements.ConvertAll(x => x.Clone()), State = State
        };

        public void CopyFrom(SC2DecalsDocument other)
        {
            var c = other?.Clone() ?? new SC2DecalsDocument();
            OriginalBdf = c.OriginalBdf; IsEditable = c.IsEditable; HasDecals = c.HasDecals;
            VertexCount = c.VertexCount; IndexCount = c.IndexCount;
            DiffuseAtlasPath = c.DiffuseAtlasPath; NormalAtlasPath = c.NormalAtlasPath;
            DiffuseAtlasContent = c.DiffuseAtlasContent; NormalAtlasContent = c.NormalAtlasContent;
            DiffuseAtlasSourceTag = c.DiffuseAtlasSourceTag; NormalAtlasSourceTag = c.NormalAtlasSourceTag;
            DiffuseAtlasForceExport = c.DiffuseAtlasForceExport; NormalAtlasForceExport = c.NormalAtlasForceExport;
            AtlasWidth = c.AtlasWidth; AtlasHeight = c.AtlasHeight; AtlasSource = c.AtlasSource;
            KeepOriginalBakedLayer = c.KeepOriginalBakedLayer; SpatialState = c.SpatialState;
            Entries = c.Entries; Placements = c.Placements; State = c.State;
        }
    }
}
