using System;
using System.Collections.Generic;

namespace SC2.MapEditor.Textures
{
    public sealed class SC2TextureCatalogQueryEngine
    {
        private IReadOnlyList<SC2TextureCatalogEntry> _entries = Array.Empty<SC2TextureCatalogEntry>();
        private readonly Dictionary<string, int[]> _cache = new(StringComparer.Ordinal);
        private int _version;

        public void SetEntries(IReadOnlyList<SC2TextureCatalogEntry> entries, int version)
        {
            _entries = entries ?? Array.Empty<SC2TextureCatalogEntry>();
            if (_version == version && _cache.Count > 0) return;
            _version = version;
            _cache.Clear();
        }

        public IReadOnlyList<int> Execute(SC2TextureQuery query, ISet<string> favorites = null)
        {
            query ??= new SC2TextureQuery();
            var cacheable = !query.FavoritesOnly;
            var key = _version + ":" + query.CacheKey;
            if (cacheable && _cache.TryGetValue(key, out var cached)) return cached;

            var tokens = Tokenize(query.Search);
            var category = (query.Category ?? "").Trim();
            var result = new List<int>();
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry == null) continue;
                if (query.Source.HasValue && entry.Source != query.Source.Value) continue;
                if (query.Usage.HasValue && entry.Usage != query.Usage.Value) continue;
                if (category.Length > 0 && !string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase)) continue;
                if (query.FavoritesOnly && (favorites == null || !favorites.Contains(entry.Id))) continue;
                var haystack = entry.SearchKey ?? "";
                var matches = true;
                for (var t = 0; t < tokens.Length; t++)
                    if (haystack.IndexOf(tokens[t], StringComparison.Ordinal) < 0) { matches = false; break; }
                if (matches) result.Add(i);
            }

            var array = result.ToArray();
            if (cacheable) _cache[key] = array;
            return array;
        }

        private static string[] Tokenize(string search) =>
            (search ?? "").Trim().ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    }
}
