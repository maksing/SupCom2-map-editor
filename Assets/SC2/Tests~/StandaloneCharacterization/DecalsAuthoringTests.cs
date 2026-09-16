using System.Linq;
using BdfIO.Core;
using BdfIO.Formats.Decals;
using SC2.MapEditor.Document;

namespace SC2CharacterizationTests;

public class DecalsAuthoringTests
{
    [Fact]
    public void ReplaceStateCommand_UndoRedoPreservesAuthoredState()
    {
        var document = new SC2MapDocument();
        var history = new SC2CommandHistory();
        var next = document.Decals.Clone();
        next.AtlasWidth = 512;
        next.AtlasHeight = 512;
        next.Entries.Add(new SC2DecalAtlasEntry { Id = "entry", Name = "Road" });
        next.Placements.Add(new SC2DecalPlacement { AtlasEntryId = "entry", Name = "Road 1" });

        history.Execute(document, new SC2ReplaceDecalsStateCommand("Add Road Decal", next));
        Assert.Single(document.Decals.Placements);
        Assert.Equal(SC2DecalsSpatialState.Stale, document.Decals.SpatialState);

        history.Undo(document);
        Assert.Empty(document.Decals.Entries);
        Assert.Empty(document.Decals.Placements);

        history.Redo(document);
        Assert.Equal("Road", document.Decals.Entries.Single().Name);
        Assert.Equal(SC2DecalsSpatialState.Stale, document.Decals.SpatialState);
    }

    [Fact]
    public void GeometryBuilder_ClipsPlaneAndWritesAtlasRectAtWriterBoundary()
    {
        var document = new SC2DecalsDocument
        {
            AtlasWidth = 256,
            AtlasHeight = 256,
            DiffuseAtlasPath = "/maps/test/decals/diffuse.dds"
        };
        document.Entries.Add(new SC2DecalAtlasEntry
        {
            Id = "entry", X = 64, Y = 32, Width = 128, Height = 64
        });
        document.Placements.Add(new SC2DecalPlacement
        {
            AtlasEntryId = "entry", Position = new[] { 0f, 0f, 0f },
            SurfaceNormal = new[] { 0f, 1f, 0f }, Width = 2f, Length = 2f,
            ProjectionDepth = 2f, HeightOffset = 0f
        });
        // Upward-facing winding (Cross(B-A, C-A) already points toward the
        // decal) — matches real mesh.triangles data; see the angle-rejection
        // fix's own comment for why this now matters.
        var source = new[]
        {
            new SC2DecalSurfaceTriangle
            {
                AX = -2f, AY = 0f, AZ = -2f, BX = 2f, BY = 0f, BZ = 2f, CX = 2f, CY = 0f, CZ = -2f
            },
            new SC2DecalSurfaceTriangle
            {
                AX = -2f, AY = 0f, AZ = -2f, BX = -2f, BY = 0f, BZ = 2f, CX = 2f, CY = 0f, CZ = 2f
            }
        };

        var first = SC2DecalGeometryBuilder.Build(document, source);
        var second = SC2DecalGeometryBuilder.Build(document, source);

        Assert.NotEmpty(first.Vertices);
        Assert.NotEmpty(first.Indices);
        Assert.Equal(first.Vertices.Count, second.Vertices.Count);
        Assert.Equal(first.Indices, second.Indices);
        Assert.All(first.Vertices, vertex =>
        {
            Assert.InRange(vertex.TexCoordX, 0f, 1f);
            Assert.InRange(vertex.TexCoordY, 0f, 1f);
            Assert.Equal(0.5f, vertex.ScaleOffsetX, 5);
            Assert.Equal(0.25f, vertex.ScaleOffsetY, 5);
            Assert.Equal(0.25f, vertex.ScaleOffsetZ, 5);
            Assert.Equal(0.125f, vertex.ScaleOffsetW, 5);
        });
    }

