using SC2.MapEditor.SmartExport;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The UI/workspace half of the split: SC2MapDocument is pure data with no
    /// UI state, and everything about how a user is currently looking at and
    /// editing that data (selection, active tab/tool, camera, overlays,
    /// undo history, in-flight operation, preview scene ownership) lives
    /// here instead.
    /// </summary>
    public sealed class SC2MapSession
    {
        public SC2MapDocument Document { get; private set; }

        public SC2SelectionState Selection { get; } = new();
        public SC2WorkspaceState Workspace { get; } = new();
        public SC2OperationState Operation { get; } = new();
        public SC2CommandHistory History { get; } = new();
        public SC2SceneProjection ScenePreview { get; } = new();

        /// <summary>Where blob-backed commands (Costs/Hfield grids, Terrain
        /// heights/triangle-pair assignments) write their content — null
        /// until the host assigns one. Deliberately not constructed here:
        /// SC2MapSession stays free of any UnityEngine dependency (no
        /// Application.persistentDataPath reference), so the host
        /// (SC2GameViewEditorUI, which already needs Unity APIs for its own
        /// save/load path) is what decides the actual directory and
        /// constructs the SC2DirectoryBlobStore.</summary>
        public ISC2BlobStore BlobStore { get; set; }

        /// <summary>Every SC2DocumentChangeSet.
        /// DirectDirtyFlags raised via Document.Changed since the last
        /// MarkExported() call, OR'd together and expanded through
        /// SC2ExportDependencyGraph.ExpandStaleFlags (so e.g. a terrain
        /// geometry edit also marks hfield/costs/collision2 stale even
        /// though nothing wrote to those flags directly) — real ground
        /// truth for whatever DOES flow through a real ISC2DocumentCommand.
        ///
        /// Deliberately NOT yet consulted by SC2TerrainExporter.
        /// ExportCorrectSmart to decide which files to skip regenerating:
        /// several live edit paths (Terrain brush sculpting/painting most
        /// notably — no ISC2DocumentCommand exists for it at all, plus
        /// several other commands not yet wired to a
        /// shell) mutate SC2TerrainData directly and never raise
        /// Changed. Treating "not in PendingDirtyFlags" as "safe to skip
        /// regenerating" today would silently ship a stale (or entirely
        /// unwritten, on a fresh export) file for exactly those edits. This
        /// property exists now, correctly accumulating and resetting, so
        /// closing the remaining document read-back gaps and
        /// a real OriginalPassthrough via the blob store have
        /// something real to build on — flipping the Smart Export writer
        /// over to actually trust this for skipping work is deferred until
        /// then.</summary>
        public SC2DirtyFlags PendingDirtyFlags { get; private set; }

        public SC2MapSession(SC2MapDocument document)
        {
            Document = document;
            SubscribeDirtyTracking();
        }

        /// <summary>Escape hatch for domains that don't (yet)
        /// mutate through a real ISC2DocumentCommand — currently terrain
        /// geometry sculpting and pair/RNM-blend brush painting
        /// (SC2GeometryMode/SC2PaintMode call this directly from their own
        /// MouseUp handlers, which use a completely separate undo mechanism
        /// from Session.History and were never going to be re-plumbed
        /// through the command system just for this). Deliberately mirrors
        /// SubscribeDirtyTracking's own Document.Changed handler as closely
        /// as possible — same ExpandStaleFlags expansion for
        /// PendingDirtyFlags, same "mark every newly-touched
        /// SC2DerivedState-bearing downstream domain Stale (never demoting
        /// an already-Dirty one)" rule — so a manually-marked domain is
        /// indistinguishable, to SmartExport and to any "is this stale" UI,
        /// from one that went through a real command.</summary>
        public void MarkDirty(SC2DirtyFlags flags)
        {
            if (flags == SC2DirtyFlags.None) return;
            var before = PendingDirtyFlags;
            PendingDirtyFlags = SC2ExportDependencyGraph.ExpandStaleFlags(before | flags);
            var newlyTouched = PendingDirtyFlags & ~before;
            MarkStaleIfNewlyTouched(SC2DomainId.Hfield, SC2DirtyFlags.HfieldHeightmap | SC2DirtyFlags.HfieldTerrainTypes, newlyTouched);
            MarkStaleIfNewlyTouched(SC2DomainId.Costs, SC2DirtyFlags.Costs, newlyTouched);
            MarkStaleIfNewlyTouched(SC2DomainId.Collision2, SC2DirtyFlags.Collision2, newlyTouched);
        }

        private void MarkStaleIfNewlyTouched(SC2DomainId domain, SC2DirtyFlags relevantBits, SC2DirtyFlags newlyTouched)
        {
            if ((newlyTouched & relevantBits) == 0) return;
            if (GetDomainState(domain) != SC2DerivedState.Dirty)
                SetDomainState(domain, SC2DerivedState.Stale);
        }

        /// <summary>Resets PendingDirtyFlags to None and flips every
        /// domain's SC2DerivedState from Dirty/Stale back to Current — call
        /// after a successful export/save has consumed whatever was
        /// pending, so the next accumulation starts clean. Safe to do
        /// unconditionally (not gated on PendingDirtyFlags the way skipping
        /// regeneration would be) because DoExport/ExportCorrectSmart both
        /// unconditionally regenerate every one of these domains' files on
        /// every call today — "just wrote a fresh copy of everything" is
        /// true regardless of what was or wasn't tracked as dirty going
        /// in.</summary>
        public void MarkExported()
        {
            PendingDirtyFlags = SC2DirtyFlags.None;
            ResetStateIfDirtyOrStale(SC2DomainId.Costs);
            ResetStateIfDirtyOrStale(SC2DomainId.Hfield);
            ResetStateIfDirtyOrStale(SC2DomainId.Collision2);
            ResetStateIfDirtyOrStale(SC2DomainId.Scenario);
            ResetStateIfDirtyOrStale(SC2DomainId.Decals);
        }

        private void SubscribeDirtyTracking()
        {
            Document.Changed += changeSet =>
            {
                PendingDirtyFlags = SC2ExportDependencyGraph.ExpandStaleFlags(PendingDirtyFlags | changeSet.DirectDirtyFlags);

                // Dirty means the USER changed this domain directly; Stale
                // means an upstream source changed and this one hasn't been
                // regenerated against it yet — Dirty always wins over
                // Stale for the same domain in the same change, and an
                // already-Dirty domain never gets demoted back to Stale by a
                // later change that only invalidates it.
                if (changeSet.ChangedDomains != null)
                    foreach (var domain in changeSet.ChangedDomains)
                        SetDomainState(domain, SC2DerivedState.Dirty);

                if (changeSet.InvalidatedDomains != null)
                    foreach (var domain in changeSet.InvalidatedDomains)
                        if (GetDomainState(domain) != SC2DerivedState.Dirty)
                            SetDomainState(domain, SC2DerivedState.Stale);
            };
        }

        private void ResetStateIfDirtyOrStale(SC2DomainId domain)
        {
            var state = GetDomainState(domain);
            if (state == SC2DerivedState.Dirty || state == SC2DerivedState.Stale)
                SetDomainState(domain, SC2DerivedState.Current);
        }

        /// <summary>Only 5 of SC2MapDocument's 8 domain sub-documents carry
        /// a SC2DerivedState field at all (Costs/Hfield/Collision2/Scenario/
        /// Decals — the ones with a real generated-from-a-source concept;
        /// see SC2DerivedState's own doc comment). Terrain/Objects/
        /// Environment/Identity/Source/EntryPoint/Archive/Assets have none,
        /// so both accessors below are deliberate no-ops for anything
        /// outside that list of 5.</summary>
        private SC2DerivedState? GetDomainState(SC2DomainId domain) => domain switch
        {
            SC2DomainId.Costs => Document.Costs.State,
            SC2DomainId.Hfield => Document.Hfield.State,
            SC2DomainId.Collision2 => Document.Collision2.State,
            SC2DomainId.Scenario => Document.Scenario.State,
            SC2DomainId.Decals => Document.Decals.State,
            _ => null,
        };

        private void SetDomainState(SC2DomainId domain, SC2DerivedState state)
        {
            switch (domain)
            {
                case SC2DomainId.Costs: Document.Costs.State = state; break;
                case SC2DomainId.Hfield: Document.Hfield.State = state; break;
                case SC2DomainId.Collision2: Document.Collision2.State = state; break;
                case SC2DomainId.Scenario: Document.Scenario.State = state; break;
                case SC2DomainId.Decals: Document.Decals.State = state; break;
                default: break;
            }
        }

        /// <summary>Replaces the active document (e.g. after a successful
        /// import) and resets the document-scoped session state: Selection,
        /// History and Operation all refer to the OLD document's objects/
        /// commands, so carrying them over would be meaningless at best and
        /// actively wrong at worst (e.g. an undo stack full of commands that
        /// no longer apply to anything). Workspace (active tab/tool/camera/
        /// overlays) and ScenePreview are UI-chrome, not document-scoped, so
        /// they deliberately survive the swap.</summary>
        public void SetDocument(SC2MapDocument document)
        {
            Document = document ?? throw new System.ArgumentNullException(nameof(document));
            Selection.Clear();
            History.Clear();
            Operation.Reset();
            PendingDirtyFlags = SC2DirtyFlags.None;
            SubscribeDirtyTracking();
        }
    }
}
