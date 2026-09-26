using System;
using System.Collections.Generic;
using System.Linq;
using BdfIO.Formats.Decals;

namespace SC2.MapEditor.Document
{
    /// <summary>Value type, not a class — SC2DecalSurfaceProvider.Collect can
    /// produce well over half a million of these in one call (a handful of
    /// large decal placements, each tessellated into a dense terrain-height
    /// grid). Flat float fields instead of float[3] arrays means a
    /// List&lt;SC2DecalSurfaceTriangle&gt; stores every triangle inline in
    /// one contiguous backing array — zero additional heap allocations per
    /// triangle (the previous class + 6 separate float[3] arrays meant 7
    /// allocations each, ~3.5M+ objects for a single realistic preview
    /// rebuild — confirmed as one of the two dominant costs in
    /// SC2DecalsShell.EnsurePreview's own measured timing, alongside
    /// SC2DecalGeometryBuilder's clip pipeline).</summary>
    public struct SC2DecalSurfaceTriangle
    {
        public float AX, AY, AZ;
        public float BX, BY, BZ;
        public float CX, CY, CZ;
        /// <summary>Smooth per-vertex normals (mesh.normals — already
        /// averaged across every face sharing that vertex, exactly what the
        /// terrain's own shading already uses) — HasNormals is false when a
        /// source has none available (NAX/NAY/... are meaningless then). See
        /// BuildPlacement's own comment for why these matter: without them,
        /// decal shading facets visibly at every underlying triangle
        /// edge.</summary>
        public bool HasNormals;
        public float NAX, NAY, NAZ;
        public float NBX, NBY, NBZ;
        public float NCX, NCY, NCZ;

        /// <summary>Null for real, shared geometry (a collider/MeshFilter/Unity Terrain — there is
        /// only ever one instance of that mesh, so any placement whose bounds reach it should
        /// clip against it). Set to a placement's own Id for a synthetic quad
        /// SC2DecalSurfaceProvider.AddLiveHeightGrid tessellated JUST for that one placement's own
        /// footprint — two nearby placements each get their OWN independent tessellation of
        /// what can be overlapping ground, at different grid resolutions/alignments, so mixing
        /// one placement's synthetic quads into a DIFFERENT placement's clip produced duplicate,
        /// inconsistent geometry (confirmed real: a visible hard seam on a decal whose footprint
        /// overlapped a neighboring one, which only disappeared once that neighbor was excluded
        /// from the same Collect() call — see FilterTrianglesNearPlacement's own use of this).</summary>
        public string OwnerPlacementId;
    }

    /// <summary>Deterministic orthographic projector shared by imported and
    /// runtime terrain. The caller supplies final world-space triangles; this
    /// class clips them to each placement volume and emits the game layout.</summary>
    public static class SC2DecalGeometryBuilder
    {
        private struct V
        {
            public double X, Y, Z;
            public V(double x, double y, double z) { X = x; Y = y; Z = z; }
            public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static V operator *(V a, double b) => new(a.X * b, a.Y * b, a.Z * b);
        }

        /// <summary>Value type (not a class) — see BuildPlacement's own
        /// comment on why avoiding a heap allocation per vertex/clip-step
        /// matters here.</summary>
        private struct P { public V World; public V Local; }

        /// <summary>Minimum Dot(sourceTriangleNormal, decalPlacementNormal)
        /// for a source triangle to be considered part of the surface a
        /// decal is projecting onto at all. User-reported real bug, twice:
        /// 0.05 (~87°) was still too strict on genuinely rugged/mountainous
        /// terrain — a single fixed placement normal can't describe a whole
        /// real mountain's actual surface orientation (a mountain has many
        /// faces pointing many directions; a rocky peak can easily have
        /// LOCAL facets well past 87° from wherever the decal happened to be
        /// placed, while still clearly being "the same feature" the decal is
        /// meant to cover), so real, legitimate rock was still being
        /// excluded and read as clipping. The only genuinely necessary
        /// safety net is excluding true BACKFACES (Dot <= 0, i.e. more than
        /// 90° away — geometry actually facing away from the decal, like the
        /// underside of an overhang, where projecting through to it would be
        /// wrong regardless of terrain ruggedness); anything with even a
        /// sliver of front-facing component should receive the decal like
        /// everything else within its footprint/depth bounds.</summary>
        private const double MinFaceAngleDot = 0.0;

