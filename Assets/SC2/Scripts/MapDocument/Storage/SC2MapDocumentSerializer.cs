using System;
using System.Collections.Generic;
using System.Globalization;
using BdfIO.Core;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Reads and writes the
    /// document manifest, covering all 8 domain
    /// sub-documents — a complete
    /// snapshot of SC2MapDocument, not just its identity/provenance shell.
    /// Deliberately maps every field explicitly rather than reflecting over
    /// the typed model: reflection would make it impossible to tell, just by
    /// reading this file, which fields are actually part of the on-disk
    /// contract — and that contract is exactly what ISC2DocumentMigration
    /// steps need to reason about precisely.
    ///
    /// Blob-backed fields (SC2BlobReference/SC2MeshBlobReference) only ever
    /// write/read the reference itself (contentHash + length) — never the
    /// underlying bytes, which live in the blob store's own directory, not
    /// in this manifest. Environment's OriginalHeader/WorkingHeader are the
    /// one exception (a small fixed-size byte[508] inline field, not blob-
    /// referenced), written as base64.
    /// </summary>
    public static class SC2MapDocumentSerializer
    {
        // No default migrations exist yet — CurrentFormatVersion has never
        // changed since this serializer was introduced. Deliberately NOT a
        // static mutable registry (an earlier draft of this file had
        // RegisterMigration(...) appending to a shared static list, which
        // means the exact set of applicable migrations would depend on
        // whatever else happened to run earlier in the process — a real
        // correctness hazard, caught while writing standalone tests for
        // this file). Callers pass the migrations that apply explicitly.
        public static string Serialize(SC2MapDocument document)
        {
            var root = SC2JsonValue.NewObject();
            root.Set("formatVersion", SC2JsonValue.Of(document.FormatVersion));
            root.Set("documentId", SC2JsonValue.Of(document.DocumentId));
            root.Set("revision", SC2JsonValue.Of(document.Revision));
            root.Set("identity", WriteIdentity(document.Identity));
            root.Set("source", WriteSource(document.Source));
            root.Set("entryPoint", WriteEntryPoint(document.EntryPoint));
            root.Set("archive", WriteArchive(document.Archive));
            root.Set("environment", WriteEnvironment(document.Environment));
            root.Set("collision2", WriteCollision2(document.Collision2));
            root.Set("costs", WriteCosts(document.Costs));
            root.Set("hfield", WriteHfield(document.Hfield));
            root.Set("objects", WriteObjects(document.Objects));
            root.Set("terrain", WriteTerrain(document.Terrain));
            root.Set("scenario", WriteScenario(document.Scenario));
            root.Set("decals", WriteDecals(document.Decals));
            return root.Write();
        }

        public static SC2MapDocument Deserialize(string manifestJson, IReadOnlyList<ISC2DocumentMigration> migrations = null)
        {
            var root = SC2JsonValue.Parse(manifestJson);
            root = ApplyMigrations(root, migrations ?? Array.Empty<ISC2DocumentMigration>());

            var document = new SC2MapDocument
            {
                FormatVersion = root.Get("formatVersion").AsInt(SC2MapDocument.CurrentFormatVersion),
                Identity = ReadIdentity(root.Get("identity")),
                Source = ReadSource(root.Get("source")),
                EntryPoint = ReadEntryPoint(root.Get("entryPoint")),
                Archive = ReadArchive(root.Get("archive")),
                Environment = ReadEnvironment(root.Get("environment")),
                Collision2 = ReadCollision2(root.Get("collision2")),
                Costs = ReadCosts(root.Get("costs")),
                Hfield = ReadHfield(root.Get("hfield")),
                Objects = ReadObjects(root.Get("objects")),
                Terrain = ReadTerrain(root.Get("terrain")),
                Scenario = ReadScenario(root.Get("scenario")),
                Decals = ReadDecals(root.Get("decals")),
            };
            return document;
        }

        private static SC2JsonValue ApplyMigrations(SC2JsonValue root, IReadOnlyList<ISC2DocumentMigration> migrations)
        {
            int version = root.Get("formatVersion").AsInt(1);
            while (version < SC2MapDocument.CurrentFormatVersion)
            {
                ISC2DocumentMigration next = null;
                foreach (var m in migrations)
                {
                    if (m.FromVersion == version) { next = m; break; }
                }
                if (next == null)
                    throw new InvalidOperationException(
                        $"No migration registered to advance manifest from formatVersion {version} to {SC2MapDocument.CurrentFormatVersion}.");

                next.Apply(root);
                version++;
                root.Set("formatVersion", SC2JsonValue.Of(version));
            }
            return root;
        }

        // ---- Identity ----

        private static SC2JsonValue WriteIdentity(SC2MapIdentity id)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("mapId", SC2JsonValue.Of(id.MapId));
            o.Set("displayName", SC2JsonValue.Of(id.DisplayName));
            o.Set("mapStem", SC2JsonValue.Of(id.MapStem));
            o.Set("author", SC2JsonValue.Of(id.Author));
            o.Set("description", SC2JsonValue.Of(id.Description));
            o.Set("createdUtc", SC2JsonValue.Of(id.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)));
            o.Set("modifiedUtc", SC2JsonValue.Of(id.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture)));
            return o;
        }

        private static SC2MapIdentity ReadIdentity(SC2JsonValue o)
        {
            var id = new SC2MapIdentity
            {
                MapId = o.Get("mapId").AsString(Guid.NewGuid().ToString("N")),
                DisplayName = o.Get("displayName").AsString(""),
                MapStem = o.Get("mapStem").AsString(""),
                Author = o.Get("author").AsString(""),
                Description = o.Get("description").AsString(""),
            };
            id.CreatedUtc = ParseUtc(o.Get("createdUtc"), id.CreatedUtc);
            id.ModifiedUtc = ParseUtc(o.Get("modifiedUtc"), id.ModifiedUtc);
            return id;
        }

        // ---- Source ----

        private static SC2JsonValue WriteSource(SC2MapSource s)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("kind", SC2JsonValue.Of(s.Kind.ToString()));
            o.Set("containerKind", SC2JsonValue.Of(s.ContainerKind.ToString()));
            o.Set("originalPath", SC2JsonValue.Of(s.OriginalPath));
            o.Set("originalFileName", SC2JsonValue.Of(s.OriginalFileName));
            o.Set("originalFingerprint", SC2JsonValue.Of(s.OriginalFingerprint));
            o.Set("importedUtc", SC2JsonValue.Of(s.ImportedUtc.ToString("O", CultureInfo.InvariantCulture)));
            o.Set("originalStillAvailable", SC2JsonValue.Of(s.OriginalStillAvailable));
            return o;
        }

        private static SC2MapSource ReadSource(SC2JsonValue o)
        {
            var s = new SC2MapSource
            {
                Kind = ParseEnum(o.Get("kind"), SC2MapSourceKind.NewProcedural),
                ContainerKind = ParseEnum(o.Get("containerKind"), SC2SourceContainerKind.None),
                OriginalPath = o.Get("originalPath").AsString(null),
                OriginalFileName = o.Get("originalFileName").AsString(null),
                OriginalFingerprint = o.Get("originalFingerprint").AsString(null),
                OriginalStillAvailable = o.Get("originalStillAvailable").AsBool(false),
            };
            s.ImportedUtc = ParseUtc(o.Get("importedUtc"), s.ImportedUtc);
            return s;
        }

        // ---- EntryPoint ----

        private static SC2JsonValue WriteEntryPoint(SC2MapEntryPoint e)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("scenarioEntryPath", SC2JsonValue.Of(e.ScenarioEntryPath));
            o.Set("scenarioEntrySha256", SC2JsonValue.Of(e.ScenarioEntrySha256));
            o.Set("mapPath", SC2JsonValue.Of(e.MapPath));
            o.Set("savePath", SC2JsonValue.Of(e.SavePath));
            o.Set("scriptPath", SC2JsonValue.Of(e.ScriptPath));
            o.Set("bdfDirectory", SC2JsonValue.Of(e.BdfDirectory));
            o.Set("bdfStem", SC2JsonValue.Of(e.BdfStem));
            o.Set("platform", SC2JsonValue.Of(e.Platform.ToString()));
            o.Set("resolutionMethod", SC2JsonValue.Of(e.ResolutionMethod));
            var warnings = SC2JsonValue.NewArray();
            foreach (var w in e.ResolutionWarnings) warnings.Add(SC2JsonValue.Of(w));
            o.Set("resolutionWarnings", warnings);
            return o;
        }

        private static SC2MapEntryPoint ReadEntryPoint(SC2JsonValue o)
        {
            var e = new SC2MapEntryPoint
            {
                ScenarioEntryPath = o.Get("scenarioEntryPath").AsString(null),
                ScenarioEntrySha256 = o.Get("scenarioEntrySha256").AsString(null),
                MapPath = o.Get("mapPath").AsString(null),
                SavePath = o.Get("savePath").AsString(null),
                ScriptPath = o.Get("scriptPath").AsString(null),
                BdfDirectory = o.Get("bdfDirectory").AsString(null),
                BdfStem = o.Get("bdfStem").AsString(null),
                Platform = ParseEnum(o.Get("platform"), BdfPlatform.Pc),
                ResolutionMethod = o.Get("resolutionMethod").AsString(null),
            };
            foreach (var w in o.Get("resolutionWarnings").Items())
                e.ResolutionWarnings.Add(w.AsString(""));
            return e;
        }

        // ---- Archive ----

        private static SC2JsonValue WriteArchive(SC2ArchiveDocument a)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("sourceContainerFingerprint", SC2JsonValue.Of(a.SourceContainerFingerprint));
            o.Set("sourceContainerKind", SC2JsonValue.Of(a.SourceContainerKind.ToString()));
            o.Set("selectedScenarioEntryPath", SC2JsonValue.Of(a.SelectedScenarioEntryPath));
            var entries = SC2JsonValue.NewArray();
            foreach (var entry in a.Entries) entries.Add(WriteArchiveEntry(entry));
            o.Set("entries", entries);
            return o;
        }

        private static SC2ArchiveDocument ReadArchive(SC2JsonValue o)
        {
            var a = new SC2ArchiveDocument
            {
                SourceContainerFingerprint = o.Get("sourceContainerFingerprint").AsString(null),
                SourceContainerKind = ParseEnum(o.Get("sourceContainerKind"), SC2SourceContainerKind.None),
                SelectedScenarioEntryPath = o.Get("selectedScenarioEntryPath").AsString(null),
            };
            foreach (var entryJson in o.Get("entries").Items())
                a.Entries.Add(ReadArchiveEntry(entryJson));
            return a;
        }

        private static SC2JsonValue WriteArchiveEntry(SC2ArchiveEntry entry)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("originalPath", SC2JsonValue.Of(entry.OriginalPath));
            o.Set("normalizedPath", SC2JsonValue.Of(entry.NormalizedPath));
            o.Set("uncompressedLength", SC2JsonValue.Of(entry.UncompressedLength));
            o.Set("sha256", SC2JsonValue.Of(entry.Sha256));
            o.Set("role", SC2JsonValue.Of(entry.Role.ToString()));
            if (!entry.OriginalContent.IsEmpty)
            {
                var blob = SC2JsonValue.NewObject();
                blob.Set("contentHash", SC2JsonValue.Of(entry.OriginalContent.ContentHash));
                blob.Set("length", SC2JsonValue.Of(entry.OriginalContent.Length));
                o.Set("originalContent", blob);
            }
            return o;
        }

        private static SC2ArchiveEntry ReadArchiveEntry(SC2JsonValue o)
        {
            var entry = new SC2ArchiveEntry
            {
                OriginalPath = o.Get("originalPath").AsString(null),
                NormalizedPath = o.Get("normalizedPath").AsString(null),
                UncompressedLength = o.Get("uncompressedLength").AsLong(0),
                Sha256 = o.Get("sha256").AsString(null),
                Role = ParseEnum(o.Get("role"), SC2ArchiveEntryRole.Unknown),
            };
            if (o.TryGet("originalContent", out var blob))
            {
                entry.OriginalContent = new SC2BlobReference(
                    blob.Get("contentHash").AsString(null),
                    blob.Get("length").AsLong(0));
            }
            return entry;
        }

        // ---- Environment ----

        private static SC2JsonValue WriteEnvironment(SC2EnvironmentDocument e)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("originalHeader", SC2JsonValue.Of(Convert.ToBase64String(e.OriginalHeader ?? Array.Empty<byte>())));
            o.Set("workingHeader", SC2JsonValue.Of(Convert.ToBase64String(e.WorkingHeader ?? Array.Empty<byte>())));
            o.Set("environmentMap", SC2JsonValue.Of(e.EnvironmentMap));
            o.Set("skyMap", SC2JsonValue.Of(e.SkyMap));
            o.Set("texturePath3", SC2JsonValue.Of(e.TexturePath3));
            o.Set("colorCorrectionCube", SC2JsonValue.Of(e.ColorCorrectionCube));
            o.Set("fogOfWarCorrectionCube", SC2JsonValue.Of(e.FogOfWarCorrectionCube));
            o.Set("waterPlaneSize", SC2JsonValue.Of(e.WaterPlaneSize));
            o.Set("waterUvScale", SC2JsonValue.Of(e.WaterUvScale));
            var waterTexturePaths = SC2JsonValue.NewArray();
            foreach (var p in e.WaterTexturePaths ?? Array.Empty<string>()) waterTexturePaths.Add(SC2JsonValue.Of(p));
            o.Set("waterTexturePaths", waterTexturePaths);
            o.Set("waterDepthAutoGenerate", SC2JsonValue.Of(e.WaterDepthAutoGenerate));
            o.Set("waterDepthResolution", SC2JsonValue.Of(e.WaterDepthResolution));
            o.Set("waterDepthMargin", SC2JsonValue.Of(e.WaterDepthMargin));
            o.Set("waterDepthFalloffPower", SC2JsonValue.Of(e.WaterDepthFalloffPower));
            o.Set("waterDepthMinAlpha", SC2JsonValue.Of(e.WaterDepthMinAlpha));
            o.Set("waterDepthPaintBorder", SC2JsonValue.Of(e.WaterDepthPaintBorder));
            o.Set("waterDepthImageBorderOutline", SC2JsonValue.Of(e.WaterDepthImageBorderOutline));
            var unknowns = SC2JsonValue.NewArray();
            foreach (var f in e.UnknownFields)
            {
                var fo = SC2JsonValue.NewObject();
                fo.Set("byteOffset", SC2JsonValue.Of(f.ByteOffset));
                fo.Set("note", SC2JsonValue.Of(f.Note));
                unknowns.Add(fo);
            }
            o.Set("unknownFields", unknowns);
            return o;
        }

        private static SC2EnvironmentDocument ReadEnvironment(SC2JsonValue o)
        {
            var e = new SC2EnvironmentDocument
            {
                OriginalHeader = ReadBase64Bytes(o.Get("originalHeader"), SC2EnvironmentDocument.HeaderSize),
                WorkingHeader = ReadBase64Bytes(o.Get("workingHeader"), SC2EnvironmentDocument.HeaderSize),
                EnvironmentMap = o.Get("environmentMap").AsString(""),
                SkyMap = o.Get("skyMap").AsString(""),
                TexturePath3 = o.Get("texturePath3").AsString(""),
                ColorCorrectionCube = o.Get("colorCorrectionCube").AsString(""),
                FogOfWarCorrectionCube = o.Get("fogOfWarCorrectionCube").AsString(""),
                WaterPlaneSize = o.Get("waterPlaneSize").AsInt(4096),
                WaterUvScale = (float)o.Get("waterUvScale").AsDouble(1024f),
                WaterDepthAutoGenerate = o.Get("waterDepthAutoGenerate").AsBool(false),
                WaterDepthResolution = o.Get("waterDepthResolution").AsInt(512),
                WaterDepthMargin = (float)o.Get("waterDepthMargin").AsDouble(64f),
                WaterDepthFalloffPower = (float)o.Get("waterDepthFalloffPower").AsDouble(2f),
                WaterDepthMinAlpha = (float)o.Get("waterDepthMinAlpha").AsDouble(0.04f),
                WaterDepthPaintBorder = o.Get("waterDepthPaintBorder").AsBool(true),
                WaterDepthImageBorderOutline = o.Get("waterDepthImageBorderOutline").AsBool(false),
            };
            var waterTexturePaths = new List<string>();
            foreach (var v in o.Get("waterTexturePaths").Items()) waterTexturePaths.Add(v.AsString(""));
            e.WaterTexturePaths = waterTexturePaths.ToArray();
            foreach (var fo in o.Get("unknownFields").Items())
                e.UnknownFields.Add(new SC2UnknownInfoField
                {
                    ByteOffset = fo.Get("byteOffset").AsInt(0),
                    Note = fo.Get("note").AsString(""),
                });
            return e;
        }

        // ---- Collision2 ----

        private static SC2JsonValue WriteCollision2(SC2CollisionDocument c)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("sourceMode", SC2JsonValue.Of(c.SourceMode.ToString()));
            o.Set("mesh", WriteMeshBlobRef(c.Mesh));
            var gen = SC2JsonValue.NewObject();
            gen.Set("maxPolygons", SC2JsonValue.Of(c.Generation.MaxPolygons));
            gen.Set("verticalOffset", SC2JsonValue.Of(c.Generation.VerticalOffset));
            o.Set("generation", gen);
            o.Set("sourceHfieldRevision", SC2JsonValue.Of(c.SourceHfieldRevision));
            o.Set("state", SC2JsonValue.Of(c.State.ToString()));
            return o;
        }

        private static SC2CollisionDocument ReadCollision2(SC2JsonValue o)
        {
            var gen = o.Get("generation");
            return new SC2CollisionDocument
            {
                SourceMode = ParseEnum(o.Get("sourceMode"), SC2CollisionSourceMode.OriginalPassthrough),
                Mesh = ReadMeshBlobRef(o.Get("mesh")),
                Generation = new SC2CollisionGenerationSettings
                {
                    MaxPolygons = gen.Get("maxPolygons").AsInt(2500),
                    VerticalOffset = (float)gen.Get("verticalOffset").AsDouble(0),
                },
                SourceHfieldRevision = o.Get("sourceHfieldRevision").AsLong(0),
                State = ParseEnum(o.Get("state"), SC2DerivedState.Original),
            };
        }

        // ---- Costs ----

        private static SC2JsonValue WriteCosts(SC2CostsDocument c)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("profile", SC2JsonValue.Of(c.Profile.ToString()));
            var layers = SC2JsonValue.NewArray();
            foreach (var l in c.Layers) layers.Add(WriteCostsLayer(l));
            o.Set("layers", layers);
            o.Set("sourceHfieldRevision", SC2JsonValue.Of(c.SourceHfieldRevision));
            o.Set("state", SC2JsonValue.Of(c.State.ToString()));
            return o;
        }

        private static SC2CostsDocument ReadCosts(SC2JsonValue o)
        {
            var c = new SC2CostsDocument
            {
                Profile = ParseEnum(o.Get("profile"), SC2CostsProfile.LandOnly),
                SourceHfieldRevision = o.Get("sourceHfieldRevision").AsLong(0),
                State = ParseEnum(o.Get("state"), SC2DerivedState.Original),
            };
            foreach (var lo in o.Get("layers").Items())
                c.Layers.Add(ReadCostsLayer(lo));
            return c;
        }

        private static SC2JsonValue WriteCostsLayer(SC2CostsLayerDocument l)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(l.Id));
            o.Set("fieldName", SC2JsonValue.Of(l.FieldName));
            o.Set("displayName", SC2JsonValue.Of(l.DisplayName));
            o.Set("baseGeneratedGrid", WriteBlobRef(l.BaseGeneratedGrid));
            o.Set("paintOverrides", WriteBlobRef(l.PaintOverrides));
            o.Set("finalGrid", WriteBlobRef(l.FinalGrid));
            var islands = SC2JsonValue.NewObject();
            islands.Set("checked", SC2JsonValue.Of(l.Islands.Checked));
            islands.Set("hasUnreachableIslands", SC2JsonValue.Of(l.Islands.HasUnreachableIslands));
            islands.Set("islandCount", SC2JsonValue.Of(l.Islands.IslandCount));
            o.Set("islands", islands);
            return o;
        }

        private static SC2CostsLayerDocument ReadCostsLayer(SC2JsonValue o)
        {
            var islands = o.Get("islands");
            return new SC2CostsLayerDocument
            {
                Id = o.Get("id").AsString(""),
                FieldName = o.Get("fieldName").AsString(""),
                DisplayName = o.Get("displayName").AsString(""),
                BaseGeneratedGrid = ReadBlobRef(o.Get("baseGeneratedGrid")),
                PaintOverrides = ReadBlobRef(o.Get("paintOverrides")),
                FinalGrid = ReadBlobRef(o.Get("finalGrid")),
                Islands = new SC2IslandValidationResult
                {
                    Checked = islands.Get("checked").AsBool(false),
                    HasUnreachableIslands = islands.Get("hasUnreachableIslands").AsBool(false),
                    IslandCount = islands.Get("islandCount").AsInt(0),
                },
            };
        }

        // ---- Hfield ----

        private static SC2JsonValue WriteHfield(SC2HfieldDocument h)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("mapX", SC2JsonValue.Of(h.MapX));
            o.Set("mapY", SC2JsonValue.Of(h.MapY));
            o.Set("playableRect", WriteRect(h.PlayableRect));
            o.Set("heightmapUInt16", WriteBlobRef(h.HeightmapUInt16));
            o.Set("terrainTypesUInt8", WriteBlobRef(h.TerrainTypesUInt8));
            var proj = SC2JsonValue.NewObject();
            proj.Set("width", SC2JsonValue.Of(h.Projection.Width));
            proj.Set("height", SC2JsonValue.Of(h.Projection.Height));
            proj.Set("worldBounds", WriteRect(h.Projection.WorldBounds));
            proj.Set("topHitPolicy", SC2JsonValue.Of(h.Projection.TopHitPolicy.ToString()));
            proj.Set("gapFillPolicy", SC2JsonValue.Of(h.Projection.GapFillPolicy.ToString()));
            proj.Set("includedLayerIds", WriteStringList(h.Projection.IncludedLayerIds));
            proj.Set("excludedLayerIds", WriteStringList(h.Projection.ExcludedLayerIds));
            proj.Set("excludedMaterialPairIds", WriteStringList(h.Projection.ExcludedMaterialPairIds));
            o.Set("projection", proj);
            o.Set("sourceTerrainRevision", SC2JsonValue.Of(h.SourceTerrainRevision));
            o.Set("state", SC2JsonValue.Of(h.State.ToString()));
            return o;
        }

        private static SC2HfieldDocument ReadHfield(SC2JsonValue o)
        {
            var proj = o.Get("projection");
            return new SC2HfieldDocument
            {
                MapX = o.Get("mapX").AsInt(0),
                MapY = o.Get("mapY").AsInt(0),
                PlayableRect = ReadRect(o.Get("playableRect")),
                HeightmapUInt16 = ReadBlobRef(o.Get("heightmapUInt16")),
                TerrainTypesUInt8 = ReadBlobRef(o.Get("terrainTypesUInt8")),
                Projection = new SC2HfieldProjectionSettings
                {
                    Width = proj.Get("width").AsInt(0),
                    Height = proj.Get("height").AsInt(0),
                    WorldBounds = ReadRect(proj.Get("worldBounds")),
                    TopHitPolicy = ParseEnum(proj.Get("topHitPolicy"), SC2TopHitPolicy.HighestSurface),
                    GapFillPolicy = ParseEnum(proj.Get("gapFillPolicy"), SC2GapFillPolicy.None),
                    IncludedLayerIds = ReadStringList(proj.Get("includedLayerIds")),
                    ExcludedLayerIds = ReadStringList(proj.Get("excludedLayerIds")),
                    ExcludedMaterialPairIds = ReadStringList(proj.Get("excludedMaterialPairIds")),
                },
                SourceTerrainRevision = o.Get("sourceTerrainRevision").AsLong(0),
                State = ParseEnum(o.Get("state"), SC2DerivedState.Original),
            };
        }

        // ---- Objects ----

        private static SC2JsonValue WriteObjects(SC2ObjectsDocument d)
        {
            var o = SC2JsonValue.NewObject();
            var props = SC2JsonValue.NewArray();
            foreach (var p in d.Props) props.Add(WriteProp(p));
            o.Set("props", props);
            var markers = SC2JsonValue.NewArray();
            foreach (var m in d.Markers) markers.Add(WriteMarker(m));
            o.Set("markers", markers);
            var graphs = SC2JsonValue.NewArray();
            foreach (var g in d.Graphs) graphs.Add(WriteGraph(g));
            o.Set("graphs", graphs);
            var armies = SC2JsonValue.NewArray();
            foreach (var a in d.Armies) armies.Add(WriteArmy(a));
            o.Set("armies", armies);
            var units = SC2JsonValue.NewArray();
            foreach (var u in d.Units) units.Add(WriteUnit(u));
            o.Set("units", units);
            var zones = SC2JsonValue.NewArray();
            foreach (var z in d.Zones) zones.Add(WriteZone(z));
            o.Set("zones", zones);
            var chains = SC2JsonValue.NewArray();
            foreach (var c in d.Chains) chains.Add(WriteChain(c));
            o.Set("chains", chains);
            var symmetryGroups = SC2JsonValue.NewArray();
            foreach (var sg in d.SymmetryGroups) symmetryGroups.Add(WriteSymmetryGroup(sg));
            o.Set("symmetryGroups", symmetryGroups);
            var referenceUnits = SC2JsonValue.NewArray();
            foreach (var r in d.ReferenceUnits) referenceUnits.Add(WriteReferenceUnit(r));
            o.Set("referenceUnits", referenceUnits);
            o.Set("nextObjectId", SC2JsonValue.Of(d.NextObjectId));
            o.Set("saveLuaTextOriginal", SC2JsonValue.Of(d.SaveLuaTextOriginal));
            o.Set("scenarioLuaTextOriginal", SC2JsonValue.Of(d.ScenarioLuaTextOriginal));
            return o;
        }

        private static SC2ObjectsDocument ReadObjects(SC2JsonValue o)
        {
            var d = new SC2ObjectsDocument
            {
                NextObjectId = o.Get("nextObjectId").AsInt(0),
                SaveLuaTextOriginal = o.Get("saveLuaTextOriginal").AsString(""),
                ScenarioLuaTextOriginal = o.Get("scenarioLuaTextOriginal").AsString(""),
            };
            foreach (var v in o.Get("props").Items()) d.Props.Add(ReadProp(v));
            foreach (var v in o.Get("markers").Items()) d.Markers.Add(ReadMarker(v));
            foreach (var v in o.Get("graphs").Items()) d.Graphs.Add(ReadGraph(v));
            foreach (var v in o.Get("armies").Items()) d.Armies.Add(ReadArmy(v));
            foreach (var v in o.Get("units").Items()) d.Units.Add(ReadUnit(v));
            foreach (var v in o.Get("zones").Items()) d.Zones.Add(ReadZone(v));
            foreach (var v in o.Get("chains").Items()) d.Chains.Add(ReadChain(v));
            foreach (var v in o.Get("symmetryGroups").Items()) d.SymmetryGroups.Add(ReadSymmetryGroup(v));
            foreach (var v in o.Get("referenceUnits").Items()) d.ReferenceUnits.Add(ReadReferenceUnit(v));
            return d;
        }

        private static SC2JsonValue WriteProp(SC2ObjProp p)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(p.Id));
            o.Set("blueprintPath", SC2JsonValue.Of(p.BlueprintPath));
            o.Set("position", WriteVector3(p.Position));
            o.Set("rotation", WriteQuaternion(p.Rotation));
            o.Set("scale", SC2JsonValue.Of(p.Scale));
            o.Set("flags", SC2JsonValue.Of(p.Flags));
            o.Set("anchorToTerrain", SC2JsonValue.Of(p.AnchorToTerrain));
            o.Set("heightOffset", SC2JsonValue.Of(p.HeightOffset));
            return o;
        }

        private static SC2ObjProp ReadProp(SC2JsonValue o) => new SC2ObjProp
        {
            Id = o.Get("id").AsInt(0),
            BlueprintPath = o.Get("blueprintPath").AsString(""),
            Position = ReadVector3(o.Get("position")),
            Rotation = ReadQuaternion(o.Get("rotation")),
            Scale = (float)o.Get("scale").AsDouble(1),
            Flags = (uint)o.Get("flags").AsLong(0),
            AnchorToTerrain = o.Get("anchorToTerrain").AsBool(true),
            HeightOffset = (float)o.Get("heightOffset").AsDouble(0),
        };

        private static SC2JsonValue WriteMarker(SC2ObjMarker m)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(m.Id));
            o.Set("name", SC2JsonValue.Of(m.Name));
            o.Set("type", SC2JsonValue.Of(m.Type));
            o.Set("position", WriteVector3(m.Position));
            o.Set("orientationEuler", WriteVector3(m.OrientationEuler));
            o.Set("orientationIsQuaternion", SC2JsonValue.Of(m.OrientationIsQuaternion));
            o.Set("colorHex", SC2JsonValue.Of(m.ColorHex));
            o.Set("prop", SC2JsonValue.Of(m.Prop));
            o.Set("extraFields", WriteLuaFields(m.ExtraFields));
            o.Set("anchorToTerrain", SC2JsonValue.Of(m.AnchorToTerrain));
            o.Set("heightOffset", SC2JsonValue.Of(m.HeightOffset));
            return o;
        }

        private static SC2ObjMarker ReadMarker(SC2JsonValue o) => new SC2ObjMarker
        {
            Id = o.Get("id").AsInt(0),
            Name = o.Get("name").AsString(""),
            Type = o.Get("type").AsString(""),
            Position = ReadVector3(o.Get("position")),
            OrientationEuler = ReadVector3(o.Get("orientationEuler")),
            OrientationIsQuaternion = o.Get("orientationIsQuaternion").AsBool(true),
            ColorHex = o.Get("colorHex").AsString("ffffffff"),
            Prop = o.Get("prop").AsString(""),
            ExtraFields = ReadLuaFields(o.Get("extraFields")),
            AnchorToTerrain = o.Get("anchorToTerrain").AsBool(true),
            HeightOffset = (float)o.Get("heightOffset").AsDouble(0),
        };

        private static SC2JsonValue WriteGraph(SC2ObjGraph g)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(g.Id));
            o.Set("name", SC2JsonValue.Of(g.Name));
            o.Set("entriesA", WriteIntList(g.EntriesA));
            o.Set("entriesB", WriteIntList(g.EntriesB));
            o.Set("markerIdsA", WriteIntList(g.MarkerIdsA));
            o.Set("markerIdsB", WriteIntList(g.MarkerIdsB));
            o.Set("usesMarkerIds", SC2JsonValue.Of(g.UsesMarkerIds));
            o.Set("graphKind", SC2JsonValue.Of(g.GraphKind));
            return o;
        }

        private static SC2ObjGraph ReadGraph(SC2JsonValue o) => new SC2ObjGraph
        {
            Id = o.Get("id").AsInt(0),
            Name = o.Get("name").AsString(""),
            EntriesA = ReadIntList(o.Get("entriesA")),
            EntriesB = ReadIntList(o.Get("entriesB")),
            MarkerIdsA = ReadIntList(o.Get("markerIdsA")),
            MarkerIdsB = ReadIntList(o.Get("markerIdsB")),
            UsesMarkerIds = o.Get("usesMarkerIds").AsBool(false),
            GraphKind = o.Get("graphKind").AsString("Land"),
        };

        private static SC2JsonValue WriteArmy(SC2ObjArmy a)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(a.Id));
            o.Set("name", SC2JsonValue.Of(a.Name));
            o.Set("personality", SC2JsonValue.Of(a.Personality));
            o.Set("plans", SC2JsonValue.Of(a.Plans));
            o.Set("color", SC2JsonValue.Of(a.Color));
            o.Set("faction", SC2JsonValue.Of(a.Faction));
            o.Set("mass", SC2JsonValue.Of(a.Mass));
            o.Set("energy", SC2JsonValue.Of(a.Energy));
            var alliances = SC2JsonValue.NewArray();
            foreach (var al in a.Alliances)
            {
                var alo = SC2JsonValue.NewObject();
                alo.Set("otherArmy", SC2JsonValue.Of(al.OtherArmy));
                alo.Set("stance", SC2JsonValue.Of(al.Stance));
                alliances.Add(alo);
            }
            o.Set("alliances", alliances);
            return o;
        }

        private static SC2ObjArmy ReadArmy(SC2JsonValue o)
        {
            var a = new SC2ObjArmy
            {
                Id = o.Get("id").AsInt(0),
                Name = o.Get("name").AsString(""),
                Personality = o.Get("personality").AsString(""),
                Plans = o.Get("plans").AsString(""),
                Color = (float)o.Get("color").AsDouble(1),
                Faction = o.Get("faction").AsInt(0),
                Mass = (float)o.Get("mass").AsDouble(0),
                Energy = (float)o.Get("energy").AsDouble(0),
            };
            foreach (var alo in o.Get("alliances").Items())
                a.Alliances.Add(new SC2ObjAlliance
                {
                    OtherArmy = alo.Get("otherArmy").AsString(""),
                    Stance = alo.Get("stance").AsString("ENEMY"),
                });
            return a;
        }

        private static SC2JsonValue WriteUnit(SC2ObjUnitNode u)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(u.Id));
            o.Set("armyName", SC2JsonValue.Of(u.ArmyName));
            o.Set("name", SC2JsonValue.Of(u.Name));
            o.Set("parentIndex", SC2JsonValue.Of(u.ParentIndex));
            o.Set("isGroup", SC2JsonValue.Of(u.IsGroup));
            o.Set("type", SC2JsonValue.Of(u.Type));
            o.Set("position", WriteVector3(u.Position));
            o.Set("orientationEuler", WriteVector3(u.OrientationEuler));
            o.Set("anchorToTerrain", SC2JsonValue.Of(u.AnchorToTerrain));
            o.Set("heightOffset", SC2JsonValue.Of(u.HeightOffset));
            return o;
        }

        private static SC2ObjUnitNode ReadUnit(SC2JsonValue o) => new SC2ObjUnitNode
        {
            Id = o.Get("id").AsInt(0),
            ArmyName = o.Get("armyName").AsString(""),
            Name = o.Get("name").AsString(""),
            ParentIndex = o.Get("parentIndex").AsInt(-1),
            IsGroup = o.Get("isGroup").AsBool(false),
            Type = o.Get("type").AsString(""),
            Position = ReadVector3(o.Get("position")),
            OrientationEuler = ReadVector3(o.Get("orientationEuler")),
            AnchorToTerrain = o.Get("anchorToTerrain").AsBool(true),
            HeightOffset = (float)o.Get("heightOffset").AsDouble(0),
        };

        private static SC2JsonValue WriteReferenceUnit(SC2ObjReferenceUnit r)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(r.Id));
            o.Set("blueprintId", SC2JsonValue.Of(r.BlueprintId));
            o.Set("displayName", SC2JsonValue.Of(r.DisplayName));
            o.Set("position", WriteVector3(r.Position));
            o.Set("orientationEuler", WriteVector3(r.OrientationEuler));
            o.Set("anchorToTerrain", SC2JsonValue.Of(r.AnchorToTerrain));
            o.Set("heightOffset", SC2JsonValue.Of(r.HeightOffset));
            o.Set("showRadar", SC2JsonValue.Of(r.ShowRadar));
            o.Set("showSonar", SC2JsonValue.Of(r.ShowSonar));
            o.Set("showVision", SC2JsonValue.Of(r.ShowVision));
            o.Set("showWeapons", SC2JsonValue.Of(r.ShowWeapons));
            return o;
        }

        private static SC2ObjReferenceUnit ReadReferenceUnit(SC2JsonValue o) => new SC2ObjReferenceUnit
        {
            Id = o.Get("id").AsInt(0),
            BlueprintId = o.Get("blueprintId").AsString(""),
            DisplayName = o.Get("displayName").AsString(""),
            Position = ReadVector3(o.Get("position")),
            OrientationEuler = ReadVector3(o.Get("orientationEuler")),
            AnchorToTerrain = o.Get("anchorToTerrain").AsBool(true),
            HeightOffset = (float)o.Get("heightOffset").AsDouble(0),
            ShowRadar = o.Get("showRadar").AsBool(true),
            ShowSonar = o.Get("showSonar").AsBool(true),
            ShowVision = o.Get("showVision").AsBool(true),
            ShowWeapons = o.Get("showWeapons").AsBool(true),
        };

        private static SC2JsonValue WriteZone(SC2ObjZone z)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(z.Id));
            o.Set("name", SC2JsonValue.Of(z.Name));
            o.Set("x1", SC2JsonValue.Of(z.X1));
            o.Set("z1", SC2JsonValue.Of(z.Z1));
            o.Set("x2", SC2JsonValue.Of(z.X2));
            o.Set("z2", SC2JsonValue.Of(z.Z2));
            o.Set("role", SC2JsonValue.Of(z.Role));
            return o;
        }

        private static SC2ObjZone ReadZone(SC2JsonValue o) => new SC2ObjZone
        {
            Id = o.Get("id").AsInt(0),
            Name = o.Get("name").AsString(""),
            X1 = (float)o.Get("x1").AsDouble(0),
            Z1 = (float)o.Get("z1").AsDouble(0),
            X2 = (float)o.Get("x2").AsDouble(0),
            Z2 = (float)o.Get("z2").AsDouble(0),
            Role = o.Get("role").AsString("Unknown"),
        };

        private static SC2JsonValue WriteChain(SC2ObjChain c)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(c.Id));
            o.Set("name", SC2JsonValue.Of(c.Name));
            o.Set("markerNames", WriteStringList(c.MarkerNames));
            return o;
        }

        private static SC2ObjChain ReadChain(SC2JsonValue o) => new SC2ObjChain
        {
            Id = o.Get("id").AsInt(0),
            Name = o.Get("name").AsString(""),
            MarkerNames = ReadStringList(o.Get("markerNames")),
        };

        private static SC2JsonValue WriteSymmetryGroup(SC2ObjSymmetryGroup g)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(g.Id));
            o.Set("kind", SC2JsonValue.Of(g.Kind));
            var members = SC2JsonValue.NewArray();
            foreach (var m in g.Members)
            {
                var mo = SC2JsonValue.NewObject();
                mo.Set("objectId", SC2JsonValue.Of(m.ObjectId));
                mo.Set("slotIndex", SC2JsonValue.Of(m.SlotIndex));
                members.Add(mo);
            }
            o.Set("members", members);
            return o;
        }

        private static SC2ObjSymmetryGroup ReadSymmetryGroup(SC2JsonValue o)
        {
            var g = new SC2ObjSymmetryGroup
            {
                Id = o.Get("id").AsInt(0),
                Kind = o.Get("kind").AsString("Marker"),
            };
            foreach (var mo in o.Get("members").Items())
                g.Members.Add(new SC2ObjSymmetryMember
                {
                    ObjectId = mo.Get("objectId").AsInt(0),
                    SlotIndex = mo.Get("slotIndex").AsInt(0),
                });
            return g;
        }

        private static SC2JsonValue WriteLuaFields(List<SC2LuaField> fields)
        {
            var arr = SC2JsonValue.NewArray();
            foreach (var f in fields)
            {
                var fo = SC2JsonValue.NewObject();
                fo.Set("key", SC2JsonValue.Of(f.Key));
                fo.Set("rawValue", SC2JsonValue.Of(f.RawValue));
                arr.Add(fo);
            }
            return arr;
        }

        private static List<SC2LuaField> ReadLuaFields(SC2JsonValue v)
        {
            var list = new List<SC2LuaField>();
            foreach (var fo in v.Items())
                list.Add(new SC2LuaField { Key = fo.Get("key").AsString(""), RawValue = fo.Get("rawValue").AsString("") });
            return list;
        }

        // ---- Terrain ----

        private static SC2JsonValue WriteTerrain(SC2TerrainDocument t)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("authoringMode", SC2JsonValue.Of(t.AuthoringMode.ToString()));
            o.Set("grid", WriteTerrainGrid(t.Grid));
            var layers = SC2JsonValue.NewArray();
            foreach (var l in t.Layers) layers.Add(WriteTerrainLayer(l));
            o.Set("layers", layers);
            var materials = SC2JsonValue.NewArray();
            foreach (var m in t.Materials) materials.Add(WriteMaterial(m));
            o.Set("materials", materials);
            var pairs = SC2JsonValue.NewArray();
            foreach (var p in t.MaterialPairs) pairs.Add(WriteMaterialPair(p));
            o.Set("materialPairs", pairs);
            o.Set("pairAssignments", WriteTrianglePairMap(t.PairAssignments));
            o.Set("blend", t.Blend == null ? SC2JsonValue.Null : WriteBlend(t.Blend));
            o.Set("rnm", WriteRnm(t.Rnm));
            var simplification = SC2JsonValue.NewObject();
            simplification.Set("enabled", SC2JsonValue.Of(t.Simplification.Enabled));
            o.Set("simplification", simplification);
            var normals = SC2JsonValue.NewObject();
            normals.Set("recalculateOnEdit", SC2JsonValue.Of(t.Normals.RecalculateOnEdit));
            o.Set("normals", normals);
            var symmetry = SC2JsonValue.NewObject();
            symmetry.Set("mode", SC2JsonValue.Of(t.Symmetry.Mode.ToString()));
            symmetry.Set("center", WriteVector3(t.Symmetry.Center));
            // This block used to never write RadialCount at all, so every document-package
            // save/load round-trip silently reset it to SC2SymmetrySettings'
            // own class default (4) regardless of what was actually saved —
            // ApplyToLegacy correctly reads doc.Symmetry.RadialCount, but the
            // JSON it was reading from never carried the real value in the
            // first place.
            symmetry.Set("radialCount", SC2JsonValue.Of(t.Symmetry.RadialCount));
            o.Set("symmetry", symmetry);
            o.Set("isInitialized", SC2JsonValue.Of(t.IsInitialized));
            o.Set("dataVersion", SC2JsonValue.Of(t.DataVersion));
            o.Set("importOnlyMode", SC2JsonValue.Of(t.ImportOnlyMode));
            o.Set("singleBspNodePerLayer", SC2JsonValue.Of(t.SingleBspNodePerLayer));
            o.Set("vanillaCandidateBspSplit", SC2JsonValue.Of(t.VanillaCandidateBspSplit));
            o.Set("playableAreaName", SC2JsonValue.Of(t.PlayableAreaName));
            o.Set("backgroundTerrainEnabled", SC2JsonValue.Of(t.BackgroundTerrainEnabled));
            o.Set("backgroundTerrainSize", SC2JsonValue.Of(t.BackgroundTerrainSize));
            o.Set("backgroundTerrainUvScale", SC2JsonValue.Of(t.BackgroundTerrainUvScale));
            o.Set("globalUvScaleEnabled", SC2JsonValue.Of(t.GlobalUvScaleEnabled));
            o.Set("globalUvScale", SC2JsonValue.Of(t.GlobalUvScale));
            o.Set("rnmBakeSyncSunDirection", SC2JsonValue.Of(t.RnmBakeSyncSunDirection));
            o.Set("rnmBakeSyncSunColor", SC2JsonValue.Of(t.RnmBakeSyncSunColor));
            o.Set("rnmBakeSyncSunIntensity", SC2JsonValue.Of(t.RnmBakeSyncSunIntensity));
            o.Set("rnmBakeSyncShadowColor", SC2JsonValue.Of(t.RnmBakeSyncShadowColor));
            o.Set("rnmBakeOverrideSunDirection", WriteVector3(t.RnmBakeOverrideSunDirection));
            o.Set("rnmBakeOverrideSunColor", WriteColor(t.RnmBakeOverrideSunColor));
            o.Set("rnmBakeOverrideSunIntensity", SC2JsonValue.Of(t.RnmBakeOverrideSunIntensity));
            o.Set("rnmBakeOverrideShadowColor", WriteColor(t.RnmBakeOverrideShadowColor));
            o.Set("rnmBakeShadowSoftRadius", SC2JsonValue.Of(t.RnmBakeShadowSoftRadius));
            // Recursive — t.Background is itself a full
            // SC2TerrainDocument (see that field's own doc comment) —
            // WriteTerrain naturally handles it with no new logic beyond
            // this one nested call.
            o.Set("background", t.Background != null ? WriteTerrain(t.Background) : SC2JsonValue.Null);
            var importedEntries = SC2JsonValue.NewArray();
            foreach (var e in t.ImportedEntries) importedEntries.Add(WriteImportedTerrainEntry(e));
            o.Set("importedEntries", importedEntries);
            return o;
        }

        private static SC2TerrainDocument ReadTerrain(SC2JsonValue o)
        {
            var t = new SC2TerrainDocument
            {
                AuthoringMode = ParseEnum(o.Get("authoringMode"), SC2TerrainAuthoringMode.ProceduralHeightmap),
                Grid = ReadTerrainGrid(o.Get("grid")),
                PairAssignments = ReadTrianglePairMap(o.Get("pairAssignments")),
                Rnm = ReadRnm(o.Get("rnm")),
                IsInitialized = o.Get("isInitialized").AsBool(false),
                DataVersion = o.Get("dataVersion").AsInt(0),
                ImportOnlyMode = o.Get("importOnlyMode").AsBool(false),
                SingleBspNodePerLayer = o.Get("singleBspNodePerLayer").AsBool(false),
                VanillaCandidateBspSplit = o.Get("vanillaCandidateBspSplit").AsBool(true),
                PlayableAreaName = o.Get("playableAreaName").AsString(""),
                BackgroundTerrainEnabled = o.Get("backgroundTerrainEnabled").AsBool(false),
                BackgroundTerrainSize = o.Get("backgroundTerrainSize").AsInt(4096),
                BackgroundTerrainUvScale = (float)o.Get("backgroundTerrainUvScale").AsDouble(1024.0),
                GlobalUvScaleEnabled = o.Get("globalUvScaleEnabled").AsBool(false),
                GlobalUvScale = (float)o.Get("globalUvScale").AsDouble(4.0),
                RnmBakeSyncSunDirection = o.Get("rnmBakeSyncSunDirection").AsBool(true),
                RnmBakeSyncSunColor = o.Get("rnmBakeSyncSunColor").AsBool(true),
                RnmBakeSyncSunIntensity = o.Get("rnmBakeSyncSunIntensity").AsBool(true),
                RnmBakeSyncShadowColor = o.Get("rnmBakeSyncShadowColor").AsBool(true),
                RnmBakeOverrideSunDirection = o.Get("rnmBakeOverrideSunDirection").Kind == SC2JsonKind.Object
                    ? ReadVector3(o.Get("rnmBakeOverrideSunDirection")) : new Vector3(0.3f, 0.8f, 0.3f),
                RnmBakeOverrideSunColor = ReadColor(o.Get("rnmBakeOverrideSunColor"), Color.white),
                RnmBakeOverrideSunIntensity = (float)o.Get("rnmBakeOverrideSunIntensity").AsDouble(1.0),
                RnmBakeOverrideShadowColor = ReadColor(o.Get("rnmBakeOverrideShadowColor"), new Color(0.64f, 0.77f, 0.92f, 1f)),
                RnmBakeShadowSoftRadius = o.Get("rnmBakeShadowSoftRadius").AsInt(6),
            };
            foreach (var v in o.Get("layers").Items()) t.Layers.Add(ReadTerrainLayer(v));
            foreach (var v in o.Get("materials").Items()) t.Materials.Add(ReadMaterial(v));
            foreach (var v in o.Get("materialPairs").Items()) t.MaterialPairs.Add(ReadMaterialPair(v));
            if (o.Get("blend").Kind == SC2JsonKind.Object) t.Blend = ReadBlend(o.Get("blend"));
            var simplification = o.Get("simplification");
            t.Simplification = new SC2TerrainSimplificationSettings { Enabled = simplification.Get("enabled").AsBool(false) };
            var normals = o.Get("normals");
            t.Normals = new SC2TerrainNormalsSettings { RecalculateOnEdit = normals.Get("recalculateOnEdit").AsBool(true) };
            var symmetry = o.Get("symmetry");
            t.Symmetry = new SC2SymmetrySettings
            {
                Mode = ParseEnum(symmetry.Get("mode"), SymmetryMode.None),
                Center = ReadVector3(symmetry.Get("center")),
                RadialCount = symmetry.Get("radialCount").AsInt(4),
            };
            foreach (var v in o.Get("importedEntries").Items()) t.ImportedEntries.Add(ReadImportedTerrainEntry(v));
            var background = o.Get("background");
            if (background.Kind == SC2JsonKind.Object) t.Background = ReadTerrain(background);
            return t;
        }

        private static SC2JsonValue WriteImportedTerrainEntry(SC2ImportedTerrainEntryDocument e)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(e.Id)); o.Set("displayName", SC2JsonValue.Of(e.DisplayName));
            o.Set("originalBdf", WriteBlobRef(e.OriginalBdf));
            o.Set("position", WriteFloatArray(e.Position)); o.Set("eulerAngles", WriteFloatArray(e.EulerAngles));
            o.Set("scale", WriteFloatArray(e.Scale)); o.Set("recomputeNormals", SC2JsonValue.Of(e.RecomputeNormals));
            var overrides = SC2JsonValue.NewArray();
            foreach (var m in e.MaterialOverrides) overrides.Add(WriteImportedTerrainMaterialOverride(m));
            o.Set("materialOverrides", overrides);
            return o;
        }

        private static SC2ImportedTerrainEntryDocument ReadImportedTerrainEntry(SC2JsonValue o)
        {
            var e = new SC2ImportedTerrainEntryDocument
            {
                Id = o.Get("id").AsString(Guid.NewGuid().ToString("N")), DisplayName = o.Get("displayName").AsString("ImportedTerrain"),
                OriginalBdf = ReadBlobRef(o.Get("originalBdf")),
                Position = ReadFloatArray(o.Get("position"), 3, new[] { 0f, 0f, 0f }),
                EulerAngles = ReadFloatArray(o.Get("eulerAngles"), 3, new[] { 0f, 0f, 0f }),
                Scale = ReadFloatArray(o.Get("scale"), 3, new[] { 1f, 1f, 1f }),
                RecomputeNormals = o.Get("recomputeNormals").AsBool(true),
            };
            foreach (var v in o.Get("materialOverrides").Items()) e.MaterialOverrides.Add(ReadImportedTerrainMaterialOverride(v));
            return e;
        }

        private static SC2JsonValue WriteImportedTerrainMaterialOverride(SC2ImportedTerrainMaterialOverrideDocument m)
        {
            var o = SC2JsonValue.NewObject();
            var textures = SC2JsonValue.NewObject();
            foreach (var kv in m.TextureResourcePaths) textures.Set(kv.Key, SC2JsonValue.Of(kv.Value));
            o.Set("textures", textures);
            var flags = SC2JsonValue.NewObject();
            foreach (var kv in m.Flags) flags.Set(kv.Key, SC2JsonValue.Of(kv.Value));
            o.Set("flags", flags);
            return o;
        }

        private static SC2ImportedTerrainMaterialOverrideDocument ReadImportedTerrainMaterialOverride(SC2JsonValue o)
        {
            var m = new SC2ImportedTerrainMaterialOverrideDocument();
            foreach (var kv in o.Get("textures").Members()) m.TextureResourcePaths[kv.Key] = kv.Value.AsString("");
            foreach (var kv in o.Get("flags").Members()) m.Flags[kv.Key] = (float)kv.Value.AsDouble(0);
            return m;
        }

        private static SC2JsonValue WriteTerrainGrid(SC2TerrainGrid g)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("width", SC2JsonValue.Of(g.Width));
            o.Set("height", SC2JsonValue.Of(g.Height));
            o.Set("cellSize", SC2JsonValue.Of(g.CellSize));
            o.Set("heightsFloat32", WriteBlobRef(g.HeightsFloat32));
            o.Set("minimumHeight", SC2JsonValue.Of(g.MinimumHeight));
            o.Set("maximumHeight", SC2JsonValue.Of(g.MaximumHeight));
            return o;
        }

        private static SC2TerrainGrid ReadTerrainGrid(SC2JsonValue o) => new SC2TerrainGrid
        {
            Width = o.Get("width").AsInt(0),
            Height = o.Get("height").AsInt(0),
            CellSize = (float)o.Get("cellSize").AsDouble(0),
            HeightsFloat32 = ReadBlobRef(o.Get("heightsFloat32")),
            MinimumHeight = (float)o.Get("minimumHeight").AsDouble(0),
            MaximumHeight = (float)o.Get("maximumHeight").AsDouble(0),
        };

        private static SC2JsonValue WriteTerrainLayer(SC2TerrainLayerDocument l)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("layerId", SC2JsonValue.Of(l.LayerId));
            o.Set("name", SC2JsonValue.Of(l.Name));
            o.Set("role", SC2JsonValue.Of(l.Role.ToString()));
            o.Set("includeInVisualTerrain", SC2JsonValue.Of(l.IncludeInVisualTerrain));
            o.Set("includeInHfield", SC2JsonValue.Of(l.IncludeInHfield));
            o.Set("includeInCollisionSource", SC2JsonValue.Of(l.IncludeInCollisionSource));
            o.Set("materialPairIds", WriteStringList(l.MaterialPairIds));
            return o;
        }

        private static SC2TerrainLayerDocument ReadTerrainLayer(SC2JsonValue o) => new SC2TerrainLayerDocument
        {
            LayerId = o.Get("layerId").AsString(""),
            Name = o.Get("name").AsString(""),
            Role = ParseEnum(o.Get("role"), SC2TerrainLayerRole.GameplaySurface),
            IncludeInVisualTerrain = o.Get("includeInVisualTerrain").AsBool(true),
            IncludeInHfield = o.Get("includeInHfield").AsBool(true),
            IncludeInCollisionSource = o.Get("includeInCollisionSource").AsBool(true),
            MaterialPairIds = ReadStringList(o.Get("materialPairIds")),
        };

        private static SC2JsonValue WriteMaterial(SC2MaterialDocument m)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(m.Id));
            o.Set("name", SC2JsonValue.Of(m.Name));
            o.Set("diffuse", WriteTextureReference(m.Diffuse));
            o.Set("normal", WriteTextureReference(m.Normal));
            o.Set("mask", WriteTextureReference(m.Mask));
            o.Set("debugColor", WriteColor(m.DebugColor));
            return o;
        }

        private static SC2MaterialDocument ReadMaterial(SC2JsonValue o) => new SC2MaterialDocument
        {
            Id = o.Get("id").AsString(""),
            Name = o.Get("name").AsString(""),
            Diffuse = ReadTextureReference(o.Get("diffuse")),
            Normal = ReadTextureReference(o.Get("normal")),
            Mask = ReadTextureReference(o.Get("mask")),
            DebugColor = ReadColor(o.Get("debugColor"), Color.white),
        };

        private static SC2JsonValue WriteTextureReference(SC2TextureReference r)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(r.Id));
            o.Set("name", SC2JsonValue.Of(r.Name));
            o.Set("resourcePath", SC2JsonValue.Of(r.ResourcePath));
            o.Set("gamePath", SC2JsonValue.Of(r.GamePath));
            o.Set("source", SC2JsonValue.Of(r.Source));
            o.Set("contentHash", SC2JsonValue.Of(r.ContentHash));
            return o;
        }

        private static SC2TextureReference ReadTextureReference(SC2JsonValue o) => new SC2TextureReference
        {
            Id = o.Get("id").AsString("") ?? "",
            Name = o.Get("name").AsString("") ?? "",
            ResourcePath = o.Get("resourcePath").AsString("") ?? "",
            GamePath = o.Get("gamePath").AsString("") ?? "",
            Source = o.Get("source").AsString("") ?? "",
            ContentHash = o.Get("contentHash").AsString("") ?? "",
        };

        private static SC2JsonValue WriteMaterialPair(SC2MaterialPairDocument p)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(p.Id));
            o.Set("name", SC2JsonValue.Of(p.Name));
            o.Set("material1Id", SC2JsonValue.Of(p.Material1Id));
            o.Set("material2Id", SC2JsonValue.Of(p.Material2Id));
            o.Set("debugColor", WriteColor(p.DebugColor));
            o.Set("tilingScale", SC2JsonValue.Of(p.TilingScale));
            o.Set("simplifyRatio", SC2JsonValue.Of(p.SimplifyRatio));
            var flags = SC2JsonValue.NewArray();
            foreach (var f in p.ShaderFlags)
            {
                var fo = SC2JsonValue.NewObject();
                fo.Set("key", SC2JsonValue.Of(f.Key));
                fo.Set("value", SC2JsonValue.Of(f.Value));
                flags.Add(fo);
            }
            o.Set("shaderFlags", flags);
            o.Set("hfieldContribution", SC2JsonValue.Of(p.HfieldContribution.ToString()));
            o.Set("rnmSetId", SC2JsonValue.Of(p.RnmSetId));
            return o;
        }

        private static SC2MaterialPairDocument ReadMaterialPair(SC2JsonValue o)
        {
            var p = new SC2MaterialPairDocument
            {
                Id = o.Get("id").AsString(""),
                Name = o.Get("name").AsString(""),
                Material1Id = o.Get("material1Id").AsString(""),
                Material2Id = o.Get("material2Id").AsString(""),
                DebugColor = ReadColor(o.Get("debugColor"), Color.white),
                TilingScale = (float)o.Get("tilingScale").AsDouble(4),
                SimplifyRatio = (float)o.Get("simplifyRatio").AsDouble(0),
                HfieldContribution = ParseEnum(o.Get("hfieldContribution"), SC2HfieldContribution.InheritLayer),
                RnmSetId = o.Get("rnmSetId").AsString(""),
            };
            foreach (var fo in o.Get("shaderFlags").Items())
                p.ShaderFlags.Add(new SC2ShaderFlag { Key = fo.Get("key").AsString(""), Value = fo.Get("value").AsBool(true) });
            return p;
        }

        private static SC2JsonValue WriteTrianglePairMap(SC2TrianglePairMap m)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("slotIndexPerTriangleInt16", WriteBlobRef(m.SlotIndexPerTriangleInt16));
            o.Set("slotIds", WriteStringList(m.SlotIds));
            return o;
        }

        private static SC2TrianglePairMap ReadTrianglePairMap(SC2JsonValue o) => new SC2TrianglePairMap
        {
            SlotIndexPerTriangleInt16 = ReadBlobRef(o.Get("slotIndexPerTriangleInt16")),
            SlotIds = ReadStringList(o.Get("slotIds")),
        };

        private static SC2JsonValue WriteBlend(SC2BlendDocument b)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("width", SC2JsonValue.Of(b.Width));
            o.Set("height", SC2JsonValue.Of(b.Height));
            o.Set("blendTextureRgba32", WriteBlobRef(b.BlendTextureRgba32));
            return o;
        }

        private static SC2BlendDocument ReadBlend(SC2JsonValue o) => new SC2BlendDocument
        {
            Width = o.Get("width").AsInt(0),
            Height = o.Get("height").AsInt(0),
            BlendTextureRgba32 = ReadBlobRef(o.Get("blendTextureRgba32")),
        };

        private static SC2JsonValue WriteRnm(SC2RnmDocument r)
        {
            var o = SC2JsonValue.NewObject();
            var sets = SC2JsonValue.NewArray();
            foreach (var s in r.MaskSets)
            {
                var so = SC2JsonValue.NewObject();
                so.Set("id", SC2JsonValue.Of(s.Id));
                so.Set("name", SC2JsonValue.Of(s.Name));
                so.Set("width", SC2JsonValue.Of(s.Width));
                so.Set("height", SC2JsonValue.Of(s.Height));
                so.Set("maskBytes", WriteBlobRef(s.MaskBytes));
                // Baked RNM lighting. Written only when present so a map that
                // was never baked keeps the same manifest shape it had before
                // these fields existed.
                if (!s.BakedRgb0.IsEmpty) so.Set("bakedRgb0", WriteBlobRef(s.BakedRgb0));
                if (!s.BakedRgb1.IsEmpty) so.Set("bakedRgb1", WriteBlobRef(s.BakedRgb1));
                if (!s.BakedRgb2.IsEmpty) so.Set("bakedRgb2", WriteBlobRef(s.BakedRgb2));
                if (!s.BakedShadowAlpha.IsEmpty) so.Set("bakedShadowAlpha", WriteBlobRef(s.BakedShadowAlpha));
                sets.Add(so);
            }
            o.Set("maskSets", sets);
            return o;
        }

        private static SC2RnmDocument ReadRnm(SC2JsonValue o)
        {
            var r = new SC2RnmDocument();
            foreach (var so in o.Get("maskSets").Items())
                r.MaskSets.Add(new SC2RnmMaskSetDocument
                {
                    Id = so.Get("id").AsString(""),
                    Name = so.Get("name").AsString(""),
                    Width = so.Get("width").AsInt(0),
                    Height = so.Get("height").AsInt(0),
                    MaskBytes = ReadBlobRef(so.Get("maskBytes")),
                    BakedRgb0 = ReadBlobRef(so.Get("bakedRgb0")),
                    BakedRgb1 = ReadBlobRef(so.Get("bakedRgb1")),
                    BakedRgb2 = ReadBlobRef(so.Get("bakedRgb2")),
                    BakedShadowAlpha = ReadBlobRef(so.Get("bakedShadowAlpha")),
                });
            return r;
        }

        // ---- Scenario ----

        private static SC2JsonValue WriteScenario(SC2ScenarioDocument s)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("originalScenarioLua", WriteBlobRef(s.OriginalScenarioLua));
            o.Set("originalSaveLua", WriteBlobRef(s.OriginalSaveLua));
            o.Set("originalScriptLua", WriteBlobRef(s.OriginalScriptLua));
            o.Set("knownFields", WriteScenarioKnownFields(s.KnownFields));
            o.Set("scenarioMode", SC2JsonValue.Of(s.ScenarioMode.ToString()));
            o.Set("saveMode", SC2JsonValue.Of(s.SaveMode.ToString()));
            o.Set("scriptMode", SC2JsonValue.Of(s.ScriptMode.ToString()));
            o.Set("state", SC2JsonValue.Of(s.State.ToString()));
            return o;
        }

        private static SC2ScenarioDocument ReadScenario(SC2JsonValue o) => new SC2ScenarioDocument
        {
            OriginalScenarioLua = ReadBlobRef(o.Get("originalScenarioLua")),
            OriginalSaveLua = ReadBlobRef(o.Get("originalSaveLua")),
            OriginalScriptLua = ReadBlobRef(o.Get("originalScriptLua")),
            KnownFields = ReadScenarioKnownFields(o.Get("knownFields")),
            ScenarioMode = ParseEnum(o.Get("scenarioMode"), SC2LuaPatchMode.OriginalPassthrough),
            SaveMode = ParseEnum(o.Get("saveMode"), SC2LuaPatchMode.OriginalPassthrough),
            ScriptMode = ParseEnum(o.Get("scriptMode"), SC2LuaPatchMode.OriginalPassthrough),
            State = ParseEnum(o.Get("state"), SC2DerivedState.Original),
        };

        private static SC2JsonValue WriteScenarioKnownFields(SC2ScenarioKnownFields k)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("name", SC2JsonValue.Of(k.Name));
            o.Set("description", SC2JsonValue.Of(k.Description));
            o.Set("type", SC2JsonValue.Of(k.Type));
            o.Set("campaignId", SC2JsonValue.Of(k.CampaignId));
            o.Set("starts", SC2JsonValue.Of(k.Starts));
            o.Set("preview", SC2JsonValue.Of(k.Preview));
            o.Set("reverbPreset", SC2JsonValue.Of(k.ReverbPreset));
            o.Set("sizeX", SC2JsonValue.Of(k.SizeX));
            o.Set("sizeY", SC2JsonValue.Of(k.SizeY));
            o.Set("mapPath", SC2JsonValue.Of(k.MapPath));
            o.Set("savePath", SC2JsonValue.Of(k.SavePath));
            o.Set("scriptPath", SC2JsonValue.Of(k.ScriptPath));
            o.Set("uiDeactivate", SC2JsonValue.Of(k.UiDeactivate));
            o.Set("noRushRadius", SC2JsonValue.Of(k.NoRushRadius));
            o.Set("extraFields", WriteLuaFields(k.ExtraFields));
            return o;
        }

        private static SC2ScenarioKnownFields ReadScenarioKnownFields(SC2JsonValue o) => new SC2ScenarioKnownFields
        {
            Name = o.Get("name").AsString(""),
            Description = o.Get("description").AsString(""),
            Type = o.Get("type").AsString(""),
            CampaignId = o.Get("campaignId").AsString(""),
            Starts = o.Get("starts").AsBool(false),
            Preview = o.Get("preview").AsString(""),
            ReverbPreset = o.Get("reverbPreset").AsString(""),
            SizeX = o.Get("sizeX").AsInt(0),
            SizeY = o.Get("sizeY").AsInt(0),
            MapPath = o.Get("mapPath").AsString(""),
            SavePath = o.Get("savePath").AsString(""),
            ScriptPath = o.Get("scriptPath").AsString(""),
            UiDeactivate = o.Get("uiDeactivate").AsBool(false),
            NoRushRadius = (float)o.Get("noRushRadius").AsDouble(0),
            ExtraFields = ReadLuaFields(o.Get("extraFields")),
        };

        // ---- Decals ----

        private static SC2JsonValue WriteDecals(SC2DecalsDocument d)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("originalBdf", WriteBlobRef(d.OriginalBdf));
            o.Set("isEditable", SC2JsonValue.Of(d.IsEditable));
            o.Set("hasDecals", SC2JsonValue.Of(d.HasDecals));
            o.Set("vertexCount", SC2JsonValue.Of(d.VertexCount));
            o.Set("indexCount", SC2JsonValue.Of(d.IndexCount));
            o.Set("diffuseAtlasPath", SC2JsonValue.Of(d.DiffuseAtlasPath));
            o.Set("normalAtlasPath", SC2JsonValue.Of(d.NormalAtlasPath));
            o.Set("diffuseAtlasContent", WriteBlobRef(d.DiffuseAtlasContent));
            o.Set("normalAtlasContent", WriteBlobRef(d.NormalAtlasContent));
            o.Set("atlasWidth", SC2JsonValue.Of(d.AtlasWidth));
            o.Set("atlasHeight", SC2JsonValue.Of(d.AtlasHeight));
            o.Set("atlasSource", SC2JsonValue.Of(d.AtlasSource.ToString()));
            o.Set("keepOriginalBakedLayer", SC2JsonValue.Of(d.KeepOriginalBakedLayer));
            o.Set("spatialState", SC2JsonValue.Of(d.SpatialState.ToString()));
            var entries = SC2JsonValue.NewArray();
            foreach (var entry in d.Entries) entries.Add(WriteDecalEntry(entry));
            o.Set("entries", entries);
            var placements = SC2JsonValue.NewArray();
            foreach (var placement in d.Placements) placements.Add(WriteDecalPlacement(placement));
            o.Set("placements", placements);
            o.Set("state", SC2JsonValue.Of(d.State.ToString()));
            return o;
        }

        private static SC2DecalsDocument ReadDecals(SC2JsonValue o)
        {
            var d = new SC2DecalsDocument
            {
                OriginalBdf = ReadBlobRef(o.Get("originalBdf")), IsEditable = o.Get("isEditable").AsBool(false),
                HasDecals = o.Get("hasDecals").AsBool(false), VertexCount = o.Get("vertexCount").AsInt(0),
                IndexCount = o.Get("indexCount").AsInt(0), DiffuseAtlasPath = o.Get("diffuseAtlasPath").AsString(""),
                NormalAtlasPath = o.Get("normalAtlasPath").AsString(""), AtlasWidth = o.Get("atlasWidth").AsInt(0),
                DiffuseAtlasContent = ReadBlobRef(o.Get("diffuseAtlasContent")),
                NormalAtlasContent = ReadBlobRef(o.Get("normalAtlasContent")),
                AtlasHeight = o.Get("atlasHeight").AsInt(0), AtlasSource = ParseEnum(o.Get("atlasSource"), SC2DecalAtlasSource.None),
                KeepOriginalBakedLayer = o.Get("keepOriginalBakedLayer").AsBool(true),
                SpatialState = ParseEnum(o.Get("spatialState"), SC2DecalsSpatialState.Missing),
                State = ParseEnum(o.Get("state"), SC2DerivedState.Original),
            };
            foreach (var v in o.Get("entries").Items()) d.Entries.Add(ReadDecalEntry(v));
            foreach (var v in o.Get("placements").Items()) d.Placements.Add(ReadDecalPlacement(v));
            return d;
        }

        private static SC2JsonValue WriteDecalEntry(SC2DecalAtlasEntry e)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(e.Id)); o.Set("name", SC2JsonValue.Of(e.Name));
            o.Set("x", SC2JsonValue.Of(e.X)); o.Set("y", SC2JsonValue.Of(e.Y));
            o.Set("width", SC2JsonValue.Of(e.Width)); o.Set("height", SC2JsonValue.Of(e.Height));
            o.Set("worldWidth", SC2JsonValue.Of(e.DefaultWorldWidth)); o.Set("worldLength", SC2JsonValue.Of(e.DefaultWorldLength));
            o.Set("projectionDepth", SC2JsonValue.Of(e.DefaultProjectionDepth)); o.Set("source", SC2JsonValue.Of(e.Source.ToString()));
            var tags = SC2JsonValue.NewArray(); foreach (var tag in e.Tags) tags.Add(SC2JsonValue.Of(tag)); o.Set("tags", tags);
            return o;
        }

        private static SC2DecalAtlasEntry ReadDecalEntry(SC2JsonValue o)
        {
            var e = new SC2DecalAtlasEntry
            {
                Id = o.Get("id").AsString(Guid.NewGuid().ToString("N")), Name = o.Get("name").AsString("Decal"),
                X = o.Get("x").AsInt(0), Y = o.Get("y").AsInt(0), Width = o.Get("width").AsInt(64), Height = o.Get("height").AsInt(64),
                DefaultWorldWidth = (float)o.Get("worldWidth").AsDouble(8), DefaultWorldLength = (float)o.Get("worldLength").AsDouble(8),
                DefaultProjectionDepth = (float)o.Get("projectionDepth").AsDouble(32), Source = ParseEnum(o.Get("source"), SC2DecalEntrySource.Manual)
            };
            foreach (var tag in o.Get("tags").Items()) e.Tags.Add(tag.AsString(""));
            return e;
        }

        private static SC2JsonValue WriteDecalPlacement(SC2DecalPlacement p)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("id", SC2JsonValue.Of(p.Id)); o.Set("name", SC2JsonValue.Of(p.Name)); o.Set("entryId", SC2JsonValue.Of(p.AtlasEntryId));
            o.Set("position", WriteFloatArray(p.Position)); o.Set("normal", WriteFloatArray(p.SurfaceNormal));
            o.Set("rotation", SC2JsonValue.Of(p.Rotation)); o.Set("width", SC2JsonValue.Of(p.Width)); o.Set("length", SC2JsonValue.Of(p.Length));
            o.Set("depth", SC2JsonValue.Of(p.ProjectionDepth)); o.Set("heightOffset", SC2JsonValue.Of(p.HeightOffset));
            o.Set("alignToSurface", SC2JsonValue.Of(p.AlignToSurface)); o.Set("enabled", SC2JsonValue.Of(p.Enabled));
            o.Set("symmetryGroupId", SC2JsonValue.Of(p.SymmetryGroupId)); o.Set("symmetrySlotIndex", SC2JsonValue.Of(p.SymmetrySlotIndex));
            return o;
        }

        private static SC2DecalPlacement ReadDecalPlacement(SC2JsonValue o) => new()
        {
            Id = o.Get("id").AsString(Guid.NewGuid().ToString("N")), Name = o.Get("name").AsString("Decal"),
            AtlasEntryId = o.Get("entryId").AsString(""), Position = ReadFloatArray(o.Get("position"), 3, new[] { 0f, 0f, 0f }),
            SurfaceNormal = ReadFloatArray(o.Get("normal"), 3, new[] { 0f, 1f, 0f }), Rotation = (float)o.Get("rotation").AsDouble(0),
            Width = (float)o.Get("width").AsDouble(8), Length = (float)o.Get("length").AsDouble(8),
            ProjectionDepth = (float)o.Get("depth").AsDouble(32), HeightOffset = (float)o.Get("heightOffset").AsDouble(0.03),
            AlignToSurface = o.Get("alignToSurface").AsBool(true), Enabled = o.Get("enabled").AsBool(true),
            SymmetryGroupId = o.Get("symmetryGroupId").AsString(""), SymmetrySlotIndex = o.Get("symmetrySlotIndex").AsInt(0)
        };

        private static SC2JsonValue WriteFloatArray(float[] values)
        {
            var a = SC2JsonValue.NewArray(); if (values != null) foreach (var v in values) a.Add(SC2JsonValue.Of(v)); return a;
        }

        private static float[] ReadFloatArray(SC2JsonValue value, int length, float[] fallback)
        {
            var result = new List<float>(); foreach (var v in value.Items()) result.Add((float)v.AsDouble());
            return result.Count == length ? result.ToArray() : fallback;
        }

        // ---- shared value-type helpers ----

        private static SC2JsonValue WriteBlobRef(SC2BlobReference r)
        {
            if (r.IsEmpty) return SC2JsonValue.Null;
            var o = SC2JsonValue.NewObject();
            o.Set("contentHash", SC2JsonValue.Of(r.ContentHash));
            o.Set("length", SC2JsonValue.Of(r.Length));
            return o;
        }

        private static SC2BlobReference ReadBlobRef(SC2JsonValue o)
        {
            if (o.Kind != SC2JsonKind.Object) return SC2BlobReference.Empty;
            return new SC2BlobReference(o.Get("contentHash").AsString(null), o.Get("length").AsLong(0));
        }

        private static SC2JsonValue WriteMeshBlobRef(SC2MeshBlobReference m)
        {
            if (m == null) return SC2JsonValue.Null;
            var o = SC2JsonValue.NewObject();
            o.Set("verticesFloat32", WriteBlobRef(m.VerticesFloat32));
            o.Set("trianglesUInt32", WriteBlobRef(m.TrianglesUInt32));
            o.Set("vertexCount", SC2JsonValue.Of(m.VertexCount));
            o.Set("triangleCount", SC2JsonValue.Of(m.TriangleCount));
            return o;
        }

        private static SC2MeshBlobReference ReadMeshBlobRef(SC2JsonValue o)
        {
            if (o.Kind != SC2JsonKind.Object) return null;
            return new SC2MeshBlobReference
            {
                VerticesFloat32 = ReadBlobRef(o.Get("verticesFloat32")),
                TrianglesUInt32 = ReadBlobRef(o.Get("trianglesUInt32")),
                VertexCount = o.Get("vertexCount").AsInt(0),
                TriangleCount = o.Get("triangleCount").AsInt(0),
            };
        }

        private static byte[] ReadBase64Bytes(SC2JsonValue v, int fixedSize)
        {
            var s = v.AsString(null);
            if (string.IsNullOrEmpty(s)) return new byte[fixedSize];
            try { return Convert.FromBase64String(s); }
            catch (FormatException) { return new byte[fixedSize]; }
        }

        private static SC2JsonValue WriteStringList(List<string> items)
        {
            var arr = SC2JsonValue.NewArray();
            foreach (var s in items) arr.Add(SC2JsonValue.Of(s));
            return arr;
        }

        private static List<string> ReadStringList(SC2JsonValue v)
        {
            var list = new List<string>();
            foreach (var item in v.Items()) list.Add(item.AsString(""));
            return list;
        }

        private static SC2JsonValue WriteIntList(List<int> items)
        {
            var arr = SC2JsonValue.NewArray();
            foreach (var i in items) arr.Add(SC2JsonValue.Of(i));
            return arr;
        }

        private static List<int> ReadIntList(SC2JsonValue v)
        {
            var list = new List<int>();
            foreach (var item in v.Items()) list.Add(item.AsInt(0));
            return list;
        }

        private static SC2JsonValue WriteVector3(Vector3 v)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("x", SC2JsonValue.Of(v.x));
            o.Set("y", SC2JsonValue.Of(v.y));
            o.Set("z", SC2JsonValue.Of(v.z));
            return o;
        }

        private static Vector3 ReadVector3(SC2JsonValue o) => new Vector3(
            (float)o.Get("x").AsDouble(0),
            (float)o.Get("y").AsDouble(0),
            (float)o.Get("z").AsDouble(0));

        private static SC2JsonValue WriteQuaternion(Quaternion q)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("x", SC2JsonValue.Of(q.x));
            o.Set("y", SC2JsonValue.Of(q.y));
            o.Set("z", SC2JsonValue.Of(q.z));
            o.Set("w", SC2JsonValue.Of(q.w));
            return o;
        }

        private static Quaternion ReadQuaternion(SC2JsonValue o)
        {
            if (o.Kind != SC2JsonKind.Object) return Quaternion.identity;
            return new Quaternion(
                (float)o.Get("x").AsDouble(0),
                (float)o.Get("y").AsDouble(0),
                (float)o.Get("z").AsDouble(0),
                (float)o.Get("w").AsDouble(1));
        }

        private static SC2JsonValue WriteColor(Color c)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("r", SC2JsonValue.Of(c.r));
            o.Set("g", SC2JsonValue.Of(c.g));
            o.Set("b", SC2JsonValue.Of(c.b));
            o.Set("a", SC2JsonValue.Of(c.a));
            return o;
        }

        private static Color ReadColor(SC2JsonValue o, Color fallback)
        {
            if (o.Kind != SC2JsonKind.Object) return fallback;
            return new Color(
                (float)o.Get("r").AsDouble(fallback.r),
                (float)o.Get("g").AsDouble(fallback.g),
                (float)o.Get("b").AsDouble(fallback.b),
                (float)o.Get("a").AsDouble(fallback.a));
        }

        private static SC2JsonValue WriteRect(Rect r)
        {
            var o = SC2JsonValue.NewObject();
            o.Set("x", SC2JsonValue.Of(r.x));
            o.Set("y", SC2JsonValue.Of(r.y));
            o.Set("width", SC2JsonValue.Of(r.width));
            o.Set("height", SC2JsonValue.Of(r.height));
            return o;
        }

        private static Rect ReadRect(SC2JsonValue o) => new Rect(
            (float)o.Get("x").AsDouble(0),
            (float)o.Get("y").AsDouble(0),
            (float)o.Get("width").AsDouble(0),
            (float)o.Get("height").AsDouble(0));

        // ---- helpers ----

        private static DateTime ParseUtc(SC2JsonValue v, DateTime fallback)
        {
            var s = v.AsString(null);
            if (s == null) return fallback;
            return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        private static TEnum ParseEnum<TEnum>(SC2JsonValue v, TEnum fallback) where TEnum : struct
        {
            var s = v.AsString(null);
            if (s == null) return fallback;
            return Enum.TryParse<TEnum>(s, out var parsed) ? parsed : fallback;
        }
    }
}
