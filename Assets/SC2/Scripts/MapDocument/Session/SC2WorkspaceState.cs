using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The workspace-chrome bits
    /// of SC2MapSession: which tab is active, viewport overlay toggles, and
    /// camera pivot. Mirrors fields that already exist today as private
    /// state on SC2GameViewEditorUI (_tool, OverlayObjects/Markers/Graphs/
    /// Areas) and SC2ObjectsShell — this is only a parallel seam for now
    /// (Task #17 wires it up); nothing reads or writes it yet, so the
    /// legacy fields stay authoritative until that wiring lands.
    /// </summary>
    public sealed class SC2WorkspaceState
    {
        public string ActiveTab = "";
        public string ActiveSubTab = "";
        public string ActiveTool = "Select";

        public bool OverlayObjects = true;
        public bool OverlayMarkers = true;
        public bool OverlayGraphs = true;
        public bool OverlayAreas = true;

        public Vector3 CameraFocusPoint;
        public float CameraDistance = 100f;
    }
}
