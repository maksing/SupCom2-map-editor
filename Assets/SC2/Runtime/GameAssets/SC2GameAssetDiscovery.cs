using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Browser-facing discovery pass. A unit/prop BP is the entry point; each
/// declared LOD resolves to an SCM and each material resolves its explicit
/// texture paths. Standard team-color materials without explicit texture names
/// fall back to the conventional sibling SCM texture names.
/// </summary>
public static class SC2GameAssetDiscovery
{
    private static readonly string[] StandardTextureFields = { "DiffuseName", "NormalName", "MaskName", "TeamName" };

    public static SC2GameAssetDiscoveryResult Build(IEnumerable<string> blueprintRoots, IEnumerable<string> gameDataRoots)
    {
        if (blueprintRoots == null) throw new ArgumentNullException(nameof(blueprintRoots));
        if (gameDataRoots == null) throw new ArgumentNullException(nameof(gameDataRoots));

        var roots = gameDataRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var catalog = SC2GameAssetCatalog.Build(blueprintRoots, roots);
        var resolver = new SC2GameAssetFileResolver(roots);
        var result = new SC2GameAssetDiscoveryResult(catalog);
        result.Warnings.AddRange(catalog.Warnings);

        foreach (var asset in catalog.Assets)
        {
            var entry = new SC2GameAssetDiscoveryEntry(asset);
            if (asset.Lods.Count == 0)
            {
                entry.Issues.Add(new SC2GameAssetResolutionIssue("MissingScm", "No visual SCM was found for this blueprint.", null));
                result.Entries.Add(entry);
                continue;
            }
            foreach (var lod in asset.Lods)
            {
                if (lod.ModelPaths.Count == 0)
                {
                    entry.Issues.Add(new SC2GameAssetResolutionIssue("MissingScm", "No SCM was found for LOD " + lod.Index + ".", null));
                    continue;
                }

                foreach (var modelPath in lod.ModelPaths) entry.Models.Add(modelPath);
                string fallbackModelPath = lod.ModelPaths[0];
                EnsureMaterialSet(lod, fallbackModelPath, entry);
                foreach (var set in lod.MaterialSets)
                foreach (var material in set.Materials)
                    ResolveMaterialTextures(material, fallbackModelPath, resolver, entry);
            }
            result.Entries.Add(entry);
        }
        return result;
    }

    private static void ResolveMaterialTextures(SC2GameAssetMaterial material, string fallbackModelPath, SC2GameAssetFileResolver resolver, SC2GameAssetDiscoveryEntry entry)
    {
        material.ResolvedTextures.Clear();
        bool useStandardFallbacks = UsesConventionalGameTextures(material);
        bool usesTeamColor = UsesTeamColor(material);
        foreach (string field in StandardTextureFields)
        {
            material.Textures.TryGetValue(field, out string requestedPath);
            string resolved = resolver.ResolveBlueprintPath(requestedPath);
            bool usedSiblingFallback = false;

            // An explicit path may be stale even though the conventional
            // sibling texture exists. Resolve fallback independently for each
            // slot; one explicit Diffuse must not suppress Normal/Mask/Team.
            if (resolved == null && (!string.IsNullOrWhiteSpace(requestedPath) || useStandardFallbacks))
            {
                string slot = field.Substring(0, field.Length - "Name".Length);
                resolved = resolver.ResolveDeclaredTextureSlot(requestedPath, slot);
                if (resolved != null) material.ResolvedTextures[field] = resolved;
                if (resolved != null)
                {
                    entry.Textures.Add(new SC2GameAssetTextureResolution(field, requestedPath, resolved, true));
                    continue;
                }
                resolved = resolver.ResolveSiblingTexture(fallbackModelPath, slot);
                usedSiblingFallback = resolved != null;
            }

            if (string.IsNullOrWhiteSpace(requestedPath) && resolved == null &&
                !(usesTeamColor && string.Equals(field, "TeamName", StringComparison.OrdinalIgnoreCase))) continue;
            entry.Textures.Add(new SC2GameAssetTextureResolution(field, requestedPath, resolved, usedSiblingFallback));
            if (resolved != null)
            {
                material.ResolvedTextures[field] = resolved;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(requestedPath))
                entry.Issues.Add(new SC2GameAssetResolutionIssue("MissingTexture", field + " could not be resolved: " + requestedPath, requestedPath, false));
            // An undeclared conventional slot is optional. Several vanilla
            // team_color materials intentionally omit Team (and some scenario
            // meshes omit all four slots), letting the game use shader
            // defaults. Keep the unresolved probe for diagnostics, but do not
            // make an otherwise usable model unavailable in the browser.
        }
    }

