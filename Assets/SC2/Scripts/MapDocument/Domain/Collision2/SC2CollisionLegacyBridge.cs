namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The
    /// smallest legacy-view bridge: SC2TerrainData persists exactly one
    /// Collision2-related field, Collision2HeightOffset. The actual
    /// collision mesh is NEVER stored on SC2TerrainData — SC2Collision2Shell
    /// regenerates it on demand into its own session-local _lastModel field
    /// (SC2TerrainExporter.BuildCollision2Preview) and never writes it back.
    /// So this bridge honestly leaves Mesh unpopulated and State=Missing,
    /// the same way SC2TerrainLegacyBridge left SC2TerrainDocument.Blend
    /// null rather than fabricate something unverified.
    ///
    /// Split into a pure-data core (testable standalone) and a thin
    /// SC2TerrainData-accepting overload in a separate file/partial
    /// (SC2CollisionLegacyBridge.ScriptableObject.cs), same pattern as
    /// SC2TerrainLegacyBridge/SC2EnvironmentLegacyBridge.
    /// </summary>
    public static partial class SC2CollisionLegacyBridge
    {
        // Default only — SC2TerrainData.Collision2PolygonBudget is now the
        // real, user-adjustable (2500-10000) source of truth; this constant
        // just keeps every existing single-argument BuildFromLegacy(offset)
        // call site (tests included) meaning exactly what it always did.
        public const int LegacyMaxPolygons = 2500;

        public static SC2CollisionDocument BuildFromLegacy(float collision2HeightOffset, int maxPolygons = LegacyMaxPolygons)
        {
            return new SC2CollisionDocument
            {
                // The legacy procedural workflow only ever generates from
                // hfield — there is no "imported manual mesh" or "original
                // passthrough" concept anywhere in SC2TerrainData today.
                SourceMode = SC2CollisionSourceMode.GeneratedFromHfield,
                Mesh = null,
                Generation = new SC2CollisionGenerationSettings
                {
                    MaxPolygons = maxPolygons,
                    VerticalOffset = collision2HeightOffset,
                },
                // SC2TerrainData has no revision counter for Heights/hfield
                // at all, so there is nothing real to put here yet.
                SourceHfieldRevision = 0,
                // Missing, not Stale: Stale implies a document that WAS
                // generated and is now out of date; this document has never
                // owned a generated mesh at all.
                State = SC2DerivedState.Missing,
            };
        }
    }
}
