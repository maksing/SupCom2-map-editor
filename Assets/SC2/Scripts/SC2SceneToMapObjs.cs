// SC2SceneToMapObjs.cs
//
// Converts the scene's Props/Markers/Graphs into a MapObjsModel for
// MapObjsBdf.Serialize. Handles:
//  - SC2SymmetryLink marker copies — exported as ordinary markers (a
//    follower already has an up-to-date name/position via SymmetryLiveSync);
//  - "deleted" markers (null in SC2Graph.MarkerTransforms, see
//    SymmetryToolWindow.RemoveFollowers) — skipped, with graph edge indices
//    recomputed (compaction).

using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using BdfIO.Formats.MapObjs;

public static class SC2SceneToMapObjs
{
    public sealed class Result
    {
        public MapObjsModel Model = null!;

        /// <summary>SC2Marker for each model.Markers[i] (same order).</summary>
        public List<SC2Marker> MarkerComponents = new();

        /// <summary>Markers with IsLuaOnly=true (ARMY_N/Mass/Combat Zone/..., not part of model.Markers).</summary>
        public List<SC2Marker> LuaOnlyMarkers = new();
    }

    public static Result Build(Transform mapRoot, int fileVersion = 4)
    {
        var model = new MapObjsModel { FileVersion = fileVersion };
        var markerComponents = new List<SC2Marker>();
        var luaOnlyMarkers = new List<SC2Marker>();

        var propsRoot = mapRoot.Find("Props");
        if (propsRoot != null)
        {
            foreach (Transform t in propsRoot)
            {
                var prop = t.GetComponent<SC2Prop>();
                if (prop == null) continue;

                var q = t.rotation;
                // SC2 quat = (w,x,y,z), coordinates map 1:1.
                model.Props.Add(new MapProp
                {
                    Quat = new[] { q.w, q.x, q.y, q.z },
                    Position = new[] { t.position.x, t.position.y, t.position.z },
                    Scale = t.localScale.x,
                    BlueprintPath = prop.BlueprintPath,
                    Unknown9 = prop.Unknown9,
                    Flags = prop.Flags
                });
            }
        }

        // ---- Markers: read from any graph's MarkerTransforms if one
        // exists (up-to-date order/indices accounting for symmetry and
        // deletions), otherwise directly from Markers/ (no graphs).
        var anyGraph = mapRoot.GetComponentsInChildren<SC2Graph>(true).FirstOrDefault();
        List<Transform?> markerSlots;
        if (anyGraph != null)
        {
            markerSlots = anyGraph.MarkerTransforms;
        }
        else
        {
            var markersRoot = mapRoot.Find("Markers");
            markerSlots = new List<Transform?>();
            if (markersRoot != null)
                foreach (Transform t in markersRoot)
                    if (t.GetComponent<SC2Marker>() != null) markerSlots.Add(t);
        }

        // old slot index -> new (compacted) marker index, null slots are skipped.
        var indexMap = new Dictionary<int, int>();
        for (var oldIndex = 0; oldIndex < markerSlots.Count; oldIndex++)
        {
            var t = markerSlots[oldIndex];
            if (t == null) continue;
            var marker = t.GetComponent<SC2Marker>();
            if (marker == null || marker.IsLuaOnly) continue; // lua-only markers are collected separately below

            indexMap[oldIndex] = model.Markers.Count;
            model.Markers.Add(new MapMarker
            {
                Name = marker.MarkerName,
                AlwaysOne = 1f,
                // Position comes from the Unity transform (Y = the real
                // height from save.lua — see ScdImportWindow, which reads Y
                // from LuaExtras on import, not from mapobjs).
                Position = new[] { t.position.x, t.position.y, t.position.z },
                MarkerType = marker.MarkerType
            });
            markerComponents.Add(marker);
        }

        // lua-only markers (ARMY_N/Mass/Combat Zone/... — never in a graph's
        // MarkerTransforms, see ScdImportWindow/SymmetryToolWindow) are
        // collected directly from Markers/, regardless of whether any graph exists.
        {
            var markersRootAll = mapRoot.Find("Markers");
            if (markersRootAll != null)
                foreach (Transform t in markersRootAll)
                {
                    var m = t.GetComponent<SC2Marker>();
                    if (m != null && m.IsLuaOnly) luaOnlyMarkers.Add(m);
                }
        }

        // ---- Graphs ----
        var graphsRoot = mapRoot.Find("Graphs");
        if (graphsRoot != null)
        {
            foreach (Transform t in graphsRoot)
            {
                var g = t.GetComponent<SC2Graph>();
                if (g == null) continue;

                var graph = new MapGraph { Name = g.GraphName };
                foreach (var e in g.Entries)
                {
                    if (!indexMap.TryGetValue(e.x, out var a)) continue;
                    if (!indexMap.TryGetValue(e.y, out var b)) continue;
                    graph.Entries.Add((a, b));
                }
                model.Graphs.Add(graph);
            }
        }

        return new Result { Model = model, MarkerComponents = markerComponents, LuaOnlyMarkers = luaOnlyMarkers };
    }
}
