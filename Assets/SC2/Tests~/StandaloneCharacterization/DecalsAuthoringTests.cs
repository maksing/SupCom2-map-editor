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
    public void GeometryBuilder_MirrorsU_SoFlatDecalReadsLeftToRightAlongWorldX()
    {
        // Text in an atlas image must read left to right when the decal lies flat and is viewed
        // from above with +X to the right: U increases along world +X, V along +Z.
        var document = new SC2DecalsDocument { AtlasWidth = 64, AtlasHeight = 64 };
        document.Entries.Add(new SC2DecalAtlasEntry { Id = "entry", X = 0, Y = 0, Width = 64, Height = 64 });
        document.Placements.Add(new SC2DecalPlacement
        {
            AtlasEntryId = "entry", Position = new[] { 0f, 0f, 0f }, SurfaceNormal = new[] { 0f, 1f, 0f },
            Rotation = 0f, Width = 4f, Length = 4f, ProjectionDepth = 4f, HeightOffset = 0f
        });
        var source = new[]
        {
            new SC2DecalSurfaceTriangle { AX = -2f, AY = 0f, AZ = -2f, BX = 2f, BY = 0f, BZ = 2f, CX = 2f, CY = 0f, CZ = -2f },
            new SC2DecalSurfaceTriangle { AX = -2f, AY = 0f, AZ = -2f, BX = -2f, BY = 0f, BZ = 2f, CX = 2f, CY = 0f, CZ = 2f },
        };
        var model = SC2DecalGeometryBuilder.Build(document, source);
        var left = model.Vertices.First(v => v.PositionX < -1.9f);
        var right = model.Vertices.First(v => v.PositionX > 1.9f);
        Assert.True(right.TexCoordX > left.TexCoordX, "U must increase toward +X");
        var near = model.Vertices.First(v => v.PositionZ < -1.9f);
        var far = model.Vertices.First(v => v.PositionZ > 1.9f);
        Assert.True(far.TexCoordY > near.TexCoordY, "V must still increase toward +Z");
        // the tangent follows +U (along +X here); the binormal is Normal x Tangent (so the frame is
        // right-handed, as on every vanilla decal) - that points along -Z, against +V
        Assert.True(right.TangentXform0X > 0.9f);
        Assert.True(right.TangentXform1Z < -0.9f);
    }

    private static SC2DecalsDocument FlatDecalDocument(float rotation = 0f)
    {
        var document = new SC2DecalsDocument { AtlasWidth = 64, AtlasHeight = 64 };
        document.Entries.Add(new SC2DecalAtlasEntry { Id = "entry", X = 0, Y = 0, Width = 64, Height = 64 });
        document.Placements.Add(new SC2DecalPlacement
        {
            AtlasEntryId = "entry", Position = new[] { 0f, 0f, 0f }, SurfaceNormal = new[] { 0f, 1f, 0f },
            Rotation = rotation, Width = 4f, Length = 4f, ProjectionDepth = 4f, HeightOffset = 0f
        });
        return document;
    }

    // Flat ground whose own tangent frame is (T, B, N) = (tangent, binormal, up).
    private static SC2DecalSurfaceTriangle[] FlatGround(float[] tangent, float[] binormal, bool withFrame)
    {
        SC2DecalSurfaceTriangle Tri(float ax, float az, float bx, float bz, float cx, float cz) => new()
        {
            AX = ax, AY = 0f, AZ = az, BX = bx, BY = 0f, BZ = bz, CX = cx, CY = 0f, CZ = cz,
            HasNormals = true, NAY = 1f, NBY = 1f, NCY = 1f,
            HasTangentFrame = withFrame,
            TgAX = tangent[0], TgAY = tangent[1], TgAZ = tangent[2], TgBX = tangent[0], TgBY = tangent[1], TgBZ = tangent[2],
            TgCX = tangent[0], TgCY = tangent[1], TgCZ = tangent[2],
            BnAX = binormal[0], BnAY = binormal[1], BnAZ = binormal[2], BnBX = binormal[0], BnBY = binormal[1], BnBZ = binormal[2],
            BnCX = binormal[0], BnCY = binormal[1], BnCZ = binormal[2]
        };
        return new[] { Tri(-2, -2, 2, 2, 2, -2), Tri(-2, -2, -2, 2, 2, 2) };
    }

    [Fact]
    public void WorldSpaceFrame_IsRightHanded_LikeEveryVanillaDecal()
    {
        var model = SC2DecalGeometryBuilder.Build(FlatDecalDocument(), FlatGround(new[] { 1f, 0f, 0f }, new[] { 0f, 0f, 1f }, withFrame: false));
        Assert.NotEmpty(model.Vertices);
        Assert.All(model.Vertices, v =>
        {
            var t = new[] { v.TangentXform0X, v.TangentXform0Y, v.TangentXform0Z };
            var b = new[] { v.TangentXform1X, v.TangentXform1Y, v.TangentXform1Z };
            var n = new[] { v.TangentXform2X, v.TangentXform2Y, v.TangentXform2Z };
            var cross = new[] { t[1] * b[2] - t[2] * b[1], t[2] * b[0] - t[0] * b[2], t[0] * b[1] - t[1] * b[0] };
            Assert.True(cross[0] * n[0] + cross[1] * n[1] + cross[2] * n[2] > 0.99f, "(T0, T1, N) must be right-handed");
        });
    }

    [Fact]
    public void TerrainTangentSpace_WritesTheDecalBasisInTheTerrainsOwnFrame()
    {
        // Terrain frame: tangent +Z, binormal +X, normal up. The decal's U runs along +X, so in
        // terrain coordinates T0 = (0,1,0); its binormal N x T0 = -Z gives T1 = (-1,0,0); T2 = (0,0,1).
        var model = SC2DecalGeometryBuilder.Build(FlatDecalDocument(), FlatGround(new[] { 0f, 0f, 1f }, new[] { 1f, 0f, 0f }, withFrame: true), terrainTangentSpace: true);
        Assert.NotEmpty(model.Vertices);
        Assert.All(model.Vertices, v =>
        {
            Assert.Equal(0f, v.TangentXform0X, 3); Assert.Equal(1f, v.TangentXform0Y, 3); Assert.Equal(0f, v.TangentXform0Z, 3);
            Assert.Equal(-1f, v.TangentXform1X, 3); Assert.Equal(0f, v.TangentXform1Y, 3); Assert.Equal(0f, v.TangentXform1Z, 3);
            Assert.Equal(0f, v.TangentXform2X, 3); Assert.Equal(0f, v.TangentXform2Y, 3); Assert.Equal(1f, v.TangentXform2Z, 3);
        });
    }

    [Fact]
    public void TerrainTangentSpace_LiesInTheTangentPlane_AndFollowsTheDecalRotation()
    {
        // Right-handed terrain frame aligned with the world (T = +X, B = -Z, N = up): T0 is the decal's U
        // direction in the terrain's xy, whatever the decal's rotation; T0.z = T1.z = 0 and T2 = (0,0,1).
        foreach (var rotation in new[] { 0f, 30f, 90f, 200f })
        {
            var model = SC2DecalGeometryBuilder.Build(FlatDecalDocument(rotation), FlatGround(new[] { 1f, 0f, 0f }, new[] { 0f, 0f, -1f }, withFrame: true), terrainTangentSpace: true);
            Assert.NotEmpty(model.Vertices);
            Assert.All(model.Vertices, v =>
            {
                Assert.Equal(0f, v.TangentXform0Z, 3);
                Assert.Equal(0f, v.TangentXform1Z, 3);
                Assert.Equal(1f, v.TangentXform0X * v.TangentXform0X + v.TangentXform0Y * v.TangentXform0Y, 3);
                Assert.Equal(0f, v.TangentXform0X * v.TangentXform1X + v.TangentXform0Y * v.TangentXform1Y, 3);
                Assert.Equal(1f, v.TangentXform2Z, 3);
                // T1 = N x T0 with N = +Z in terrain coordinates: (-T0.y, T0.x, 0)
                Assert.Equal(-v.TangentXform0Y, v.TangentXform1X, 3);
                Assert.Equal(v.TangentXform0X, v.TangentXform1Y, 3);
            });
        }
    }

    [Fact]
    public void TerrainTangentSpace_KeepsTheDecalFrameRightHandedInTheWorld_EvenOnALeftHandedTerrainFrame()
    {
        // Vanilla terrain vertex frames are not all the same handedness (they come from the UV1
        // derivatives), yet every vanilla decal frame is right-handed in the world. So on a
        // left-handed terrain frame the coordinates rotate the other way: T1 = (T0.y, -T0.x).
        var tangent = new[] { 1f, 0f, 0f }; var binormal = new[] { 0f, 0f, 1f }; // T x B = -N
        foreach (var rotation in new[] { 0f, 40f, 130f })
        {
            var model = SC2DecalGeometryBuilder.Build(FlatDecalDocument(rotation), FlatGround(tangent, binormal, withFrame: true), terrainTangentSpace: true);
            Assert.NotEmpty(model.Vertices);
            Assert.All(model.Vertices, v =>
            {
                Assert.Equal(v.TangentXform0Y, v.TangentXform1X, 3);
                Assert.Equal(-v.TangentXform0X, v.TangentXform1Y, 3);
                // back to world: D0 = T0.x*T + T0.y*B, D1 = T1.x*T + T1.y*B; D0 x D1 must point along +Y (N)
                var d0 = new[] { v.TangentXform0X * tangent[0] + v.TangentXform0Y * binormal[0], 0f, v.TangentXform0X * tangent[2] + v.TangentXform0Y * binormal[2] };
                var d1 = new[] { v.TangentXform1X * tangent[0] + v.TangentXform1Y * binormal[0], 0f, v.TangentXform1X * tangent[2] + v.TangentXform1Y * binormal[2] };
                Assert.True(d0[2] * d1[0] - d0[0] * d1[2] > 0.99f, "decal frame must stay right-handed in the world");
            });
        }
    }

    [Fact]
    public void TerrainTangentSpace_FallsBackToWorldSpace_WhenTheSourceHasNoTerrainFrame()
    {
        var withoutFrame = SC2DecalGeometryBuilder.Build(FlatDecalDocument(), FlatGround(new[] { 0f, 0f, 1f }, new[] { 1f, 0f, 0f }, withFrame: false), terrainTangentSpace: true);
        Assert.NotEmpty(withoutFrame.Vertices);
        // world space: the decal's normal is up, not (0,0,1)
        Assert.All(withoutFrame.Vertices, v => Assert.Equal(1f, v.TangentXform2Y, 3));
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

    private static DecalsModel OneQuadModel(string diffusePath, string normalPath)
    {
        var document = new SC2DecalsDocument
        {
            AtlasWidth = 64, AtlasHeight = 64, DiffuseAtlasPath = diffusePath, NormalAtlasPath = normalPath
        };
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
        DecalsSpatialBuilder.Rebuild(model);
        return model;
    }

    [Theory]
    [InlineData(54, 53)]   // ends at 71 / 125: neither is a multiple of 4 (what a packed pool wrote)
    [InlineData(59, 58)]   // vanilla's common case
    [InlineData(0, 40)]    // no diffuse path at all
    [InlineData(40, 0)]    // no normal atlas (vanilla SC2_D1_101 has exactly this)
    public void StringPool_StartsEveryStringAndTheSpatialBlockOnAFourByteBoundary_LikeVanilla(int diffuseLength, int normalLength)
    {
        var model = OneQuadModel("/" + new string('d', Math.Max(0, diffuseLength - 1)), "/" + new string('n', Math.Max(0, normalLength - 1)));
        if (diffuseLength == 0) model.DiffuseAtlasPath = "";
        if (normalLength == 0) model.NormalAtlasPath = "";
        DecalsSpatialBuilder.Rebuild(model);
        var serialized = DecalsBdf.Serialize(model);
        var data = serialized.Data;

        var diffuseOff = BitConverter.ToInt32(data, 4);
        var normalOff = BitConverter.ToInt32(data, 8);
        var extraOff = BitConverter.ToInt32(data, 12);
        var geometryStart = checked((int)BitConverter.ToUInt32(serialized.TrailingData, 4));

        Assert.Equal(16, diffuseOff);
        Assert.Equal(0, normalOff % 4);
        Assert.Equal(0, extraOff % 4);
        Assert.Equal(0, geometryStart % 32);
        Assert.Equal(0, data.Length % 32);
        Assert.Equal(model.DiffuseAtlasPath, System.Text.Encoding.ASCII.GetString(data, diffuseOff, model.DiffuseAtlasPath.Length));
        Assert.Equal(model.NormalAtlasPath, System.Text.Encoding.ASCII.GetString(data, normalOff, model.NormalAtlasPath.Length));
        Assert.Equal(0, data[normalOff + model.NormalAtlasPath.Length]);
        Assert.InRange(normalOff - (diffuseOff + model.DiffuseAtlasPath.Length + 1), 0, 3);
        Assert.InRange(extraOff - (normalOff + model.NormalAtlasPath.Length + 1), 0, 3);

        // The generated EDB pointers must match the offset the block was actually placed at.
        Assert.Equal(extraOff + 108, BitConverter.ToInt32(data, extraOff + 32));

        // And the container still round-trips it.
        var bytes = new BdfContainer
        {
            Platform = BdfPlatform.Pc, Data = data, OffsetTable = serialized.OffsetTable,
            NumFiles = serialized.NumFiles, TrailingData = serialized.TrailingData
        }.Write(DecalsBdf.Padding);
        var parsed = DecalsBdf.Parse(BdfContainer.Read(bytes, DecalsBdf.Padding));
        Assert.Equal(model.NormalAtlasPath, parsed.NormalAtlasPath);
        Assert.Equal(model.Vertices.Count, parsed.Vertices.Count);
    }

    [Fact]
    public void EmptyStub_MatchesTheVanilla64ByteLayout()
    {
        var serialized = DecalsBdf.Serialize(new DecalsModel());
        Assert.Equal(64, serialized.Data.Length);
        Assert.Equal(new[] { 3, 16, 16, -1 }, new[]
        {
            BitConverter.ToInt32(serialized.Data, 0), BitConverter.ToInt32(serialized.Data, 4),
            BitConverter.ToInt32(serialized.Data, 8), BitConverter.ToInt32(serialized.Data, 12)
        });
        Assert.Equal(32u, BitConverter.ToUInt32(serialized.TrailingData, 4));
        Assert.All(serialized.Data.Skip(16).Take(16), b => Assert.Equal(0, b));
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
