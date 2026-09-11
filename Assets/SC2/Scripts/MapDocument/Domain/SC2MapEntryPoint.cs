using System;
using System.Collections.Generic;
using BdfIO.Core;

namespace SC2.MapEditor.Document
{
    /// <summary>
    /// The selected scenario.lua
    /// and the map/save/script/BDF paths resolved from it. Deliberately
    /// drops a separate "ByteOrder" field: BdfPlatform (Pc =
    /// little-endian "MFDB", Xbox = big-endian "BDFM") already encodes byte
    /// order fully — see Runtime/BdfIO/Core/BdfPlatform.cs — so a second
    /// field would just be a second place for the two to silently disagree.
    /// </summary>
    [Serializable]
    public sealed class SC2MapEntryPoint
    {
        public string ScenarioEntryPath;
        public string ScenarioEntrySha256;
        public string MapPath;
        public string SavePath;
        public string ScriptPath;

        public string BdfDirectory;
        public string BdfStem;
        public BdfPlatform Platform;

        public string ResolutionMethod;
        public List<string> ResolutionWarnings = new();
    }
}
