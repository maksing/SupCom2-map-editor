using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Selection tracked by
    /// stable object id, never by list index (the same lesson that drove
    /// SC2ObjectGraphRefs' MarkerIdsA/MarkerIdsB migration). Kind is a plain
    /// string rather than an enum tied to domain types on purpose, so this
    /// session layer doesn't take a compile-time dependency on legacy domain
    /// types. Today
    /// nothing populates this — SC2ObjectsShell still owns _selMarkers/
    /// _selProps locally — this only exists as a seam for future wiring.
    /// </summary>
    public sealed class SC2SelectionState
    {
        public string Kind = "";
        public readonly List<int> SelectedIds = new();

        public bool IsEmpty => SelectedIds.Count == 0;

        public void Clear()
        {
            Kind = "";
            SelectedIds.Clear();
        }

        public void Set(string kind, IEnumerable<int> ids)
        {
            Kind = kind ?? "";
            SelectedIds.Clear();
            SelectedIds.AddRange(ids);
        }

        public bool Contains(int id) => SelectedIds.Contains(id);
    }
}
