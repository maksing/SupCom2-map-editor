using System;
using System.Collections.Generic;
using System.Linq;

namespace SC2.MapEditor.Document
{
    /// <summary>The decal placements of one type: every placement that uses the same atlas entry (the decal shape picked in the
    /// catalog).</summary>
    public sealed class SC2DecalTypeGroup
    {
        public string EntryId = "";
        public string Name = "";
        public readonly List<string> PlacementIds = new();
    }

    /// <summary>Groups the placements of a decals document by type for the "By type" panel (select / hide / isolate a whole type).</summary>
    public static class SC2DecalGrouping
    {
        /// <summary>One group per atlas entry that has placements, named after the entry (a placement whose entry is gone is grouped as
        /// "(unknown)"), sorted by name.</summary>
        public static List<SC2DecalTypeGroup> ByType(SC2DecalsDocument document)
        {
            var names = new Dictionary<string, string>();
            foreach (var entry in document.Entries)
                if (entry?.Id != null) names[entry.Id] = string.IsNullOrEmpty(entry.Name) ? "Decal" : entry.Name;

            var groups = new Dictionary<string, SC2DecalTypeGroup>();
            foreach (var placement in document.Placements)
            {
                if (placement?.Id == null) continue;
                var entryId = placement.AtlasEntryId ?? "";
                if (!groups.TryGetValue(entryId, out var group))
                    groups[entryId] = group = new SC2DecalTypeGroup { EntryId = entryId, Name = names.TryGetValue(entryId, out var name) ? name : "(unknown)" };
                group.PlacementIds.Add(placement.Id);
            }
            return groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.EntryId, StringComparer.Ordinal).ToList();
        }

        /// <summary>The ids to hide to show only <paramref name="keep"/>: every placement of the document that is not in it.</summary>
        public static HashSet<string> IdsExcept(SC2DecalsDocument document, IReadOnlyCollection<string> keep)
        {
            var keepSet = new HashSet<string>(keep);
            var hidden = new HashSet<string>();
            foreach (var placement in document.Placements)
                if (placement?.Id != null && !keepSet.Contains(placement.Id)) hidden.Add(placement.Id);
            return hidden;
        }
    }
}
