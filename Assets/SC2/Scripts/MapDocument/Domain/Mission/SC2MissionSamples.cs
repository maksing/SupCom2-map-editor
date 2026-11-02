using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>Ready-made graphs: a way to see what the nodes do before building a mission from scratch.</summary>
    public static class SC2MissionSamples
    {
        /// <summary>Stage 1 spawns an enemy outpost and asks the player to destroy it; stage 2 waits a little and ends the mission.</summary>
        public static SC2MissionDocument DestroyTheOutpost(string playerArmy = "ARMY_1", string enemyArmy = "ARMY_2", string group = "OUTPOST")
        {
            var doc = new SC2MissionDocument { Title = "Destroy the outpost" };

            var stage1 = new SC2MissionStage { Id = "stage1", Name = "Stage 1" };
            var spawn = stage1.AddNode(SC2MissionNodeCatalog.SpawnGroup, 0, 0);
            spawn.SetParam("army", enemyArmy);
            spawn.SetParam("group", group);
            var objective = stage1.AddNode(SC2MissionNodeCatalog.ObjectiveKill, 260, 0);
            objective.SetParam("army", enemyArmy);
            objective.SetParam("group", group);
            objective.SetParam("title", "Destroy the outpost");
            objective.SetParam("description", "Wipe out the enemy outpost.");
            var advance = stage1.AddNode(SC2MissionNodeCatalog.AdvanceStage, 540, -60);
            advance.SetParam("stage", "stage2");
            var defeat = stage1.AddNode(SC2MissionNodeCatalog.Defeat, 540, 80);
            stage1.Connect(SC2MissionStage.StartNodeId, SC2MissionStage.StartPort, spawn.Id);
            stage1.Connect(spawn.Id, "out", objective.Id);
            stage1.Connect(objective.Id, "completed", advance.Id);
            stage1.Connect(objective.Id, "failed", defeat.Id);

            var stage2 = new SC2MissionStage { Id = "stage2", Name = "Stage 2" };
            var timer = stage2.AddNode(SC2MissionNodeCatalog.Timer, 0, 0);
            timer.SetParam("seconds", "10");
            var victory = stage2.AddNode(SC2MissionNodeCatalog.Victory, 260, 0);
            stage2.Connect(SC2MissionStage.StartNodeId, SC2MissionStage.StartPort, timer.Id);
            stage2.Connect(timer.Id, "fired", victory.Id);

            doc.Stages.Add(stage1);
            doc.Stages.Add(stage2);
            return doc;
        }

        /// <summary>Three rounds of a loop, wired the way every counted loop is: the body ends at the loop's 'next' input and the loop's 'done'
        /// output leaves it. It needs nothing from the map, so it shows the wiring anywhere.</summary>
        public static SC2MissionDocument ThreeWaves()
        {
            var doc = new SC2MissionDocument { Title = "Three waves" };
            doc.Variables.Add(new SC2MissionVariable { Name = "wave", Type = SC2MissionVariable.Number, Initial = "0" });

            var stage = new SC2MissionStage { Id = "stage1", Name = "Stage 1" };
            var loop = stage.AddNode("loop_repeat", 0, 0);
            loop.SetParam("count", "3");
            loop.SetParam("pause", "2");
            loop.SetParam("variable", "wave");
            var talk = stage.AddNode(SC2MissionNodeCatalog.Dialogue, 320, -20);
            talk.SetParam("speaker", "HQ");
            talk.SetParam("text", "Another wave is coming.");
            talk.SetParam("seconds", "3");
            var done = stage.AddNode(SC2MissionNodeCatalog.Dialogue, 320, 160);
            done.SetParam("speaker", "HQ");
            done.SetParam("text", "That was the last wave.");
            done.SetParam("seconds", "3");
            var victory = stage.AddNode(SC2MissionNodeCatalog.Victory, 640, 160);
            stage.Connect(SC2MissionStage.StartNodeId, SC2MissionStage.StartPort, loop.Id, "start");
            stage.Connect(loop.Id, "body", talk.Id);
            stage.Connect(talk.Id, "done", loop.Id, "next");
            stage.Connect(loop.Id, "done", done.Id);
            stage.Connect(done.Id, "done", victory.Id);
            doc.Stages.Add(stage);
            return doc;
        }

        /// <summary>A co-op mission that uses every part of the editor: an intro cutscene with dialogue, an enemy base with an OpAI raid, a
        /// kill objective that switches the raids on and counts in a variable, a solo or co-op ending.</summary>
        public static SC2MissionDocument HoldTheLine(string enemyArmy = "ARMY_3") =>
            HoldTheLine(enemyArmy, "OUTPOST", "BASE_100", "ENEMY_BASE", "CAM_1", "RAID_1", coop: true);

        private static SC2MissionDocument HoldTheLine(string enemyArmy, string outpostGroup, string baseGroup, string baseMarker, string cameraMarker, string chain, bool coop)
        {
            var doc = new SC2MissionDocument { Title = "Hold the line" };
            doc.Settings.Coop = coop;
            doc.Settings.HumanSlots = coop ? 2 : 1;
            doc.Settings.FailWhen = SC2MissionSettings.FailAny;

            doc.Variables.Add(new SC2MissionVariable { Name = "outposts_destroyed", Type = SC2MissionVariable.Number, Initial = "0" });

            var enemyBase = new SC2MissionBase { Id = "EnemyMain", Army = enemyArmy, Marker = baseMarker, Radius = 50, Engineers = 2 };
            enemyBase.BuildGroups.Add(new SC2MissionBuildGroup { Group = baseGroup, Priority = 100 });
            enemyBase.OpAIs.Add(new SC2MissionOpAI
            {
                Name = "EnemyRaid", Type = "LandAttackUEF", Thread = SC2MissionOpAI.RandomRoute, ChildCount = 4, MaxPlatoons = 2, AttackDelay = 60, Enabled = false,
                Chains = { chain },
            });
            doc.Bases.Add(enemyBase);

            var intro = new SC2MissionStage { Id = "intro", Name = "Intro" };
            var begin = intro.AddNode(SC2MissionNodeCatalog.CutsceneStart, 0, 0);
            var camera = intro.AddNode(SC2MissionNodeCatalog.CameraMove, 240, 0);
            camera.SetParam("marker", cameraMarker);
            var talk = intro.AddNode(SC2MissionNodeCatalog.Dialogue, 480, 0);
            talk.SetParam("speaker", "Command");
            talk.SetParam("text", "Commander, an enemy outpost is closing in. Hold the line.");
            var end = intro.AddNode(SC2MissionNodeCatalog.CutsceneEnd, 720, 0);
            var next = intro.AddNode(SC2MissionNodeCatalog.AdvanceStage, 960, 0);
            next.SetParam("stage", "stage1");
            intro.Connect(SC2MissionStage.StartNodeId, SC2MissionStage.StartPort, begin.Id);
            intro.Connect(begin.Id, "out", camera.Id);
            intro.Connect(camera.Id, "done", talk.Id);
            intro.Connect(talk.Id, "done", end.Id);
            intro.Connect(end.Id, "out", next.Id);

            var stage1 = new SC2MissionStage { Id = "stage1", Name = "Stage 1" };
            var spawn = stage1.AddNode(SC2MissionNodeCatalog.SpawnGroup, 0, 0);
            spawn.SetParam("army", enemyArmy);
            spawn.SetParam("group", outpostGroup);
            var order = stage1.AddNode(SC2MissionNodeCatalog.GiveOrder, 240, 0);
            order.SetParam("army", enemyArmy);
            order.SetParam("group", outpostGroup);
            order.SetParam("chain", chain);
            var objective = stage1.AddNode(SC2MissionNodeCatalog.ObjectiveKill, 480, 0);
            objective.SetParam("army", enemyArmy);
            objective.SetParam("group", outpostGroup);
            var raids = stage1.AddNode(SC2MissionNodeCatalog.OpAISet, 760, -80);
            raids.SetParam("base", "EnemyMain");
            raids.SetParam("opai", "EnemyRaid");
            raids.SetParam("state", "enable");
            var count = stage1.AddNode(SC2MissionNodeCatalog.SetVariable, 1000, -80);
            count.SetParam("variable", "outposts_destroyed");
            count.SetParam("op", "add");
            count.SetParam("value", "1");
            var split = stage1.AddNode(SC2MissionNodeCatalog.CoopBranch, 1240, -80);
            var coopAdvance = stage1.AddNode(SC2MissionNodeCatalog.AdvanceStage, 1480, -140);
            coopAdvance.SetParam("stage", "stage2");
            var soloAdvance = stage1.AddNode(SC2MissionNodeCatalog.AdvanceStage, 1480, -20);
            soloAdvance.SetParam("stage", "stage2");
            var lose = stage1.AddNode(SC2MissionNodeCatalog.Defeat, 760, 100);
            stage1.Connect(SC2MissionStage.StartNodeId, SC2MissionStage.StartPort, spawn.Id);
            stage1.Connect(spawn.Id, "out", order.Id);
            stage1.Connect(order.Id, "out", objective.Id);
            stage1.Connect(objective.Id, "completed", raids.Id);
            stage1.Connect(raids.Id, "out", count.Id);
            stage1.Connect(count.Id, "out", split.Id);
            stage1.Connect(split.Id, "coop", coopAdvance.Id);
            stage1.Connect(split.Id, "solo", soloAdvance.Id);
            stage1.Connect(objective.Id, "failed", lose.Id);

            var stage2 = new SC2MissionStage { Id = "stage2", Name = "Stage 2" };
            var survive = stage2.AddNode(SC2MissionNodeCatalog.ObjectiveTimer, 0, 0);
            survive.SetParam("title", "Survive the raids");
            survive.SetParam("seconds", "300");
            var win = stage2.AddNode(SC2MissionNodeCatalog.Victory, 280, -40);
            var fail = stage2.AddNode(SC2MissionNodeCatalog.Defeat, 280, 60);
            stage2.Connect(SC2MissionStage.StartNodeId, SC2MissionStage.StartPort, survive.Id);
            stage2.Connect(survive.Id, "completed", win.Id);
            stage2.Connect(survive.Id, "failed", fail.Id);

            doc.Stages.Add(intro);
            doc.Stages.Add(stage1);
            doc.Stages.Add(stage2);
            return doc;
        }

        // ------------------------------------------------------------------ samples that fit the map being edited

        /// <summary>The enemy of the sample is the last army of the map, the player the first; the group, markers and chain are ones the map has.</summary>
        public static SC2MissionDocument DestroyTheOutpost(SC2MissionMapInfo map)
        {
            var player = map?.ArmyOrder is { Count: > 0 } ? map.ArmyOrder[0] : "ARMY_1";
            var enemy = LastArmy(map, "ARMY_2");
            return DestroyTheOutpost(player, enemy, FirstGroup(map, enemy, 0, "OUTPOST"));
        }

        /// <summary>A co-op mission needs a third army for the enemy (two human slots come first); on a map with two armies the sample is solo.</summary>
        public static SC2MissionDocument HoldTheLine(SC2MissionMapInfo map)
        {
            var enemy = LastArmy(map, "ARMY_3");
            var coop = map?.ArmyOrder == null || map.ArmyOrder.Count == 0 || map.ArmyOrder.Count >= 3;
            var markers = PlainMarkers(map);
            return HoldTheLine(enemy, FirstGroup(map, enemy, 0, "OUTPOST"), FirstGroup(map, enemy, 1, "BASE_100"),
                markers.Count > 0 ? markers[0] : "ENEMY_BASE", markers.Count > 1 ? markers[1] : markers.Count > 0 ? markers[0] : "CAM_1",
                PatrolChain(map), coop);
        }

        /// <summary>The first chain that is a route; a base's own engineer chain is not one.</summary>
        private static string PatrolChain(SC2MissionMapInfo map)
        {
            if (map?.Chains != null)
                foreach (var chain in Sorted(map.Chains))
                    if (!chain.EndsWith("_EngineerChain", System.StringComparison.Ordinal)) return chain;
            return "RAID_1";
        }

        private static string LastArmy(SC2MissionMapInfo map, string fallback) =>
            map?.ArmyOrder is { Count: > 0 } ? map.ArmyOrder[map.ArmyOrder.Count - 1] : fallback;

        /// <summary>The nth group (by name) of an army, or the fallback when the map has fewer.</summary>
        private static string FirstGroup(SC2MissionMapInfo map, string army, int nth, string fallback)
        {
            if (map?.Groups == null || !map.Groups.TryGetValue(army, out var groups)) return fallback;
            var sorted = Sorted(groups);
            // a group with no units cannot be spawned, so the samples keep to the ones that have some when the map says how many there are
            if (map.GroupUnitCounts != null && map.GroupUnitCounts.TryGetValue(army, out var counts))
            {
                var filled = sorted.FindAll(g => counts.TryGetValue(g, out var n) && n > 0);
                if (filled.Count > 0) sorted = filled;
            }
            return nth < sorted.Count ? sorted[nth] : sorted.Count > 0 ? sorted[sorted.Count - 1] : fallback;
        }

        /// <summary>The markers that are not army start positions, by name.</summary>
        private static List<string> PlainMarkers(SC2MissionMapInfo map)
        {
            var list = new List<string>();
            if (map?.Markers == null) return list;
            foreach (var m in map.Markers)
                if (!m.StartsWith("ARMY_", System.StringComparison.Ordinal)) list.Add(m);
            list.Sort(System.StringComparer.Ordinal);
            return list;
        }

        private static List<string> Sorted(IEnumerable<string> names)
        {
            var list = new List<string>(names);
            list.Sort(System.StringComparer.Ordinal);
            return list;
        }
    }
}