        public static DecalsModel Build(SC2DecalsDocument document, IEnumerable<SC2DecalSurfaceTriangle> source)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var model = new DecalsModel { DiffuseAtlasPath = document.DiffuseAtlasPath ?? "", NormalAtlasPath = document.NormalAtlasPath ?? "" };
            if (document.AtlasWidth <= 0 || document.AtlasHeight <= 0) return model;
            var triangles = source?.ToList() ?? new List<SC2DecalSurfaceTriangle>();
            var entries = document.Entries.Where(e => e != null).ToDictionary(e => e.Id, e => e);

            foreach (var placement in document.Placements.Where(p => p != null && p.Enabled))
            {
                if (!entries.TryGetValue(placement.AtlasEntryId ?? "", out var entry)) continue;
                BuildPlacement(model, placement, entry, FilterTrianglesNearPlacement(triangles, placement), document.AtlasWidth, document.AtlasHeight);
            }

            return model;
        }

        /// <summary>Builds ONE placement's own clipped geometry in isolation,
        /// with LOCAL indices starting at 0 (not offset into a shared
        /// document-wide vertex list) — lets a caller (SC2DecalsShell.
        /// EnsurePreview) cache each placement's fragment independently and
        /// only recompute the ones actually affected by a change, instead of
        /// re-running Build() for the WHOLE document (every placement) on
        /// every single terrain edit regardless of whether the edit was
        /// anywhere near most of them.</summary>
        public static (List<DecalVertex> Vertices, List<int> Indices) BuildPlacementFragment(
            SC2DecalPlacement placement, SC2DecalAtlasEntry entry, List<SC2DecalSurfaceTriangle> triangles, int atlasW, int atlasH)
        {
            var fragment = new DecalsModel();
            BuildPlacement(fragment, placement, entry, FilterTrianglesNearPlacement(triangles, placement), atlasW, atlasH);
            return (fragment.Vertices, fragment.Indices);
        }

        /// <summary>Cheap X/Z bounding-box pre-filter, run once per placement
        /// before BuildPlacement's own expensive per-triangle clip/normal
        /// math. Without this, every placement scanned the SAME shared
        /// triangle list collected for the WHOLE document (every other
        /// placement's own terrain patch included) — with N placements that
        /// is O(triangles x N) instead of O(triangles), and on a real map it
        /// dominated the whole preview rebuild (confirmed via SC2DecalsShell's
        /// own EnsurePreview timing: ~1.1s spent here alone for just 4
        /// placements / ~74k triangles, most of them irrelevant to any given
        /// one). Only X/Z is checked (not the placement's projection depth)
        /// so this can never incorrectly exclude a real triangle regardless
        /// of how the placement's own surface normal is oriented — the exact
        /// depth/angle clipping BuildPlacement already does per-triangle is
        /// untouched, this only skips triangles nowhere near the footprint at
        /// all. Diagonal (not Width/Length directly) is used as the half-
        /// extent for the same reason SC2DecalSurfaceProvider.PlacementBoundsList
        /// does — a rotated rectangle's true footprint reaches past an
        /// unrotated box at its corners, so anything smaller could wrongly
        /// drop real triangles.</summary>
        private static List<SC2DecalSurfaceTriangle> FilterTrianglesNearPlacement(List<SC2DecalSurfaceTriangle> triangles, SC2DecalPlacement p)
        {
            if (p.Position == null || p.Position.Length < 3) return triangles;
            var halfDiag = Math.Sqrt((double)p.Width * p.Width + (double)p.Length * p.Length) * 0.5;
            double cx = p.Position[0], cz = p.Position[2];
            var minX = cx - halfDiag; var maxX = cx + halfDiag;
            var minZ = cz - halfDiag; var maxZ = cz + halfDiag;

            var result = new List<SC2DecalSurfaceTriangle>();
            foreach (var t in triangles)
            {
                // A synthetic quad tessellated for a DIFFERENT placement's own footprint (see
                // OwnerPlacementId's own doc comment) — two nearby placements' independent
                // tessellations of the same physical ground can overlap in world space (bounding
                // boxes intersecting) while each grid's own quad boundaries and resolution differ,
                // so mixing them into one placement's clip produced duplicate/inconsistent
                // geometry — a visible hard seam right at the overlap boundary. Real, shared
                // geometry (OwnerPlacementId null — a collider/MeshFilter/Unity Terrain) is
                // unaffected and still clips against every nearby placement as before.
                if (!string.IsNullOrEmpty(t.OwnerPlacementId) && t.OwnerPlacementId != p.Id) continue;
                var triMinX = Math.Min(t.AX, Math.Min(t.BX, t.CX));
                if (triMinX > maxX) continue;
                var triMaxX = Math.Max(t.AX, Math.Max(t.BX, t.CX));
                if (triMaxX < minX) continue;
                var triMinZ = Math.Min(t.AZ, Math.Min(t.BZ, t.CZ));
                if (triMinZ > maxZ) continue;
                var triMaxZ = Math.Max(t.AZ, Math.Max(t.BZ, t.CZ));
                if (triMaxZ < minZ) continue;
                result.Add(t);
            }
            return result;
        }

