using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SC2.MapEditor.Document;
using UnityEngine;

/// <summary>
/// A running preview of the Effect markers in the viewport: for every marker of type Effect the particle emitters of its template
/// (EffectTemplates.Marker, see SC2EffectTemplateCatalog) are simulated (SC2ParticleEmitter) and drawn as camera-facing or flat
/// textured quads, the way the game's own particle shader combines the particle texture with the emitter's ramp texture.
///
/// Approximate: it shows what the effect looks like and how big it is, not frame-exact motion (see SC2ParticleEmitter for the
/// assumptions). The emitter blueprints and their textures are read from the game archives on first use and kept.
///
/// Sync(...) follows the marker list (called with the marker preview's own dirty-gated Sync); DrawAll(...) is called once per
/// rendered frame from the game view's LateUpdate: it advances every emitter by the frame's time and issues one draw call per
/// (texture, ramp, blend) group.
/// </summary>
public static class SC2EffectPreview
{
    private const float MaxStepSeconds = 0.1f;
    private const int MaxTotalParticles = 20000;
    private const float PrewarmCapTicks = 250f;
    private static readonly string[] BlueprintRoots = { "", "mods/DLC1/shadow/", "uncompiled/", "uncompiled/mods/DLC1/shadow/" };

    private static bool _enabled = true;
    /// <summary>Shows the effects of Effect markers in the viewport. Turning it back on rebuilds them on the next frame.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; _resync = true; if (!value) ClearAll(); }
    }

    private sealed class Runner
    {
        public SC2ParticleEmitter Sim;
        public string Key;
        public Texture Texture, Ramp;
        public int BlendMode;
    }

    private sealed class Instance
    {
        public string Template;
        public int Signature;
        public Vector3 Origin;
        public readonly List<Runner> Runners = new List<Runner>();
    }

    private sealed class Batch
    {
        public Material Material;
        public Mesh Mesh;
        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector2> Uv0 = new List<Vector2>();
        public readonly List<Vector2> Uv1 = new List<Vector2>();
        public readonly List<int> Triangles = new List<int>();
        public readonly List<(float Distance, SC2ParticleEmitter.Particle Particle, SC2EmitterBlueprint Blueprint)> Sorted =
            new List<(float, SC2ParticleEmitter.Particle, SC2EmitterBlueprint)>();
        public int Quads;
    }

    private static readonly Dictionary<int, Instance> Instances = new Dictionary<int, Instance>();
    private static readonly Dictionary<string, SC2EmitterBlueprint> Blueprints = new Dictionary<string, SC2EmitterBlueprint>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture> Textures = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Batch> Batches = new Dictionary<string, Batch>();
    private static readonly List<SC2ParticleEmitter.Particle> _scratch = new List<SC2ParticleEmitter.Particle>();

    private static ISC2SourceContainerReader _entriesFor;
    private static Dictionary<string, string> _entryByLowerPath;
    private static Shader _shader;

    private static SC2TerrainData _lastData;
    private static HashSet<int> _lastHidden;
    private static bool _lastVisible;
    private static float? _lastWaterLevel;
    private static bool _resync;

    /// <summary>Why no effect shows, or null.</summary>
    public static string Problem { get; private set; }
    public static int ParticleCount { get; private set; }

    /// <summary>Follows the marker list: effects are created for markers of type Effect that name a template, kept running while
    /// their template, place, rotation, scale and offset stay the same, and dropped with their marker.</summary>
    public static void Sync(SC2TerrainData data, HashSet<int> hiddenIds, bool visible, float? waterLevel = null)
    {
        _lastData = data; _lastHidden = hiddenIds; _lastVisible = visible; _lastWaterLevel = waterLevel;
        _resync = false;
        if (data == null || !visible || !_enabled) { ClearAll(); return; }

        var alive = new HashSet<int>();
        IReadOnlyList<SC2MarkerEffectTemplate> templates = null;
        foreach (var marker in data.ObjMarkers)
        {
            if (!string.Equals(marker.Type, "Effect", StringComparison.OrdinalIgnoreCase)) continue;
            if (hiddenIds != null && hiddenIds.Contains(marker.Id)) continue;
            var templateName = SC2EffectMarkerPicker.GetTemplate(marker);
            if (templateName.Length == 0) continue;

            var scale = SC2EffectMarkerPicker.GetScale(marker);
            var offset = SC2EffectMarkerPicker.GetOffset(marker);
            var signature = Signature(templateName, marker, scale, offset, waterLevel);
            alive.Add(marker.Id);
            if (Instances.TryGetValue(marker.Id, out var existing))
            {
                if (existing.Signature == signature) continue;
                if (string.Equals(existing.Template, templateName, StringComparison.Ordinal))
                {
                    // Same effect, moved / turned / scaled (a drag, or the R / S gesture): the running particles follow at once.
                    var orientation = marker.OrientationIsQuaternion ? Quaternion.Euler(marker.OrientationEuler) : Quaternion.identity;
                    foreach (var runner in existing.Runners) runner.Sim.SetTransform(marker.Position, orientation, scale, offset, waterLevel);
                    existing.Origin = marker.Position;
                    existing.Signature = signature;
                    continue;
                }
            }

            Instances[marker.Id] = Rebuild(marker, templateName, waterLevel, ref templates);
        }
        foreach (var id in Instances.Keys.Where(id => !alive.Contains(id)).ToList()) Instances.Remove(id);
    }

    private static Instance Rebuild(SC2ObjMarker marker, string templateName, float? waterLevel, ref IReadOnlyList<SC2MarkerEffectTemplate> templates)
    {
        var scale = SC2EffectMarkerPicker.GetScale(marker);
        var offset = SC2EffectMarkerPicker.GetOffset(marker);
        templates ??= SC2EffectTemplateCatalog.Templates;
        var template = templates.FirstOrDefault(t => string.Equals(t.Name, templateName, StringComparison.Ordinal));
        var instance = Build(marker, template, Signature(templateName, marker, scale, offset, waterLevel), scale, offset, waterLevel);
        instance.Template = templateName;
        return instance;
    }

    private static int Signature(string template, SC2ObjMarker marker, float scale, Vector3 offset, float? waterLevel)
    {
        var hash = 17;
        hash = hash * 31 + template.GetHashCode();
        hash = hash * 31 + marker.Position.GetHashCode();
        hash = hash * 31 + marker.OrientationEuler.GetHashCode();
        hash = hash * 31 + marker.OrientationIsQuaternion.GetHashCode();
        hash = hash * 31 + scale.GetHashCode();
        hash = hash * 31 + offset.GetHashCode();
        hash = hash * 31 + (waterLevel ?? float.NaN).GetHashCode();
        return hash;
    }

    private static Instance Build(SC2ObjMarker marker, SC2MarkerEffectTemplate template, int signature, float scale, Vector3 offset, float? waterLevel)
    {
        var instance = new Instance { Signature = signature, Origin = marker.Position };
        if (template == null) return instance;
        var orientation = marker.OrientationIsQuaternion ? Quaternion.Euler(marker.OrientationEuler) : Quaternion.identity;

        for (var i = 0; i < template.Emitters.Count; i++)
        {
            var blueprint = LoadBlueprint(template.Emitters[i]);
            if (blueprint == null) continue;
            var texture = LoadTexture(blueprint.Texture);
            if (texture == null) continue;
            var ramp = LoadTexture(blueprint.RampTexture);
            var sim = new SC2ParticleEmitter(blueprint, marker.Position, orientation, scale, offset, marker.Id * 7919 + i, waterLevel);
            // The game's emitters have been running since the map loaded: skip the empty start of a slow smoke.
            sim.Prewarm(PrewarmCapTicks);
            instance.Runners.Add(new Runner
            {
                Sim = sim,
                Texture = texture,
                Ramp = ramp,
                BlendMode = blueprint.BlendMode,
                Key = blueprint.Texture + "|" + blueprint.RampTexture + "|" + (blueprint.BlendMode == 3 ? "add" : "alpha"),
            });
        }
        return instance;
    }

    public static void ClearAll()
    {
        Instances.Clear();
        foreach (var batch in Batches.Values) batch.Quads = 0;
        ParticleCount = 0;
    }

    // ── Archive access ───────────────────────────────────────────────────────

    private static SC2EmitterBlueprint LoadBlueprint(string blueprintPath)
    {
        if (string.IsNullOrEmpty(blueprintPath)) return null;
        if (Blueprints.TryGetValue(blueprintPath, out var cached)) return cached;
        SC2EmitterBlueprint blueprint = null;
        try
        {
            if (SC2GameArchiveRegistry.TryGetReader(out var reader))
            {
                EnsureEntries(reader);
                var relative = blueprintPath.TrimStart('/');
                foreach (var root in BlueprintRoots)
                {
                    if (!_entryByLowerPath.TryGetValue((root + relative).ToLowerInvariant(), out var entry)) continue;
                    using var stream = reader.OpenEntry(entry);
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    blueprint = SC2EmitterBlueprintReader.Read(memory.ToArray(), Path.GetFileNameWithoutExtension(blueprintPath));
                    break;
                }
                if (blueprint == null) Problem = "Emitter " + blueprintPath + " was not found in the game archives.";
            }
            else Problem = "The game archives are not available, so effects cannot be shown.";
        }
        catch (Exception exception)
        {
            Problem = "Emitter " + blueprintPath + ": " + exception.Message;
        }
        Blueprints[blueprintPath] = blueprint;
        return blueprint;
    }

    private static void EnsureEntries(ISC2SourceContainerReader reader)
    {
        if (ReferenceEquals(reader, _entriesFor) && _entryByLowerPath != null) return;
        _entriesFor = reader;
        _entryByLowerPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in reader.Entries) _entryByLowerPath[entry.NormalizedPath.ToLowerInvariant()] = entry.NormalizedPath;
        Blueprints.Clear();
        Textures.Clear();
    }

    private static Texture LoadTexture(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Textures.TryGetValue(path, out var cached)) return cached;
        Texture texture = null;
        try { texture = SC2TerrainMaterialFactory.LoadTexturePublic(path, true); }
        catch (Exception exception) { Problem = "Texture " + path + ": " + exception.Message; }
        Textures[path] = texture;
        return texture;
    }

    // ── Per-frame run and draw ───────────────────────────────────────────────

    /// <summary>Advances every effect by the time since the last frame and draws it. Call once per rendered frame.
    /// <paramref name="ticksOverride"/> advances by that many game ticks instead (for tests that render without a frame loop).</summary>
    public static void DrawAll(Camera camera, float? ticksOverride = null)
    {
        if (_resync) Sync(_lastData, _lastHidden, _lastVisible, _lastWaterLevel);
        if (!_enabled || Instances.Count == 0 || camera == null) { ParticleCount = 0; return; }
        if (!EnsureShader()) return;

        var ticks = ticksOverride ?? Mathf.Min(Time.unscaledDeltaTime, MaxStepSeconds) * SC2ParticleEmitter.TicksPerSecond;
        var cameraPosition = camera.transform.position;
        var cameraRight = camera.transform.right;
        var cameraUp = camera.transform.up;
        var viewDirection = camera.transform.forward;

        foreach (var batch in Batches.Values) ResetBatch(batch);
        var total = 0;
        foreach (var instance in Instances.Values)
        {
            foreach (var runner in instance.Runners)
            {
                var blueprint = runner.Sim.Blueprint;
                var distance = Vector3.Distance(cameraPosition, instance.Origin);
                if (blueprint.LodCutoff > 0f && distance > blueprint.LodCutoff) continue;
                runner.Sim.Advance(ticks);
                if (total >= MaxTotalParticles) continue;

                runner.Sim.GetParticles(_scratch);
                total += _scratch.Count;
                var batch = BatchFor(runner);
                foreach (var particle in _scratch)
                    batch.Sorted.Add((Vector3.SqrMagnitude(particle.Position - cameraPosition), particle, blueprint));
            }
        }
        ParticleCount = total;

        foreach (var batch in Batches.Values)
        {
            if (batch.Sorted.Count == 0) continue;
            // Alpha-blended particles are drawn farthest first; added light does not depend on the order.
            if (batch.Material.GetFloat("_DstBlend") != 1f) batch.Sorted.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            foreach (var (_, particle, blueprint) in batch.Sorted) AddQuad(batch, particle, blueprint, cameraRight, cameraUp, viewDirection);
            Flush(batch);
            Graphics.DrawMesh(batch.Mesh, Matrix4x4.identity, batch.Material, 0, null, 0, null, false, false);
        }
    }

    private static bool EnsureShader()
    {
        if (_shader != null) return true;
        _shader = Resources.Load<Shader>("SC2EffectParticle") ?? Shader.Find("SC2/EffectParticle");
        if (_shader == null) Problem = "The effect particle shader is missing.";
        return _shader != null;
    }

    private static Batch BatchFor(Runner runner)
    {
        if (Batches.TryGetValue(runner.Key, out var batch) && batch.Material != null) return batch;
        batch = batch ?? new Batch();
        var material = new Material(_shader) { hideFlags = HideFlags.HideAndDontSave, name = "SC2EffectParticle " + runner.Key };
        material.SetTexture("_MainTex", runner.Texture);
        material.SetTexture("_RampTex", runner.Ramp != null ? runner.Ramp : Texture2D.whiteTexture);
        var additive = runner.BlendMode == 3;
        material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetFloat("_DstBlend", (float)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
        batch.Material = material;
        batch.Mesh = new Mesh { name = "SC2EffectParticles", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
        batch.Mesh.MarkDynamic();
        Batches[runner.Key] = batch;
        return batch;
    }

    private static void ResetBatch(Batch batch)
    {
        batch.Vertices.Clear(); batch.Uv0.Clear(); batch.Uv1.Clear(); batch.Triangles.Clear(); batch.Sorted.Clear();
        batch.Quads = 0;
    }

    private static void AddQuad(Batch batch, SC2ParticleEmitter.Particle p, SC2EmitterBlueprint blueprint,
        Vector3 cameraRight, Vector3 cameraUp, Vector3 viewDirection)
    {
        Vector3 right, up;
        if (blueprint.AlignRotation && p.Direction.sqrMagnitude > 0.5f)
        {
            // Stretched along the way it travels, facing the camera.
            up = p.Direction;
            right = Vector3.Cross(viewDirection, p.Direction);
            right = right.sqrMagnitude > 1e-8f ? right.normalized : cameraRight;
        }
        else
        {
            var c = Mathf.Cos(p.Rotation); var s = Mathf.Sin(p.Rotation);
            if (blueprint.Flat)
            {
                right = new Vector3(c, 0f, s);
                up = new Vector3(-s, 0f, c);
            }
            else
            {
                right = cameraRight * c + cameraUp * s;
                up = cameraRight * -s + cameraUp * c;
            }
        }

        var half = p.Size * 0.5f;
        var strips = Mathf.Max(1, blueprint.TextureStripCount);
        var v0 = p.Strip / (float)strips; var v1 = (p.Strip + 1) / (float)strips;
        var ramp = new Vector2(p.Life01, p.RampRow);
        var baseIndex = batch.Vertices.Count;
        batch.Vertices.Add(p.Position + (-right - up) * half); batch.Uv0.Add(new Vector2(0f, v0)); batch.Uv1.Add(ramp);
        batch.Vertices.Add(p.Position + (right - up) * half);  batch.Uv0.Add(new Vector2(1f, v0)); batch.Uv1.Add(ramp);
        batch.Vertices.Add(p.Position + (right + up) * half);  batch.Uv0.Add(new Vector2(1f, v1)); batch.Uv1.Add(ramp);
        batch.Vertices.Add(p.Position + (-right + up) * half); batch.Uv0.Add(new Vector2(0f, v1)); batch.Uv1.Add(ramp);
        batch.Triangles.Add(baseIndex); batch.Triangles.Add(baseIndex + 2); batch.Triangles.Add(baseIndex + 1);
        batch.Triangles.Add(baseIndex); batch.Triangles.Add(baseIndex + 3); batch.Triangles.Add(baseIndex + 2);
        batch.Quads++;
    }

    private static void Flush(Batch batch)
    {
        var mesh = batch.Mesh;
        mesh.Clear();
        mesh.SetVertices(batch.Vertices);
        mesh.SetUVs(0, batch.Uv0);
        mesh.SetUVs(1, batch.Uv1);
        mesh.SetTriangles(batch.Triangles, 0, false);
        mesh.bounds = new Bounds(Vector3.zero, new Vector3(1e6f, 1e6f, 1e6f));
    }
}