    private static void EnsureMaterialSet(SC2GameAssetLod lod, string modelPath, SC2GameAssetDiscoveryEntry entry)
    {
        if (lod.MaterialSets.Any(set => set.Materials.Count != 0)) return;

        var set = new SC2GameAssetMaterialSet { Name = "SCM" };
        try
        {
            SC2ScmModel model;
            using (var stream = File.OpenRead(modelPath)) model = SC2ScmModelReader.Read(stream, modelPath);
            if (model.Materials.Count != 0)
            {
                foreach (SC2ScmMaterialRange range in model.Materials)
                    set.Materials.Add(CreateImplicitMaterial(range.Name));
            }
            else
            {
                set.Materials.Add(CreateImplicitMaterial("Diffuse"));
            }
        }
        catch (Exception exception)
        {
            entry.Issues.Add(new SC2GameAssetResolutionIssue("ScmMaterialRead", "SCM material ranges could not be read: " + exception.Message, modelPath, false));
            set.Materials.Add(CreateImplicitMaterial("Diffuse"));
        }
        lod.MaterialSets.Add(set);
    }

    private static SC2GameAssetMaterial CreateImplicitMaterial(string name)
    {
        return new SC2GameAssetMaterial
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Diffuse" : name,
            ShaderMacros = "ambient_lighting, diffuse_lighting, specular_lighting, ambient_occlusion, alpha_test = 128"
        };
    }

    private static bool UsesConventionalGameTextures(SC2GameAssetMaterial material)
    {
        return !string.IsNullOrWhiteSpace(material.ShaderMacros) &&
               (material.ShaderMacros.IndexOf("diffuse_lighting", StringComparison.OrdinalIgnoreCase) >= 0 ||
                material.ShaderMacros.IndexOf("team_color", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool UsesTeamColor(SC2GameAssetMaterial material)
    {
        return !string.IsNullOrWhiteSpace(material.ShaderMacros) &&
               material.ShaderMacros.IndexOf("team_color", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

/// <summary>Case-insensitive lookup for BP texture paths inside game-data roots.</summary>
public sealed class SC2GameAssetFileResolver
{
    private readonly string[] _roots;
    private readonly Dictionary<string, string> _texturesByRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _uniqueTexturesByFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _siblingTextureCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public SC2GameAssetFileResolver(IEnumerable<string> roots)
    {
        _roots = roots == null ? new string[0] : roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string root in _roots) IndexTextures(root);
    }

    public string ResolveBlueprintPath(string blueprintPath)
    {
        if (string.IsNullOrWhiteSpace(blueprintPath)) return null;
        string key = NormalizeRelativePath(blueprintPath);
        string path;
        if (_texturesByRelativePath.TryGetValue(key, out path)) return path;

        foreach (string variant in GetGameTexturePathVariants(key))
            if (_texturesByRelativePath.TryGetValue(variant, out path)) return path;

        // A root may be Units/ or props/ rather than the overall gamedata root.
        // Fall back to suffix matching only when the full BP path was not indexed.
        foreach (string candidate in GetGameTexturePathVariants(key).Append(key))
        {
            string suffix = "/" + candidate;
            foreach (var pair in _texturesByRelativePath)
                if (pair.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }

        // A few shipped BPs contain stale directories while the DDS basename
        // is still correct (for example MP_204 wall masks and DLC tree aliases).
        // A basename fallback is safe only when that filename is unique across
        // all indexed roots; collisions deliberately remain unresolved.
        foreach (string candidate in GetGameTexturePathVariants(key).Append(key))
        {
            string fileName = Path.GetFileName(candidate);
            if (_uniqueTexturesByFileName.TryGetValue(fileName, out path) && path != null) return path;
        }
        return null;
    }

    public string ResolveSiblingTexture(string scmPath, string slot)
    {
        if (string.IsNullOrWhiteSpace(scmPath) || string.IsNullOrWhiteSpace(slot)) return null;
        string cacheKey = scmPath + "|" + slot;
        if (_siblingTextureCache.TryGetValue(cacheKey, out string cached)) return cached;
        string directory = Path.GetDirectoryName(scmPath);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        string scmFileStem = Path.GetFileNameWithoutExtension(scmPath); // Foo_lod0.win
        string lodStem = Regex.Replace(scmFileStem, "\\.win$", string.Empty, RegexOptions.IgnoreCase); // Foo_lod0
        string baseStem = Regex.Replace(lodStem, "_lod[0-9]+$", string.Empty, RegexOptions.IgnoreCase); // Foo
        foreach (string prefix in new[] { baseStem, lodStem }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(directory, prefix + "_" + slot + ".win.dds");
            if (File.Exists(candidate)) return _siblingTextureCache[cacheKey] = candidate;
        }

        // SpeedTree-derived props use compact atlas suffixes (Foo_B_Atlas_D
        // and Foo_B_Atlas_N) rather than the regular Diffuse/Normal names.
        // Restrict this recovery to files that start with the owning SCM stem,
        // then rank the exact stem first so unrelated atlases in the same
        // directory cannot be selected accidentally.
        string compactSuffix = GetCompactSlotSuffix(slot);
        if (compactSuffix != null)
        {
            string prefix = baseStem + "_";
            string compact = Directory.EnumerateFiles(directory, "*.dds", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))
                    .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(path => Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path))
                    .EndsWith(compactSuffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (compact != null) return _siblingTextureCache[cacheKey] = compact;
        }
        _siblingTextureCache[cacheKey] = null;
        return null;
    }

    private static string GetCompactSlotSuffix(string slot)
    {
        if (string.Equals(slot, "Diffuse", StringComparison.OrdinalIgnoreCase)) return "_D";
        if (string.Equals(slot, "Normal", StringComparison.OrdinalIgnoreCase)) return "_N";
        if (string.Equals(slot, "Mask", StringComparison.OrdinalIgnoreCase)) return "_S";
        return null;
    }

    public string ResolveDeclaredTextureSlot(string blueprintPath, string slot)
    {
        if (string.IsNullOrWhiteSpace(blueprintPath) || string.IsNullOrWhiteSpace(slot)) return null;
        string key = NormalizeRelativePath(blueprintPath);
        string extension = key.EndsWith(".win.dds", StringComparison.OrdinalIgnoreCase) ? ".win.dds" : ".dds";
        string withoutExtension = key.Substring(0, key.Length - extension.Length);
        if (withoutExtension.EndsWith("_" + slot, StringComparison.OrdinalIgnoreCase)) return null;
        return ResolveBlueprintPath(withoutExtension + "_" + slot + extension);
    }

    private void IndexTextures(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*.dds", SearchOption.AllDirectories))
        {
            string relative = path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string key = NormalizeRelativePath(relative);
            if (!_texturesByRelativePath.ContainsKey(key)) _texturesByRelativePath.Add(key, path);

            string fileName = Path.GetFileName(path);
            if (!_uniqueTexturesByFileName.TryGetValue(fileName, out string existing))
                _uniqueTexturesByFileName.Add(fileName, path);
            else if (!string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                _uniqueTexturesByFileName[fileName] = null;
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim().TrimStart('/');
    }

    private static IEnumerable<string> GetGameTexturePathVariants(string normalizedPath)
    {
        const string winSuffix = ".win.dds";
        const string ddsSuffix = ".dds";
        if (normalizedPath.EndsWith(winSuffix, StringComparison.OrdinalIgnoreCase))
        {
            yield return normalizedPath.Substring(0, normalizedPath.Length - winSuffix.Length) + ddsSuffix;
            yield break;
        }

        if (normalizedPath.EndsWith(ddsSuffix, StringComparison.OrdinalIgnoreCase))
            yield return normalizedPath.Substring(0, normalizedPath.Length - ddsSuffix.Length) + winSuffix;
    }
}

public sealed class SC2GameAssetDiscoveryResult
{
    public readonly SC2GameAssetCatalogResult Catalog;
    public readonly List<SC2GameAssetDiscoveryEntry> Entries = new List<SC2GameAssetDiscoveryEntry>();
    public readonly List<string> Warnings = new List<string>();
    public SC2GameAssetDiscoveryResult(SC2GameAssetCatalogResult catalog) { Catalog = catalog; }
    public int ReadyCount => Entries.Count(entry => entry.IsReady);
    public int IssueCount => Entries.Sum(entry => entry.Issues.Count);
}

public sealed class SC2GameAssetDiscoveryEntry
{
    public readonly SC2GameAsset Asset;
    public readonly List<string> Models = new List<string>();
    public readonly List<SC2GameAssetTextureResolution> Textures = new List<SC2GameAssetTextureResolution>();
    public readonly List<SC2GameAssetResolutionIssue> Issues = new List<SC2GameAssetResolutionIssue>();
    public bool IsReady => Issues.All(issue => !issue.IsBlocking);
    public SC2GameAssetDiscoveryEntry(SC2GameAsset asset) { Asset = asset; }
}

public sealed class SC2GameAssetTextureResolution
{
    public readonly string FieldName;
    public readonly string RequestedPath;
    public readonly string ResolvedPath;
    public readonly bool IsSiblingFallback;
    public SC2GameAssetTextureResolution(string fieldName, string requestedPath, string resolvedPath, bool isSiblingFallback)
    {
        FieldName = fieldName; RequestedPath = requestedPath; ResolvedPath = resolvedPath; IsSiblingFallback = isSiblingFallback;
    }
}

public sealed class SC2GameAssetResolutionIssue
{
    public readonly string Kind;
    public readonly string Message;
    public readonly string Path;
    public readonly bool IsBlocking;
    public SC2GameAssetResolutionIssue(string kind, string message, string path, bool isBlocking = true)
    {
        Kind = kind; Message = message; Path = path; IsBlocking = isBlocking;
    }
}
