using System;
using System.Collections.Generic;
using System.IO;
using BdfIO.Core;
using BdfIO.Formats.Costs;

/// <summary>
/// Unified "Import Map" pipeline — real costs.bdf navmesh import. Before this,
/// no import path anywhere in this codebase read a map's real, hand-tuned
/// Costs navmesh into anything editable: re-importing an already-authored
/// vanilla map gave back its terrain/objects/environment, but its Costs data
/// was silently lost (only auto-generate-from-height or repaint-by-hand were
/// ever possible afterward).
///
/// Reuses CostsBdf.Parse (the format layer, already proven correct by
/// SC2VanillaImportDiagnostics' own parse-only stage) and
/// SC2TerrainData.LandOnlyCostsBlockNames/PlayableWaterCostsBlockNames (the
/// real vanilla name arrays this project already uses for from-scratch
/// defaults) to assign each block a name POSITIONALLY — the raw file itself
/// carries no block names, only an ordered BlockIndex, so block count (3 vs
/// 5) selects which real name array applies, matching the same convention
/// SC2CostsAutoGen/SC2CostsLegacyBridge already use elsewhere.
///
/// Islandmap/LongInts are deliberately NOT imported: Islandmap is a derived
/// connectivity map the existing "Recompute Islands" button already
/// regenerates correctly from Navmesh, and LongInts' meaning is unconfirmed
/// and not needed for editing.
/// </summary>
public static class SC2CostsBdfImporter
{
    public sealed class Result
    {
        public bool Success;
        public string Summary;
        public int BlocksImported;
    }

    public static Result Import(SC2TerrainData data, string costsBdfPath)
    {
        var bytes = File.ReadAllBytes(costsBdfPath);
        var container = BdfContainer.Read(bytes, CostsBdf.Padding);
        var model = CostsBdf.Parse(container);
        return Import(data, model);
    }

    public static Result Import(SC2TerrainData data, CostsModel model)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (model == null || model.Blocks.Count == 0)
            return new Result { Success = false, Summary = "costs.bdf has no blocks." };

        var blocks = new List<CostsBlock>(model.Blocks);
        blocks.Sort((a, b) => a.BlockIndex.CompareTo(b.BlockIndex));

        string[] names;
        if (blocks.Count == SC2TerrainData.LandOnlyCostsBlockNames.Length)
        {
            names = SC2TerrainData.LandOnlyCostsBlockNames;
            data.PlayableWater = false;
        }
        else if (blocks.Count == SC2TerrainData.PlayableWaterCostsBlockNames.Length)
        {
            names = SC2TerrainData.PlayableWaterCostsBlockNames;
            data.PlayableWater = true;
        }
        else
        {
            return new Result
            {
                Success = false,
                Summary = $"costs.bdf has {blocks.Count} block(s) — expected " +
                          $"{SC2TerrainData.LandOnlyCostsBlockNames.Length} (land-only) or " +
                          $"{SC2TerrainData.PlayableWaterCostsBlockNames.Length} (playable water)."
            };
        }

        var firstLen = blocks[0].Navmesh.Length;
        var gridSize = (int)Math.Round(Math.Sqrt(firstLen));
        if (gridSize <= 0 || gridSize * gridSize != firstLen)
            return new Result { Success = false, Summary = $"costs.bdf block 0's navmesh length ({firstLen}) isn't a perfect square — can't derive a grid size." };

        var newBlocks = new List<SC2CostsBlockData>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Navmesh.Length != firstLen)
                return new Result { Success = false, Summary = $"costs.bdf block {i} ('{names[i]}') navmesh length ({block.Navmesh.Length}) doesn't match block 0's ({firstLen})." };

            newBlocks.Add(new SC2CostsBlockData
            {
                Name = names[i],
                Navmesh = (byte[])block.Navmesh.Clone()
            });
        }

        data.CostsGridSize = gridSize;
        data.CostsBlocks = newBlocks;

        return new Result
        {
            Success = true,
            BlocksImported = newBlocks.Count,
            Summary = $"Imported {newBlocks.Count} Costs block(s), grid {gridSize}x{gridSize}. " +
                      "Run \"Recompute Islands\" on the Costs tab to rebuild the island map from this data."
        };
    }
}
