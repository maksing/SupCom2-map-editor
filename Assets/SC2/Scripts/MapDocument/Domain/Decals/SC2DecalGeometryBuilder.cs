using System;
using System.Collections.Generic;
using System.Linq;
using BdfIO.Formats.Decals;

namespace SC2.MapEditor.Document
{
    public sealed class SC2DecalSurfaceTriangle
    {
        public float[] A = new float[3];
        public float[] B = new float[3];
        public float[] C = new float[3];
        /// <summary>Smooth per-vertex normals (mesh.normals — already
        /// averaged across every face sharing that vertex, exactly what the
        /// terrain's own shading already uses), left null when a source has
        /// none available. See BuildPlacement's own comment for why these
        /// matter: without them, decal shading facets visibly at every
        /// underlying triangle edge.</summary>
        public float[] NA;
        public float[] NB;
        public float[] NC;
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

        private sealed class P { public V World; public V Local; }

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
                BuildPlacement(model, placement, entry, triangles, document.AtlasWidth, document.AtlasHeight);
            }

            return model;
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

            foreach (var t in triangles)
            {
                if (t?.A == null || t.B == null || t.C == null) continue;
                var worldA = Vec(t.A, 0, 0, 0); var worldB = Vec(t.B, 0, 0, 0); var worldC = Vec(t.C, 0, 0, 0);
                var hasVertexNormals = t.NA != null && t.NB != null && t.NC != null;
                var normalA = hasVertexNormals ? Normalize(Vec(t.NA, 0, 1, 0)) : default;
                var normalB = hasVertexNormals ? Normalize(Vec(t.NB, 0, 1, 0)) : default;
                var normalC = hasVertexNormals ? Normalize(Vec(t.NC, 0, 1, 0)) : default;
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

                var polygon = new List<P>(3) { Point(worldA), Point(worldB), Point(worldC) };
                P Point(V world)
                {
                    var d = world - center;
                    return new P { World = world, Local = new V(Dot(d, right) / halfW, Dot(d, normal) / depth, Dot(d, forward) / halfL) };
                }

                polygon = Clip(polygon, q => q.Local.X + 1, 0); polygon = Clip(polygon, q => 1 - q.Local.X, 0);
                polygon = Clip(polygon, q => q.Local.Z + 1, 0); polygon = Clip(polygon, q => 1 - q.Local.Z, 0);
                polygon = Clip(polygon, q => q.Local.Y + 0.5, 0); polygon = Clip(polygon, q => 0.5 - q.Local.Y, 0);
                if (polygon.Count < 3) continue;

                var baseIndex = output.Vertices.Count;
                foreach (var q in polygon)
                {
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
                    var tangent = Normalize(right - shadingNormal * Dot(right, shadingNormal));
                    var binormal = Normalize(Cross(shadingNormal, tangent));

                    var world = q.World + shadingNormal * p.HeightOffset;
                    output.Vertices.Add(new DecalVertex
                    {
                        Position = F(world),
                        TexCoord = new[] { (float)(q.Local.X * 0.5 + 0.5), (float)(q.Local.Z * 0.5 + 0.5), (float)(q.Local.Y + 0.5) },
                        ScaleOffset = new[] { e.Width / (float)atlasW, e.Height / (float)atlasH, e.X / (float)atlasW, e.Y / (float)atlasH },
                        TangentXform0 = F(tangent), TangentXform1 = F(binormal), TangentXform2 = F(shadingNormal)
                    });
                }
                for (var i = 1; i + 1 < polygon.Count; i++)
                { output.Indices.Add(baseIndex); output.Indices.Add(baseIndex + i); output.Indices.Add(baseIndex + i + 1); }
            }
        }

        private static List<P> Clip(List<P> input, Func<P, double> distance, double epsilon)
        {
            var result = new List<P>(); if (input.Count == 0) return result;
            var previous = input[input.Count - 1]; var pd = distance(previous); var pin = pd >= -epsilon;
            foreach (var current in input)
            {
                var cd = distance(current); var cin = cd >= -epsilon;
                if (cin != pin)
                {
                    var t = pd / (pd - cd);
                    result.Add(new P { World = previous.World + (current.World - previous.World) * t, Local = previous.Local + (current.Local - previous.Local) * t });
                }
                if (cin) result.Add(current);
                previous = current; pd = cd; pin = cin;
            }
            return result;
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
        private static float[] F(V v) => new[] { (float)v.X, (float)v.Y, (float)v.Z };
        private static double Dot(V a, V b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static V Cross(V a, V b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        private static V Normalize(V v) { var l = Math.Sqrt(Dot(v, v)); return l > 1e-12 ? v * (1 / l) : new V(0, 1, 0); }
    }

}
