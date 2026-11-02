using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SC2.MapEditor.Document
{
    /// <summary>One army of the map as the mission script sees it: its place in the army list and the tuning it gets.</summary>
    public sealed class SC2MissionArmyEntry
    {
        /// <summary>The engine's army index: the 1-based position in the scenario's army list.</summary>
        public int Index;
        public SC2MissionArmy Army;
        /// <summary>The role the script gives it: a human slot is always a player army, whatever the army's own role says.</summary>
        public string Role;
        public string Alias;
        /// <summary>False when the army has no entry of its own in the document and shows the defaults.</summary>
        public bool Stored;
    }

    /// <summary>Works out the army block of the generated script: which armies exist, their indices, roles and aliases.</summary>
    public static class SC2MissionArmies
    {
        public static readonly Regex AliasPattern = new("^[A-Za-z][A-Za-z0-9_]*$");
        public static readonly Regex ColorPattern = new("^[0-9A-Fa-f]{8}$");

        private static readonly string[][] Palettes =
        {
            new[] { "FF00A2BE", "FFFFFFFF", "FF00A2BE" },
            new[] { "FF950000", "FFFF7E00", "FF950000" },
            new[] { "FF007C4E", "FFFFFFFF", "FF007C4E" },
            new[] { "FF45008E", "FFFF7C1E", "FF4E00A0" },
            new[] { "FF000000", "FF000000", "FF595959" },
        };

        /// <summary>The armies of the map in the engine's order. With no map known (a test, a map without markers) the armies of the document
        /// stand in, or a player and one enemy when it has none.</summary>
        public static List<string> ArmyNames(SC2MissionDocument doc, SC2MissionMapInfo map)
        {
            var names = new List<string>();
            if (map?.ArmyOrder != null && map.ArmyOrder.Count > 0) names.AddRange(map.ArmyOrder);
            else
                foreach (var a in doc.Armies) names.Add(a.Name);
            if (names.Count == 0) { names.Add("ARMY_1"); names.Add("ARMY_2"); }
            return names;
        }

        public static int HumanSlots(SC2MissionDocument doc) => doc.Settings.Coop ? System.Math.Clamp(doc.Settings.HumanSlots, 1, 4) : 1;

        public static List<SC2MissionArmyEntry> Effective(SC2MissionDocument doc, SC2MissionMapInfo map)
        {
            var names = ArmyNames(doc, map);
            var slots = HumanSlots(doc);
            var entries = new List<SC2MissionArmyEntry>();
            var enemyCount = 0;
            for (var i = 0; i < names.Count; i++)
            {
                var stored = doc.Armies.Find(a => a.Name == names[i]);
                var isPlayer = i < slots;
                if (!isPlayer) enemyCount++;
                var army = stored ?? Default(names[i], i, isPlayer, enemyCount);
                var role = isPlayer ? SC2MissionArmy.RolePlayer : army.Role == SC2MissionArmy.RolePlayer ? SC2MissionArmy.RoleEnemy : army.Role;
                var alias = !string.IsNullOrEmpty(army.Alias) ? army.Alias : DefaultAlias(i, isPlayer, enemyCount);
                entries.Add(new SC2MissionArmyEntry { Index = i + 1, Army = army, Role = role, Alias = alias, Stored = stored != null });
            }
            return entries;
        }

        public static string DefaultAlias(int zeroBasedIndex, bool isPlayer, int enemyNumber) =>
            isPlayer ? (zeroBasedIndex == 0 ? "PLAYER" : "PLAYER" + (zeroBasedIndex + 1).ToString(CultureInfo.InvariantCulture))
                     : "ENEM" + enemyNumber.ToString("00", CultureInfo.InvariantCulture);

        public static SC2MissionArmy Default(string name, int zeroBasedIndex, bool isPlayer, int enemyNumber)
        {
            var army = new SC2MissionArmy
            {
                Name = name,
                Alias = DefaultAlias(zeroBasedIndex, isPlayer, enemyNumber),
                Role = isPlayer ? SC2MissionArmy.RolePlayer : SC2MissionArmy.RoleEnemy,
            };
            if (isPlayer)
            {
                army.UnitCap = 1500;
            }
            else
            {
                army.Colors = (string[])Palettes[(System.Math.Max(1, enemyNumber) - 1) % Palettes.Length].Clone();
                army.UnitCap = 1000;
            }
            return army;
        }

        /// <summary>The army that edits write into: the stored one, or the default one added to the document on its first change.</summary>
        public static SC2MissionArmy Store(SC2MissionDocument doc, SC2MissionArmyEntry entry)
        {
            if (!doc.Armies.Contains(entry.Army)) doc.Armies.Add(entry.Army);
            entry.Army.Alias = entry.Alias;
            entry.Stored = true;
            return entry.Army;
        }
    }
}