        private static void BuildPlacement(DecalsModel output, SC2DecalPlacement p, SC2DecalAtlasEntry e,
            List<SC2DecalSurfaceTriangle> triangles, int atlasW, int atlasH)
        {
            var center = Vec(p.Position, 0, 0, 0);
            var normal = Normalize(Vec(p.SurfaceNormal, 0, 1, 0));
            var reference = Math.Abs(normal.Y) < 0.99 ? new V(0, 1, 0) : new V(0, 0, 1);
            var right = Normalize(Cross(reference, normal));
            var forward = Normalize(Cross(normal, right));
            var angle = p.Rotation * Math.PI / 180.0;
            var rotatedRight = right * Math.Cos(angle) + forward * Math.Sin(angle);
            var rotatedForward = forward * Math.Cos(angle) - right * Math.Sin(angle);
            right = rotatedRight; forward = rotatedForward;
            var halfW = Math.Max(0.0001, p.Width * 0.5);
            var halfL = Math.Max(0.0001, p.Length * 0.5);
            var depth = Math.Max(0.0001, p.ProjectionDepth);

            // Reused across every triangle in this placement instead of a
            // fresh List<P> per Clip() call (the previous approach) — a
            // convex polygon clipped against one half-plane can gain at most
            // one vertex, so starting from a triangle (3) and clipping
            // against 6 planes below can never exceed 3+6=9 vertices, making
            // two fixed 9-slot buffers always enough. Confirmed real cost:
            // on a map with tens of thousands of source triangles per
            // placement, the old version allocated a new List<P> (and, since
            // P was a class, a new P object per emitted vertex) six times
            // PER TRIANGLE — hundreds of thousands of small heap allocations
            // per preview rebuild, one of the two dominant costs in
            // SC2DecalsShell.EnsurePreview's measured rebuild time. Per the
            // SAME GC-pause behavior already diagnosed for SC2LiveHeightGrid,
            // a stall like this isn't fixed by moving the work elsewhere,
            // only by allocating less.
            var bufA = new P[9];
            var bufB = new P[9];

            foreach (var t in triangles)
            {
                var worldA = new V(t.AX, t.AY, t.AZ); var worldB = new V(t.BX, t.BY, t.BZ); var worldC = new V(t.CX, t.CY, t.CZ);
                var hasVertexNormals = t.HasNormals;
                var normalA = hasVertexNormals ? Normalize(new V(t.NAX, t.NAY, t.NAZ)) : default;
                var normalB = hasVertexNormals ? Normalize(new V(t.NBX, t.NBY, t.NBZ)) : default;
                var normalC = hasVertexNormals ? Normalize(new V(t.NCX, t.NCY, t.NCZ)) : default;
                var rawFlatNormal = Normalize(Cross(worldB - worldA, worldC - worldA));

                // A real bug: on
                // genuinely rugged/steep terrain, a decal got cut off
                // partway across its own footprint. The depth clip below (a
                // fixed +-ProjectionDepth/2 band around the placement plane)
                // is the correct way to stop a decal from wrapping onto
                // CLEARLY unrelated geometry, but tuning it to "just barely"
                // cover legitimate steep relief is fundamentally fragile.
                // This angle test is the actual correct tool for "don't
                // bleed onto unrelated geometry" (reject only genuinely
                // backfacing triangles, MinFaceAngleDot = 0 — see its own
                // doc comment for why even a fairly permissive 87° threshold
                // still turned out too strict on a real mountain), which
                // frees the depth clip to be very generous (see
                // SC2DecalsShell.ComputeAdequateProjectionDepth) without
                // reintroducing wrap-around bleed.
                //
                // MUST use the RAW (winding-order-determined) direction here,
                // or the smooth average of the source's own per-vertex
                // normals when available (more representative of the real
                // surface than one triangle's flat cross-product) - NOT the
                // flip-corrected flatFaceNormal below. That flip exists
                // purely to fix the FALLBACK SHADING normal's polarity when
                // no per-vertex normals exist; reusing it here would silently
                // make this whole rejection test a no-op, since the flip
                // always forces a non-negative dot before this check could
                // ever see a genuinely backfacing triangle (a real bug caught
                // before shipping — the flip ran before the check).
                var rejectionNormal = hasVertexNormals ? Normalize(normalA + normalB + normalC) : rawFlatNormal;
                if (Dot(rejectionNormal, normal) < MinFaceAngleDot) continue;

                // Flat fallback face normal — only used whole-polygon when
                // the source had no per-vertex normals at all (see below).
                var flatFaceNormal = Dot(rawFlatNormal, normal) < 0 ? rawFlatNormal * -1 : rawFlatNormal;

                P Point(V world)
                {
                    var d = world - center;
                    return new P { World = world, Local = new V(Dot(d, right) / halfW, Dot(d, normal) / depth, Dot(d, forward) / halfL) };
                }

                bufA[0] = Point(worldA); bufA[1] = Point(worldB); bufA[2] = Point(worldC);
                var count = 3;
                count = ClipInto(bufA, count, bufB, q => q.Local.X + 1); count = ClipInto(bufB, count, bufA, q => 1 - q.Local.X);
                count = ClipInto(bufA, count, bufB, q => q.Local.Z + 1); count = ClipInto(bufB, count, bufA, q => 1 - q.Local.Z);
                count = ClipInto(bufA, count, bufB, q => q.Local.Y + 0.5); count = ClipInto(bufB, count, bufA, q => 0.5 - q.Local.Y);
                if (count < 3) continue;
                // Final result lives in bufA[0..count) — an even number
                // (6) of ClipInto calls above always leaves the result back
                // in bufA.

                var baseIndex = output.Vertices.Count;
                for (var qi = 0; qi < count; qi++)
                {
                    var q = bufA[qi];
                    // Confirmed real bug — a visibly faceted decal on a rock
                    // surface, in both
                    // the editor and the exported game data: this used to
                    // compute ONE flat face normal per SOURCE triangle from
                    // raw world positions and share it across the whole
                    // clipped fragment's vertices/tangent basis. Adjacent
                    // source triangles on any non-perfectly-flat surface
                    // (any real rock/cliff/terrain detail) have slightly
                    // different flat normals, so the decal's own lighting
                    // (TangentXform0/1/2, baked straight into decals.bdf)
                    // jumped abruptly at every underlying triangle edge -
                    // visible as a hard edge along each polygon border. Real terrain
                    // shading avoids exactly this by smoothly interpolating
                    // per-vertex normals across a triangle; doing the same
                    // here (barycentric-interpolating the ORIGINAL triangle's
                    // smooth vertex normals at each clipped point, instead of
                    // one flat cross-product per fragment) makes the decal's
                    // normal continuous across shared edges, matching how the
                    // underlying surface itself already shades smoothly.
                    var shadingNormal = hasVertexNormals
                        ? Normalize(BarycentricNormal(worldA, worldB, worldC, normalA, normalB, normalC, q.World))
                        : flatFaceNormal;
                    if (Dot(shadingNormal, normal) < 0) shadingNormal = shadingNormal * -1;
                    // U runs against `right` (the decal's atlas image is mirrored in U, otherwise text
                    // in a decal reads right to left): the tangent follows +U, so it points along
                    // -right; the binormal stays along +V, as it was.
                    var tangent = Normalize(right * -1 - shadingNormal * Dot(right * -1, shadingNormal));
                    var binormal = Normalize(Cross(shadingNormal, right - shadingNormal * Dot(right, shadingNormal)));

                    var world = q.World + shadingNormal * p.HeightOffset;
                    output.Vertices.Add(new DecalVertex
                    {
                        PositionX = (float)world.X, PositionY = (float)world.Y, PositionZ = (float)world.Z,
                        TexCoordX = (float)(0.5 - q.Local.X * 0.5), TexCoordY = (float)(q.Local.Z * 0.5 + 0.5), TexCoordZ = (float)(q.Local.Y + 0.5),
                        ScaleOffsetX = e.Width / (float)atlasW, ScaleOffsetY = e.Height / (float)atlasH, ScaleOffsetZ = e.X / (float)atlasW, ScaleOffsetW = e.Y / (float)atlasH,
                        TangentXform0X = (float)tangent.X, TangentXform0Y = (float)tangent.Y, TangentXform0Z = (float)tangent.Z,
                        TangentXform1X = (float)binormal.X, TangentXform1Y = (float)binormal.Y, TangentXform1Z = (float)binormal.Z,
                        TangentXform2X = (float)shadingNormal.X, TangentXform2Y = (float)shadingNormal.Y, TangentXform2Z = (float)shadingNormal.Z
                    });
                }
                for (var i = 1; i + 1 < count; i++)
                { output.Indices.Add(baseIndex); output.Indices.Add(baseIndex + i); output.Indices.Add(baseIndex + i + 1); }
            }
        }

