using System.Collections.Generic;

namespace SC2.MapEditor.Document
{
    /// <summary>One sentence about what a node does, shown as the library tooltip and under the title of the node's properties. The common nodes have
    /// a sentence of their own; the rest get the sentence of their category.</summary>
    public static class SC2MissionNodeDescriptions
    {
        private static readonly Dictionary<string, string> Own = new()
        {
            ["timer"] = "Waits the given number of seconds, then runs its output.",
            ["area_entered"] = "Fires when units of an army enter an area of the map: once, or every time.",
            ["unit_killed"] = "Fires when a named unit of the map is destroyed.",
            ["group_killed"] = "Fires when every unit of a group has been destroyed.",
            ["spawn_group"] = "Creates a unit group of the map for an army. A group that lies inside INITIAL already exists when the map loads.",
            ["give_order"] = "Orders a spawned group to move, attack-move or patrol along a chain.",
            ["objective_kill"] = "Shows an objective to destroy a group. Runs completed when it is gone, failed if the objective fails.",
            ["objective_protect"] = "Shows an objective to keep a group alive, optionally for a time.",
            ["objective_capture"] = "Shows an objective to capture a group.",
            ["objective_area"] = "Shows an objective to get units into an area.",
            ["objective_timer"] = "Shows a countdown objective that completes or fails when the time is up.",
            ["objective_resolve"] = "Completes or fails another objective of this stage by its node number.",
            ["dialogue"] = "Shows a line of dialogue (text, or a video with voice) and continues when it ends.",
            ["cutscene_start"] = "Begins a cutscene: locks the player's input and shows the black bars. A cutscene end node must follow.",
            ["camera_move"] = "Moves the camera to a marker over a number of seconds, then continues.",
            ["cutscene_end"] = "Ends the cutscene: the camera, the interface and the player's input come back.",
            ["nis_begin_standard"] = "Begins a standard cutscene of the game's own cinematic system.",
            ["nis_end_standard"] = "Ends a standard cutscene and gives the game back to the player.",
            ["set_variable"] = "Sets or adds to a mission variable.",
            ["branch"] = "Compares a mission variable with a value and continues through true or false.",
            ["coop_branch"] = "Continues through solo or coop, depending on how many human players are in the game.",
            ["advance_stage"] = "Ends this stage and starts another one.",
            ["victory"] = "Ends the mission with a win.",
            ["defeat"] = "Ends the mission with a loss.",
            ["base_start"] = "Starts an AI base of the Bases tab (filled or empty, as the base says).",
            ["opai_set"] = "Switches an OpAI attack wave of a base on or off.",
            ["raw_lua"] = "Runs Lua you write yourself. Use it for what the library lacks.",
            ["ask_difficulty"] = "Shows the player a menu of three difficulty levels and builds the bases after the answer.",
            ["join"] = "Waits until every one of its inputs has been reached, then continues.",
            ["once"] = "Lets the flow through the first time only.",
            ["random_branch"] = "Continues through yes or no by chance.",
            ["sequence"] = "Runs its outputs one after the other.",
            ["sequence_timed"] = "Runs its outputs one after the other with a pause between them.",
            ["switch_variable"] = "Continues through the output of the value a variable has, or through other.",
            ["gate"] = "Lets the flow through while it is open; open and close change its state.",
            ["counter"] = "Counts what reaches count and fires reached when the target is hit; reset starts again.",
            ["loop_repeat"] = "Runs body a number of times. Wire the end of the body back to next, or switch on Next round by itself.",
            ["loop_while"] = "Runs body while a variable matches a value. Wire the body back to next, or let it move by itself.",
            ["loop_forever"] = "Runs tick every few seconds until stop is reached; pause and resume hold it.",
            ["loop_foreach"] = "Goes through the units of a group one by one; the Loop unit nodes act on the current one.",
            ["comment"] = "A frame with a label around nodes. It does nothing in the game; moving it moves the nodes inside.",
        };

        private static readonly Dictionary<string, string> ByCategory = new()
        {
            ["Events"] = "Waits for something to happen in the game, then runs its output.",
            ["Objectives"] = "Gives the player an objective; its outputs run when it is completed or failed.",
            ["Units"] = "Does something to a unit or a group of units.",
            ["Actions"] = "Does something in the game and continues at once.",
            ["World"] = "Changes the map or what players can see of it.",
            ["Economy"] = "Reads or changes an army's resources or research.",
            ["Cutscene (NIS)"] = "A step of a cutscene made with the game's cinematic system.",
            ["Cutscene (simple)"] = "A short cutscene step: begin, move the camera, end.",
            ["Bases"] = "Controls an AI base or one of its attack waves.",
            ["Flow"] = "Steers the flow of the graph.",
            ["Loops"] = "Repeats part of the graph.",
            ["Coop"] = "Reacts to how many human players are in the game.",
            ["Layout"] = "Helps to arrange the graph; it does nothing in the game.",
            ["Raw Lua"] = "Runs Lua you write yourself.",
        };

        public static string Of(SC2MissionNodeDef def)
        {
            if (def == null) return "";
            if (Own.TryGetValue(def.Type, out var text)) return text;
            if (def.Type.StartsWith("loop_unit_")) return "Acts on the unit the For each loop is at. Give it the number of that loop node.";
            return ByCategory.TryGetValue(def.Category, out var generic) ? generic : "";
        }
    }
}