    [Fact]
    public void GeometryBuilder_UsesSmoothVertexNormals_NotAFlatPerTriangleNormal()
    {
        // Regression test: decals showed a visible
        // "polygon border" seam, in both the editor and the exported game
        // data, on any non-perfectly-flat surface. Root cause was a flat
        // per-source-triangle face normal (computed fresh from raw
        // positions) baked into TangentXform0/1/2, instead of the smooth
        // per-vertex normals real terrain shading already uses - adjacent
        // triangles with even a slight bend produced visibly different
        // normals at their shared edge.
        //
        // Two triangles sharing edge A-C, deliberately bent (D is raised to
        // y=1, unlike the flat A/B/C plane) so their raw flat face normals
        // genuinely differ - but every vertex is given the SAME explicit
        // "up" normal, simulating a smoothly-shaded surface whose baked
        // normals don't follow every small positional wrinkle. With the fix,
        // every decal vertex must end up with that same shared "up" normal
        // (interpolated from the source triangle's own vertex normals), not
        // each triangle's own differing flat cross-product normal.
        var document = new SC2DecalsDocument { AtlasWidth = 64, AtlasHeight = 64 };
        document.Entries.Add(new SC2DecalAtlasEntry { Id = "entry", X = 0, Y = 0, Width = 64, Height = 64 });
        document.Placements.Add(new SC2DecalPlacement
        {
            AtlasEntryId = "entry", Position = new[] { 0f, 0f, 0f },
            SurfaceNormal = new[] { 0f, 1f, 0f }, Width = 2f, Length = 2f, ProjectionDepth = 2f
        });
        var source = new[]
        {
            new SC2DecalSurfaceTriangle
            {
                AX = -2f, AY = 0f, AZ = -2f, BX = 2f, BY = 0f, BZ = -2f, CX = 2f, CY = 0f, CZ = 2f,
                HasNormals = true, NAX = 0f, NAY = 1f, NAZ = 0f, NBX = 0f, NBY = 1f, NBZ = 0f, NCX = 0f, NCY = 1f, NCZ = 0f
            },
            new SC2DecalSurfaceTriangle
            {
                AX = -2f, AY = 0f, AZ = -2f, BX = 2f, BY = 0f, BZ = 2f, CX = -2f, CY = 1f, CZ = 2f, // bent: C raised to y=1
                HasNormals = true, NAX = 0f, NAY = 1f, NAZ = 0f, NBX = 0f, NBY = 1f, NBZ = 0f, NCX = 0f, NCY = 1f, NCZ = 0f
            }
        };

        var model = SC2DecalGeometryBuilder.Build(document, source);

        Assert.NotEmpty(model.Vertices);
        Assert.All(model.Vertices, vertex =>
        {
            Assert.Equal(0f, vertex.TangentXform2X, 3);
            Assert.Equal(1f, vertex.TangentXform2Y, 3);
            Assert.Equal(0f, vertex.TangentXform2Z, 3);
        });
    }

    [Fact]
    public void GeometryBuilder_WithNoVertexNormals_FallsBackToFlatFaceNormal()
    {
        // Backward-compat: a source with no NA/NB/NC (e.g. an older caller,
        // or a mesh genuinely without normals) must keep working exactly as
        // before this fix - one flat normal per source triangle. Winding
        // order here is deliberately upward-facing (Cross(B-A, C-A) already
        // points toward the decal without needing the fallback-shading
        // flip) - matching real mesh data (mesh.triangles has consistent,
        // correct winding by construction) - since the angle-rejection test
        // added alongside this fix must use each triangle's real winding-
        // order-determined facing direction to work correctly (see
        // BuildPlacement's own comment on why it can't reuse the flip-
        // corrected fallback normal for that).
        var document = new SC2DecalsDocument { AtlasWidth = 64, AtlasHeight = 64 };
        document.Entries.Add(new SC2DecalAtlasEntry { Id = "entry", X = 0, Y = 0, Width = 64, Height = 64 });
        document.Placements.Add(new SC2DecalPlacement
        {
            AtlasEntryId = "entry", Position = new[] { 0f, 0f, 0f },
            SurfaceNormal = new[] { 0f, 1f, 0f }, Width = 2f, Length = 2f, ProjectionDepth = 2f
        });
        var source = new[]
        {
            new SC2DecalSurfaceTriangle { AX = -2f, AY = 0f, AZ = -2f, BX = 2f, BY = 0f, BZ = 2f, CX = 2f, CY = 0f, CZ = -2f },
            new SC2DecalSurfaceTriangle { AX = -2f, AY = 0f, AZ = -2f, BX = -2f, BY = 0f, BZ = 2f, CX = 2f, CY = 0f, CZ = 2f }
        };

        var model = SC2DecalGeometryBuilder.Build(document, source);

        Assert.NotEmpty(model.Vertices);
        Assert.All(model.Vertices, vertex =>
        {
            Assert.Equal(0f, vertex.TangentXform2X, 3);
            Assert.Equal(1f, vertex.TangentXform2Y, 3);
            Assert.Equal(0f, vertex.TangentXform2Z, 3);
        });
    }

