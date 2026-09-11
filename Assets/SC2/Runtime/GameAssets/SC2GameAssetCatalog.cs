using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Builds a lightweight, serializable-by-caller index of SC2 unit/prop blueprints.
/// Discovery is deliberately separate from rendering and editor UI.
/// </summary>
public static class SC2GameAssetCatalog
{
    private static readonly HashSet<string> MaterialTextureFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "DiffuseName", "NormalName", "MaskName", "TeamName"
    };

    public static SC2GameAssetCatalogResult Build(IEnumerable<string> blueprintRoots, IEnumerable<string> modelRoots)
    {
        if (blueprintRoots == null) throw new ArgumentNullException(nameof(blueprintRoots));
        var result = new SC2GameAssetCatalogResult();
        var models = IndexModels(modelRoots ?? Enumerable.Empty<string>(), result.Warnings);
        foreach (string root in blueprintRoots.Where(Directory.Exists))
        {
            foreach (string path in Directory.EnumerateFiles(root, "*.bp", SearchOption.AllDirectories))
            {
                if (!IsBrowsableBlueprint(path)) continue;
                try
                {
                    var blueprint = SC2BlueprintLoader.ParseFile(path);
                    result.Assets.Add(CreateAsset(blueprint, path, models));
                }
                catch (Exception exception)
                {
                    result.Warnings.Add("Could not parse blueprint '" + path + "': " + exception.Message);
                }
            }
        }
        result.Assets.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static bool IsBrowsableBlueprint(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (normalized.IndexOf("/uncompiled/", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        string file = Path.GetFileNameWithoutExtension(path);
        return file.EndsWith("_unit", StringComparison.OrdinalIgnoreCase) || file.EndsWith("_prop", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, List<string>> IndexModels(IEnumerable<string> roots, List<string> warnings)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(root, "*.scm", SearchOption.AllDirectories))
                {
                    string key = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path)); // Foo_lod0.win.scm -> Foo_lod0
                    List<string> entries;
                    if (!result.TryGetValue(key, out entries)) result[key] = entries = new List<string>();
                    if (!entries.Contains(path, StringComparer.OrdinalIgnoreCase)) entries.Add(path);
                }
            }
            catch (Exception exception) { warnings.Add("Could not index SCM root '" + root + "': " + exception.Message); }
        }
        return result;
    }

    private static SC2GameAsset CreateAsset(SC2Blueprint blueprint, string sourcePath, Dictionary<string, List<string>> models)
    {
        string fileStem = Path.GetFileNameWithoutExtension(sourcePath);
        string id = RemoveKnownSuffix(fileStem);
        var display = blueprint.Table.GetTable("Display");
        var mesh = display == null ? null : display.GetTable("Mesh");
        var asset = new SC2GameAsset
        {
            Id = id,
            Kind = blueprint.Declaration ?? "Blueprint",
            SourcePath = sourcePath,
            DisplayName = StripLocTag(display == null ? null : display.GetString("DisplayName")),
            UniformScale = display == null ? 1f : display.GetSingle("UniformScale", 1f),
            Blueprint = blueprint
        };
        // Reported directly: unit browser needs faction/category filtering
        // (Cybran/Illuminate/UEF/Scenario, Structure vs Mobile, Experimental,
        // Land/Air/Naval/Amphibious, ...). Real blueprints carry this as a
        // flat top-level `Categories = {'CYBRAN', 'MOBILE', 'LAND', ...}`
        // array of plain strings (confirmed by the user's own real .bp
        // excerpt) — extracted here once, alongside every other blueprint
        // field this class already reads, rather than re-parsing on every
        // browser filter change.
        var categories = blueprint.Table.GetTable("Categories");
        if (categories != null)
            foreach (var item in categories.Items)
                if (item.Kind == SC2BlueprintValueKind.String && !string.IsNullOrEmpty(item.StringValue))
                    asset.Categories.Add(item.StringValue);
        // Reported directly, real example: `Footprint = { SizeX = 14,
        // SizeZ = 14 }` — how many terrain cells a building (or any prop)
        // actually occupies, needed to draw the real occupied-area overlay
        // at placement time instead of just a point marker. Top-level like
        // Categories/Display, not nested — 0 (the default) means "no
        // Footprint table" and callers treat that as "nothing to draw".
        var footprint = blueprint.Table.GetTable("Footprint");
        if (footprint != null)
        {
            asset.FootprintSizeX = footprint.GetSingle("SizeX");
            asset.FootprintSizeZ = footprint.GetSingle("SizeZ");
        }
        var lods = mesh == null ? null : mesh.GetTable("LODs");
        if (lods != null)
        {
            for (int index = 0; index < lods.Items.Count; index++)
            {
                var entry = lods.Items[index];
                if (entry.Kind != SC2BlueprintValueKind.Table) continue;
                var lodTable = entry.TableValue;
                string declaredMeshPath = lodTable.GetString("MeshName");
                var lod = new SC2GameAssetLod
                {
                    Index = index,
                    Cutoff = lodTable.GetSingle("LODCutoff"),
                    DeclaredMeshPath = declaredMeshPath,
                    ModelPaths = FindModels(models, declaredMeshPath, id, index, sourcePath)
                };
                ExtractMaterialSets(lodTable.GetTable("MaterialSets"), lod.MaterialSets);
                asset.Lods.Add(lod);
            }
        }

        // SpeedTree-style props and a few other shipped blueprints expose an
        // SRTPath but omit Display.Mesh entirely. The archive still contains
        // a conventional Foo_lod0.win.scm used by map/object previews. Treat
        // that real SCM as an implicit LOD instead of reporting a ready asset
        // that can only render a placeholder card.
        if (asset.Lods.Count == 0)
        {
            List<string> implicitModels = FindModels(models, null, id, 0, sourcePath);
            if (implicitModels.Count != 0)
            {
                asset.Lods.Add(new SC2GameAssetLod
                {
                    Index = 0,
                    Cutoff = 9001f,
                    ModelPaths = implicitModels
                });
            }
        }
        return asset;
    }

    private static List<string> FindModels(Dictionary<string, List<string>> models, string declaredMeshPath, string assetId, int lodIndex, string blueprintPath)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, GetModelKey(declaredMeshPath));
        AddCandidate(candidates, assetId + "_lod" + lodIndex);

        // Vanilla wreckage BPs occasionally contain stale MeshName values or
        // request an LOD that was never shipped. The model still follows the
        // owning unit's stem (Foo_wreckage02_prop.bp -> Foo_lod0.win.scm).
        string baseAssetId = Regex.Replace(assetId ?? string.Empty, "_wreckage[0-9]+$", string.Empty, RegexOptions.IgnoreCase);
        AddCandidate(candidates, baseAssetId + "_lod" + lodIndex);
        AddCandidate(candidates, baseAssetId + "_lod0");

        string blueprintDirectoryName = Path.GetFileName(Path.GetDirectoryName(blueprintPath) ?? string.Empty);
        AddCandidate(candidates, blueprintDirectoryName + "_lod" + lodIndex);
        AddCandidate(candidates, blueprintDirectoryName + "_lod0");

        foreach (string candidate in candidates)
        {
            if (!models.TryGetValue(candidate, out List<string> paths)) continue;
            return RankModelPaths(paths, blueprintPath);
        }
        return new List<string>();
    }

    private static void AddCandidate(List<string> candidates, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || candidates.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        candidates.Add(value);
    }

    private static List<string> RankModelPaths(IEnumerable<string> paths, string blueprintPath)
    {
        string blueprintDirectory = Path.GetDirectoryName(blueprintPath) ?? string.Empty;
        return paths
            .OrderByDescending(path => string.Equals(Path.GetDirectoryName(path), blueprintDirectory, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(IsGameScm)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetModelKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string file = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar));
        return Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));
    }

    private static bool IsGameScm(string path)
    {
        return path.EndsWith(".win.scm", StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractMaterialSets(SC2BlueprintTable source, List<SC2GameAssetMaterialSet> output)
    {
        if (source == null) return;
        foreach (SC2BlueprintValue setValue in source.Items)
        {
            if (setValue.Kind != SC2BlueprintValueKind.Table) continue;
            var setTable = setValue.TableValue;
            var target = new SC2GameAssetMaterialSet { Name = setTable.GetString("Name") ?? "Default" };
            var materials = setTable.GetTable("Materials");
            if (materials != null)
            {
                foreach (SC2BlueprintValue materialValue in materials.Items)
                {
                    if (materialValue.Kind != SC2BlueprintValueKind.Table) continue;
                    var material = new SC2GameAssetMaterial();
                    foreach (var field in materialValue.TableValue.Fields)
                    {
                        string value = field.Value.AsString();
                        if (MaterialTextureFields.Contains(field.Key) && value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) material.Textures[field.Key] = value;
                        else if (string.Equals(field.Key, "ShaderMacros", StringComparison.OrdinalIgnoreCase)) material.ShaderMacros = value;
                    }
                    target.Materials.Add(material);
                }
            }
            output.Add(target);
        }
    }

    /// <summary>Reported directly, real example: `Display.DisplayName` on
    /// real blueprints is often a raw, unresolved localization reference —
    /// `&lt;LOC UNIT_NAME_0070&gt;Cybranasaurus Rex` — where the real game
    /// engine substitutes the `&lt;LOC key&gt;` prefix via its loc table at
    /// runtime and this editor has no such table to resolve against. Rather
    /// than showing the raw tag (or an unresolved blank), strip just the
    /// `&lt;LOC ...&gt;` prefix and keep whatever fallback text already
    /// follows it in the same string — matches every real example seen
    /// (the fallback text is always present inline, not a separate lookup).
    /// Applied once here so every consumer of SC2GameAsset.DisplayName
    /// (browser search/sort/card label, and the Name assigned to a newly
    /// placed prop/unit) gets the clean text automatically.</summary>
    private static readonly Regex LocTagPrefix = new Regex(@"^\s*<LOC\s+[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string StripLocTag(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var stripped = LocTagPrefix.Replace(value, string.Empty);
        return stripped.Length == 0 ? value : stripped;
    }

    private static string RemoveKnownSuffix(string name)
    {
        foreach (string suffix in new[] { "_unit", "_prop" }) if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - suffix.Length);
        return name;
    }
}

