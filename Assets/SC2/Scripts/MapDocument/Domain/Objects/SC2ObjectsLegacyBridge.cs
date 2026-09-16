using System;
using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// Same
    /// read-only legacy-view snapshot pattern as every other domain bridge.
    /// Simpler than most: since the 8 element types already carry
    /// stable Ids (see SC2ObjectsDocument's own doc comment), there's no
    /// index-to-id conversion to do — just group the existing lists.
    ///
    /// List-level snapshot, not a deep element clone: each returned list is
    /// a NEW List&lt;T&gt; instance (so adding/removing through the document
    /// never touches the live SC2TerrainData lists), but the ELEMENT objects
    /// inside are the SAME references as the live data. Cheap, and honest
    /// given nothing writes live edits through this document yet — a deep
    /// clone would just be extra cost with no consumer that needs isolation
    /// at the element level today.
    ///
    /// Split into a pure-data core (testable standalone) and a thin
    /// SC2TerrainData-accepting overload in a separate file/partial
    /// (SC2ObjectsLegacyBridge.ScriptableObject.cs).
    /// </summary>
    public static partial class SC2ObjectsLegacyBridge
    {
        public static SC2ObjectsDocument BuildFromLegacy(
            IReadOnlyList<SC2ObjProp> props,
            IReadOnlyList<SC2ObjMarker> markers,
            IReadOnlyList<SC2ObjGraph> graphs,
            IReadOnlyList<SC2ObjArmy> armies,
            IReadOnlyList<SC2ObjUnitNode> units,
            IReadOnlyList<SC2ObjZone> zones,
            IReadOnlyList<SC2ObjChain> chains,
            IReadOnlyList<SC2ObjSymmetryGroup> symmetryGroups,
            int nextObjectId,
            string saveLuaTextOriginal = "",
            string scenarioLuaTextOriginal = "",
            IReadOnlyList<SC2ObjReferenceUnit> referenceUnits = null)
        {
            if (props == null) throw new ArgumentNullException(nameof(props));
            if (markers == null) throw new ArgumentNullException(nameof(markers));
            if (graphs == null) throw new ArgumentNullException(nameof(graphs));
            if (armies == null) throw new ArgumentNullException(nameof(armies));
            if (units == null) throw new ArgumentNullException(nameof(units));
            if (zones == null) throw new ArgumentNullException(nameof(zones));
            if (chains == null) throw new ArgumentNullException(nameof(chains));
            if (symmetryGroups == null) throw new ArgumentNullException(nameof(symmetryGroups));

            return new SC2ObjectsDocument
            {
                Props = new List<SC2ObjProp>(props),
                Markers = new List<SC2ObjMarker>(markers),
                Graphs = new List<SC2ObjGraph>(graphs),
                Armies = new List<SC2ObjArmy>(armies),
                Units = new List<SC2ObjUnitNode>(units),
                Zones = new List<SC2ObjZone>(zones),
                Chains = new List<SC2ObjChain>(chains),
                SymmetryGroups = new List<SC2ObjSymmetryGroup>(symmetryGroups),
                NextObjectId = nextObjectId,
                SaveLuaTextOriginal = saveLuaTextOriginal ?? "",
                ScenarioLuaTextOriginal = scenarioLuaTextOriginal ?? "",
                ReferenceUnits = referenceUnits != null ? new List<SC2ObjReferenceUnit>(referenceUnits) : new List<SC2ObjReferenceUnit>(),
            };
        }
    }
}