    [Fact]
    public void GeometryBuilder_KeepsNearVerticalWall_ButRejectsTrueBackfacingOverhang()
    {
        // Regression test: on
        // genuinely rugged mountain terrain, a decal can keep getting cut off
        // if the angle-rejection threshold is too strict (e.g. ~87°) for how
        // much a single fixed placement normal can differ from a real
        // mountain's own widely-varying local facets. The threshold is
        // therefore maximally permissive -
        // reject ONLY true backfaces (Dot <= 0, i.e. more than 90° away,
        // like the underside of an overhang) - so even a near-vertical wall
        // (89° from the placement normal, well past the old 87° cutoff)
        // must now be KEPT, while a genuine overhang (angled so its face
        // points slightly AWAY from the decal, past 90°) must still be
        // rejected.
        //
        // This also regression-tests a real bug caught while implementing
        // this: the rejection check must run on the triangle's RAW
        // (winding-order) facing direction, not the "flip toward the decal"
        // -corrected fallback shading normal - reusing the flipped value
        // would make the whole rejection test a permanent no-op, since the
        // flip always forces a non-negative dot before any check could see
        // it.
        var document = new SC2DecalsDocument { AtlasWidth = 64, AtlasHeight = 64 };
        document.Entries.Add(new SC2DecalAtlasEntry { Id = "entry", X = 0, Y = 0, Width = 64, Height = 64 });
        document.Placements.Add(new SC2DecalPlacement
        {
            AtlasEntryId = "entry", Position = new[] { 0f, 0f, 0f },
            SurfaceNormal = new[] { 0f, 1f, 0f }, Width = 4f, Length = 4f, ProjectionDepth = 4f
        });
        var source = new[]
        {
            // Near-vertical wall, tilted very slightly outward (C.x = 0.51,
            // just past the wall's own base x=0.5) so its real face normal
            // has a small but genuinely positive Dot with the decal's "up"
            // normal (~89° away, not exactly 90°) - must be kept under the
            // new backfaces-only threshold.
            new SC2DecalSurfaceTriangle
            {
                AX = 0.5f, AY = 0f, AZ = -0.5f, BX = 0.5f, BY = 0f, BZ = 0.5f, CX = 0.51f, CY = 1f, CZ = 0f
            },
            // True overhang: tilted the OTHER way (C.x = 0.3, well past the
            // wall's own base back toward the decal center) so its real face
            // normal points slightly AWAY from the decal (Dot < 0, past 90°)
            // - must still be rejected.
            new SC2DecalSurfaceTriangle
            {
                AX = 1.5f, AY = 0f, AZ = -0.5f, BX = 1.5f, BY = 0f, BZ = 0.5f, CX = 1.3f, CY = 1f, CZ = 0f
            }
        };

        var model = SC2DecalGeometryBuilder.Build(document, source);

        Assert.NotEmpty(model.Vertices);
        // Only the near-vertical (but still front-facing) wall's 3 vertices
        // should survive; none of them come from x≈1.5 (the overhang).
        Assert.Equal(3, model.Vertices.Count);
        Assert.All(model.Vertices, vertex => Assert.True(vertex.PositionX < 1f,
            $"vertex at x={vertex.PositionX} came from the backfacing overhang triangle, which should have been rejected"));
    }