        /// <summary>Sutherland-Hodgman half-plane clip against a fixed
        /// output buffer instead of allocating a List — see BuildPlacement's
        /// own comment on why. `output` must be a different array than
        /// `input` (the two 9-slot buffers are ping-ponged by the caller);
        /// writing into the same array while reading it would corrupt
        /// not-yet-processed input entries.</summary>
        private static int ClipInto(P[] input, int count, P[] output, Func<P, double> distance)
        {
            if (count == 0) return 0;
            var outCount = 0;
            var previous = input[count - 1]; var pd = distance(previous); var pin = pd >= 0;
            for (var i = 0; i < count; i++)
            {
                var current = input[i];
                var cd = distance(current); var cin = cd >= 0;
                if (cin != pin)
                {
                    var t = pd / (pd - cd);
                    output[outCount++] = new P { World = previous.World + (current.World - previous.World) * t, Local = previous.Local + (current.Local - previous.Local) * t };
                }
                if (cin) output[outCount++] = current;
                previous = current; pd = cd; pin = cin;
            }
            return outCount;
        }

        /// <summary>Interpolates the original triangle's smooth per-vertex
        /// normals at world-space point `p` (which always lies within the
        /// A/B/C triangle — clipping only ever keeps points on the correct
        /// side of a plane or linearly interpolates along an existing edge,
        /// so it can never produce a point outside the original triangle's
        /// affine span) via standard barycentric weights. Falls back to the
        /// (already-normalized) A normal if the triangle is degenerate
        /// (zero area) rather than dividing by zero.</summary>
        private static V BarycentricNormal(V a, V b, V c, V na, V nb, V nc, V p)
        {
            var v0 = b - a; var v1 = c - a; var v2 = p - a;
            var d00 = Dot(v0, v0); var d01 = Dot(v0, v1); var d11 = Dot(v1, v1);
            var d20 = Dot(v2, v0); var d21 = Dot(v2, v1);
            var denom = d00 * d11 - d01 * d01;
            if (Math.Abs(denom) < 1e-12) return na;
            var v = (d11 * d20 - d01 * d21) / denom;
            var w = (d00 * d21 - d01 * d20) / denom;
            var u = 1.0 - v - w;
            return na * u + nb * v + nc * w;
        }

        private static V Vec(float[] a, double x, double y, double z) => a != null && a.Length >= 3 ? new V(a[0], a[1], a[2]) : new V(x, y, z);
        private static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static V Cross(V a, V b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        private static V Normalize(V v) { var l = Math.Sqrt(Dot(v, v)); return l > 1e-12 ? v * (1 / l) : new V(0, 1, 0); }
    }

}
