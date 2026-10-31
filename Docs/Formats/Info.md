# `info.bdf` (environment: sun, water, fog, glow, bloom, wind, textures)

Code: `Assets/SC2/Runtime/BdfIO/Formats/Info/InfoBdf.cs` (`InfoModel`, `InfoBdf.Parse`/`Serialize`). Container: [Container](Container.md), padding `Align32`, 1 file, 5 offset-table entries. The editor side (tabs, limits, viewport preview, texture paths) is in [Environment](../Systems/Environment.md).

## Structure

`EnvironmentHeader` (508 bytes), immediately followed by a string block holding the five texture paths (`StringPoolWriter`, deduplicated, each string 4-byte aligned, pool base 508). The editor keeps the header as a **flat 508-byte buffer** with typed accessors at fixed offsets (`InfoModel.Header`, `GetRawFloat`/`SetRawFloat` and the int and uint variants) and does not store one member per field; field names and offsets follow the game's real structure exactly.

**Offset table (5 entries, fixed)**: 240, 244, 248, 252, 256 (the five string pointers).

## The five texture pointers (inside the header)

| Offset | Field | Notes |
|---:|---|---|
| 240 | `EnvironmentMap` | environment cube |
| 244 | `SkyMap` | sky |
| 248 | `ReservedTexturePath` | empty on all 56 PC maps (the editor calls it `TexturePath3`) |
| 252 | `ColorCorrectionCube` | |
| 256 | `FogOfWarCorrectionCube` | |

Each holds a pointer into the string block that follows the header.

## Header fields

`i` = int32, all other numbers are float32 unless the row says "pointer". Offsets in bytes. Reserved offsets (the five pointers at 240 to 256) are in the table above.

| Offset | Fields |
|---:|---|
| 0 | `Version` (i), 17 on all 56 vanilla maps |
| 4, 8, 12 | `SunDirection` X, Y, Z (the direction **to** the sun) |
| 16, 20, 24 | `SunColor` R, G, B |
| 28 | `SunIntensity` |
| 32 to 44 | `ShadowColor` R, G, B, A |
| 48 | `WaterEnabled` (i) |
| 52 to 96 | water movement vectors, X then Y each: `WaterDiffuse1Movement` (52), `WaterDiffuse2Movement` (60), `WaterNormal1Movement` (68), `WaterNormal2Movement` (76), `WaterNormal3Movement` (84), `WaterNormal4Movement` (92) |
| 100 to 112 | `WaterColor` R, G, B, A |
| 116, 120 | `WaterFoamRepeatRate` X, Y |
| 124 to 136 | `WaterNormalRepeatRate` X, Y, Z, W |
| 140 | `WaterRefractionScale` |
| 144 | `WaterReflectionScale` |
| 148 | `WaterFresnelBias` |
| 152 | `WaterFresnelPower` |
| 156 | `WaterDepthScale` |
| 160 | `WaterChromaticScale` |
| 164 | `WaterSpecPower` |
| 168 | `WaterBloomFactor` |
| 172 | `WaterWhiteCapCutoff` |
| 176 | `WaterWhiteCapBrightness` |
| 180 | `WaterWaveTiming` |
| 184 | `WaterWaveModulus` |
| 188 | `WaterFoamHeightOffset` |
| 192 | `WaterWaveStartDepth` |
| 196, 200 | `WaterDepthOrigin` X, Y |
| 204, 208 | `WaterDepthBoundsSize` X, Y |
| 212 | `MaxWaterDepth` |
| 216 | `WaterElevation` (the water level the plane, depth texture and costs read) |
| 220 | `WaterRenderElevationOverride` |
| 224, 228 | `WaterAuxParameter0`, `WaterAuxParameter1` |
| 232 | `AirElevation` |
| 236 | `ReservedZero236` (zero on all 56 PC maps) |
| 240 to 256 | five string pointers (above) |
| 260 to 272 | `CoreFogColor` R, G, B, A |
| 276 to 288 | `HorizonFogColor` R, G, B, A |
| 292, 296, 300 | `CoreFogAmount1`, `2`, `3` |
| 304, 308, 312 | `HorizonFogAmount1`, `2`, `3` |
| 316, 320 | `CoreFogDist1`, `CoreFogDist2` |
| 324, 328 | `HorizonFogDist1`, `HorizonFogDist2` |
| 332, 336, 340 | `PointCloudOrigin` X, Y, Z |
| 344, 348, 352 | `PointCloudSize` X, Y, Z |
| 356 | `GlowEnabled` (i) |
| 360, 364 | `GlowBlurKernelScale0`, `1` |
| 368 | `PostProcessSourceWeight` |
| 372 | `GlowBlurPassCount` (i) |
| 376, 380 | `GlowBlurFactor0`, `1` |
| 384 | `BloomEnabled` (i) |
| 388 | `BloomThreshold` |
| 392 | `BloomDirectionCount` (i) |
| 396 | `BloomDirectionPhase` |
| 400 | `BloomSpreadDivisor` |
| 404, 408 | `BloomSampleScale` X, Y |
| 412 | `BloomResultWeight` |
| 416 | `BloomPassCount` (i) |
| 420 | `PointCloudLightAmount` |
| 424, 428, 432 | `FogOfWarLowerHeight`, `FogOfWarHighestHeight`, `FogOfWarHeightStrength` |
| 436, 440, 444 | `IntelLowerHeight`, `IntelHighestHeight`, `IntelHeightStrength` |
| 448 | `DepthOfFieldEnabled` (i) |
| 452, 456 | `DepthOfFieldStart`, `DepthOfFieldEnd` |
| 460, 464 | `SharpenStart`, `SharpenEnd` |
| 468 | `SharpenBaseAmount` |
| 472, 476 | `SharpenNearOffset`, `SharpenFarOffset` |
| 480, 484, 488 | `RenderPassACEnabled`, `RenderPassB0Enabled`, `RenderPassB4Enabled` (i) |
| 492 | `ShadowCastDistance` |
| 496, 500 | `WindDirection` X, Z |
| 504 | `WindSpeed` |

The header ends at 508.

## Rules

- The header is exactly 508 bytes; the model copies it whole, so a field the editor does not know about is preserved.
- The editor's "Advanced" tab edits fields whose meaning is not confirmed as raw values; changing them is at the user's risk.
- Texture paths must be game paths. A path that is an editor marker or a machine-local path is dropped at export (the export checks all five), see [Export pipeline](../Systems/ExportPipeline.md).