    [Fact]
    public void AuthoredModel_WithGeneratedSpatialRoot_RoundTripsThroughBdfContainer()
    {
        var document = new SC2DecalsDocument
        {
            AtlasWidth = 64,
            AtlasHeight = 64,
            DiffuseAtlasPath = "/maps/test/decals/diffuse.win.dds"
        };
        document.Entries.Add(new SC2DecalAtlasEntry
        {
            Id = "entry", X = 0, Y = 0, Width = 64, Height = 64
        });
        document.Placements.Add(new SC2DecalPlacement
        {
            AtlasEntryId = "entry", Position = new[] { 0f, 0f, 0f },
            SurfaceNormal = new[] { 0f, 1f, 0f }, Width = 2f, Length = 2f,
            ProjectionDepth = 2f
        });
        // Upward-facing winding — see the angle-rejection fix's own comment.
        var source = new[]
        {
            new SC2DecalSurfaceTriangle
            {
                AX = -2f, AY = 0f, AZ = -2f, BX = 2f, BY = 0f, BZ = 2f, CX = 2f, CY = 0f, CZ = -2f
            },
            new SC2DecalSurfaceTriangle
            {
                AX = -2f, AY = 0f, AZ = -2f, BX = -2f, BY = 0f, BZ = 2f, CX = 2f, CY = 0f, CZ = 2f
            }
        };

        var model = SC2DecalGeometryBuilder.Build(document, source);
        DecalsSpatialBuilder.Rebuild(model);
        var serialized = DecalsBdf.Serialize(model);
        var bytes = new BdfContainer
        {
            Platform = BdfPlatform.Pc,
            Data = serialized.Data,
            OffsetTable = serialized.OffsetTable,
            NumFiles = serialized.NumFiles,
            TrailingData = serialized.TrailingData
        }.Write(DecalsBdf.Padding);

        var roundTripContainer = BdfContainer.Read(bytes, DecalsBdf.Padding);
        var parsed = DecalsBdf.Parse(roundTripContainer);
        Assert.Equal(model.DiffuseAtlasPath, parsed.DiffuseAtlasPath);
        Assert.Equal(model.Vertices.Count, parsed.Vertices.Count);
        Assert.Equal(model.Indices, parsed.Indices);
        Assert.Equal(model.ExtraDataRaw, parsed.ExtraDataRaw);

        var extraStart = BitConverter.ToInt32(roundTripContainer.Data, 12);
        var geometryStart = checked((int)BitConverter.ToUInt32(roundTripContainer.TrailingData, 4));
        var root = checked((int)BitConverter.ToUInt32(roundTripContainer.Data, extraStart + 32));
        var spatialRelocations = roundTripContainer.OffsetTable.Count(offset => offset >= extraStart && offset < geometryStart);
        var nodeCount = (spatialRelocations - 10) / 2;
        Assert.Equal(extraStart + 108, root);
        Assert.True(nodeCount > 0);
        Assert.Equal(10 + nodeCount * 2, spatialRelocations);
        Assert.Equal(model.Vertices.Count - 1, BitConverter.ToInt32(roundTripContainer.Data, root + 36));
        Assert.Equal(model.Indices.Count / 3, BitConverter.ToInt32(roundTripContainer.Data, root + 44));

        parsed.DiffuseAtlasPath = "/maps/test/a/much/longer/diffuse.dds";
        var moved = DecalsBdf.Serialize(parsed);
        var movedExtraStart = BitConverter.ToInt32(moved.Data, 12);
        Assert.Equal(movedExtraStart + 108, BitConverter.ToInt32(moved.Data, movedExtraStart + 32));
    }

    [Fact]
    public void SpatialBuilder_WritesMultiNodeVanillaEdhEdbShape()
    {
        var model = new DecalsModel
        {
            DiffuseAtlasPath = "/maps/test/decals/diffuse.dds",
            NormalAtlasPath = "/maps/test/decals/normal.dds"
        };
        const int cells = 20;
        for (var z = 0; z <= cells; z++)
        for (var x = 0; x <= cells; x++)
        {
            model.Vertices.Add(new DecalVertex
            {
                PositionX = x, PositionY = 0f, PositionZ = z,
                TexCoordX = x / (float)cells, TexCoordY = z / (float)cells, TexCoordZ = 0f,
                ScaleOffsetX = 1f, ScaleOffsetY = 1f, ScaleOffsetZ = 0f, ScaleOffsetW = 0f,
                TangentXform0X = 1f, TangentXform0Y = 0f, TangentXform0Z = 0f,
                TangentXform1X = 0f, TangentXform1Y = 0f, TangentXform1Z = 1f,
                TangentXform2X = 0f, TangentXform2Y = 1f, TangentXform2Z = 0f
            });
        }
        for (var z = 0; z < cells; z++)
        for (var x = 0; x < cells; x++)
        {
            var a = z * (cells + 1) + x;
            var b = a + 1;
            var c = a + cells + 1;
            var d = c + 1;
            model.Indices.AddRange(new[] { a, c, b, b, c, d });
        }

        DecalsSpatialBuilder.Rebuild(model);
        var serialized = DecalsBdf.Serialize(model);
        var extraStart = BitConverter.ToInt32(serialized.Data, 12);
        var root = BitConverter.ToInt32(serialized.Data, extraStart + 32);
        var nodeCount = (model.ExtraDataOffsetTable.Length - 10) / 2;

        Assert.True(nodeCount > 1);
        Assert.Equal(extraStart + 108, root);
        Assert.Equal(10 + nodeCount * 2, model.ExtraDataOffsetTable.Length);
        Assert.Equal(model.Vertices.Count - 1, BitConverter.ToInt32(serialized.Data, root + 36));
        Assert.Equal(model.Indices.Count / 3, BitConverter.ToInt32(serialized.Data, root + 44));
        Assert.NotEqual(-1, BitConverter.ToInt32(serialized.Data, root + 48));
        Assert.NotEqual(-1, BitConverter.ToInt32(serialized.Data, root + 52));
    }
}
