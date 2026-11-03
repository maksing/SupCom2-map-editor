using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using SC2.MapEditor.Document;

/// <summary>Compiles the Lua of every node of the mission catalog inside Unity (the Mono runtime of a player build), several times each, and
/// reports the nodes that do not compile. The standalone tests run on .NET, where an intermittent KopiLua failure did not show; see
/// Docs/Process/MissionPanelHarness.md.
///
/// Entry point: SC2MissionCatalogProbe.Run. Environment: MISSION_REPEAT (compiles per node, default 5), MISSION_OUT (where the Lua of a
/// failing node is written). Result line, tag "[Catalog]": nodes=N badChoices=N notCompiling=N good=True bad=False. Exit code 1 on a failure.</summary>
public static class SC2MissionCatalogProbe
{
    public static void Run()
    {
        var failed = 0;
        try
        {
            var repeat = int.TryParse(Environment.GetEnvironmentVariable("MISSION_REPEAT"), out var n) && n > 0 ? n : 5;
            var outDir = Environment.GetEnvironmentVariable("MISSION_OUT") is { Length: > 0 } dir ? dir : Path.Combine(Path.GetTempPath(), "mission_harness");
            Directory.CreateDirectory(outDir);

            var badChoices = 0;
            foreach (var def in SC2MissionNodeCatalog.All)
                foreach (var p in def.Params)
                    if (p.Kind == SC2MissionParamKind.Choice && (p.Choices == null || p.Choices.Length == 0))
                    {
                        badChoices++;
                        Debug.Log("[Catalog] choice without choices: " + def.Type + "." + p.Key);
                    }

            foreach (var def in SC2MissionNodeCatalog.All)
            {
                var doc = new SC2MissionDocument();
                doc.Bases.Add(new SC2MissionBase { Id = "Base1", Army = "ARMY_2", Marker = "M" });
                var stage = new SC2MissionStage { Id = "stage1" };
                var node = stage.AddNode(def.Type, 0, 0);
                stage.Connect(0, "out", node.Id);
                doc.Stages.Add(stage);
                var lua = SC2MissionCompiler.Compile(doc).Lua;

                var bad = 0;
                var firstError = "";
                for (var i = 0; i < repeat; i++)
                    if (!SC2LuaSyntaxCheck.TryCompile(lua, out var error)) { bad++; if (firstError.Length == 0) firstError = error; }
                if (bad == 0) continue;
                failed++;
                File.WriteAllText(Path.Combine(outDir, "catalog_fail_" + def.Type + ".lua"), lua);
                Debug.Log("[Catalog] does not compile: " + def.Type + " (" + bad + " of " + repeat + ") " + firstError);
            }

            // the check itself: good Lua must pass and bad Lua must fail with a message
            var good = SC2LuaSyntaxCheck.TryCompile("local a = 1", out _);
            var broken = SC2LuaSyntaxCheck.TryCompile("local = = 1", out var message);
            Debug.Log("[Catalog] nodes=" + SC2MissionNodeCatalog.All.Length + " badChoices=" + badChoices + " notCompiling=" + failed
                + " good=" + good + " bad=" + broken + " badMessage='" + message + "'");
            if (badChoices > 0 || !good || broken) failed++;
        }
        catch (Exception ex)
        {
            Debug.LogError("[Catalog] exception " + ex);
            failed++;
        }
        finally
        {
            EditorApplication.Exit(failed > 0 ? 1 : 0);
        }
    }
}
