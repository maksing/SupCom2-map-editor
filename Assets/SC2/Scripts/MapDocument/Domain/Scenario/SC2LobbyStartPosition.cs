using System.Collections.Generic;
using UnityEngine;

namespace SC2.MapEditor.Document
{
    /// <summary>Where one army's start marker is drawn on the lobby minimap —
    /// one entry of _scenario.lua's ScenarioInfo.StartPositions
    /// (<c>['ARMY_1'] = { x, y, z }</c>, world units; the lobby maps x to the
    /// minimap's horizontal axis and z to the vertical one, both divided by
    /// the map size). Independent of the army's in-game spawn, which lives in
    /// _save.lua's ARMY_n marker: moving one never moves the other — with one
    /// exception, see <see cref="IsCurrent"/>.</summary>
    public sealed class SC2LobbyStartPosition
    {
        /// <summary>How closely the army's real position has to match the one
        /// recorded in <see cref="BaseX"/>/<see cref="BaseZ"/>.</summary>
        public const float BaseTolerance = 0.01f;

        public string ArmyName = "";
        public float X;
        public float Y;
        public float Z;

        /// <summary>The army's real (ObjMarker / _save.lua) position when this
        /// lobby position was set. If the real spawn is moved afterwards the
        /// lobby position no longer applies and the lobby follows the real
        /// spawn again. False = unknown (an entry from a save that predates
        /// this, or not yet matched to a marker): treated as still valid.</summary>
        public bool HasBase;
        public float BaseX;
        public float BaseZ;

        /// <summary>Whether this entry still applies to an army now standing at
        /// <paramref name="realPosition"/>. Comparing against the recorded
        /// position (rather than hooking every place a marker can move) also
        /// rolls the entry back when the move is undone.</summary>
        public bool IsCurrent(Vector3 realPosition) =>
            !HasBase || (Mathf.Abs(BaseX - realPosition.x) <= BaseTolerance && Mathf.Abs(BaseZ - realPosition.z) <= BaseTolerance);

        public void SetBase(Vector3 realPosition)
        {
            HasBase = true;
            BaseX = realPosition.x;
            BaseZ = realPosition.z;
        }

        public SC2LobbyStartPosition Clone() => new()
        {
            ArmyName = ArmyName, X = X, Y = Y, Z = Z, HasBase = HasBase, BaseX = BaseX, BaseZ = BaseZ,
        };

        public static List<SC2LobbyStartPosition> CloneList(IEnumerable<SC2LobbyStartPosition> source)
        {
            var result = new List<SC2LobbyStartPosition>();
            if (source == null) return result;
            foreach (var entry in source)
                if (entry != null) result.Add(entry.Clone());
            return result;
        }

        /// <summary>Records the current real position as the base of every
        /// entry that has none yet (imported from scenario.lua, or saved before
        /// bases existed) and whose army is on the map.</summary>
        public static void AdoptBases(IEnumerable<SC2LobbyStartPosition> list, IReadOnlyDictionary<string, Vector3> realPositions)
        {
            if (list == null || realPositions == null) return;
            foreach (var entry in list)
                if (entry != null && !entry.HasBase && realPositions.TryGetValue(entry.ArmyName, out var real))
                    entry.SetBase(real);
        }

        /// <summary>The lobby positions that apply right now, army name →
        /// position: entries whose army is not on the map, or whose real spawn
        /// has moved since the entry was set, are left out (the lobby then
        /// shows the real spawn).</summary>
        public static Dictionary<string, Vector3> CurrentOverrides(IEnumerable<SC2LobbyStartPosition> list, IReadOnlyDictionary<string, Vector3> realPositions)
        {
            var current = new Dictionary<string, Vector3>();
            if (list == null || realPositions == null) return current;
            foreach (var entry in list)
                if (!string.IsNullOrEmpty(entry?.ArmyName) && realPositions.TryGetValue(entry.ArmyName, out var real) && entry.IsCurrent(real))
                    current[entry.ArmyName] = new Vector3(entry.X, entry.Y, entry.Z);
            return current;
        }
    }
}