public sealed class SC2GameAssetCatalogResult
{
    public readonly List<SC2GameAsset> Assets = new List<SC2GameAsset>();
    public readonly List<string> Warnings = new List<string>();
}
public sealed class SC2GameAsset
{
    public string Id; public string Kind; public string SourcePath; public string DisplayName; public float UniformScale; public SC2Blueprint Blueprint;
    public readonly List<SC2GameAssetLod> Lods = new List<SC2GameAssetLod>();
    /// <summary>Raw Categories tags from the blueprint's top-level
    /// `Categories = {'CYBRAN', 'MOBILE', 'LAND', ...}` array — faction
    /// (UEF/CYBRAN/ILLUMINATE/...), STRUCTURE vs MOBILE, EXPERIMENTAL,
    /// movement type (LAND/AIR/NAVAL/AMPHIBIOUS), etc. all live in here as
    /// plain uppercase strings, exactly as authored. Empty for blueprints
    /// with no Categories table (e.g. most props).</summary>
    public readonly HashSet<string> Categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Blueprint's `Footprint = { SizeX, SizeZ }` table, in
    /// terrain cells — 0 means no Footprint table was present (most props;
    /// non-structure units).</summary>
    public float FootprintSizeX;
    public float FootprintSizeZ;
}
public sealed class SC2GameAssetLod
{
    public int Index; public float Cutoff; public string DeclaredMeshPath; public List<string> ModelPaths = new List<string>(); public readonly List<SC2GameAssetMaterialSet> MaterialSets = new List<SC2GameAssetMaterialSet>();
}
public sealed class SC2GameAssetMaterialSet { public string Name; public readonly List<SC2GameAssetMaterial> Materials = new List<SC2GameAssetMaterial>(); }
public sealed class SC2GameAssetMaterial
{
    public readonly Dictionary<string, string> Textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> ResolvedTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string Name;
    public string ShaderMacros;
}
