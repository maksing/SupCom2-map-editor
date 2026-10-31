# Game assets: blueprints, models, units, props, markers and effects

Status: first draft from reading the code at commit `17863e4`; not yet reviewed by a second reader. Priority P2. Read [Architecture](../Architecture.md) and [Objects](Objects.md) first.

## 1. Purpose and boundaries

Reads the game's own units, props, particle effects and sound events out of the install, and draws them in the viewport: blueprints (`.bp`) and model files (`.scm`), the unit and prop browser, instanced drawing of placed units, props and markers, a model preview, the effect preview of Effect markers, and the catalogs of effect templates and sound events that marker fields refer to.

Not included: what an object is in the map and how it is saved ([objects](Objects.md)), how a texture is loaded ([textures](Textures.md)), the game archive reader ([import pipeline](ImportPipeline.md), `SC2SourceContainer`).

## 2. Where the code is

| Part | Files |
|---|---|
| Parsing (no Unity) | `Runtime/GameAssets/` (assembly `SC2.GameAssets`): `SC2BlueprintLoader.cs` (text or Lua 5.1 bytecode), `SC2BlueprintParser.cs` (read-only parser of the blueprint's Lua-table subset), `SC2ScmModelReader.cs` (SCM v7 and v8, material ranges), `SC2GameAssetCatalog.cs` and `SC2GameAssetDiscovery.cs` (the index), `SC2EmitterBlueprint.cs` and `SC2EmitterBlueprintReader.cs` (particle emitters), `SC2EffectTemplateNames.cs`, `SC2FevEventNames.cs` (FMOD event files) |
| Unity side | `Scripts/GameAssets/` (assembly `SC2.GameAssets.Unity`): `SC2UnitMaterialFactory.cs` (one `SC2/Unit` material per blueprint material, `SC2UnitTextureResolver`), `SC2ParticleEmitter.cs` (a simplified emitter run) |
| Browser and pickers | `Scripts/Unified/SC2UnitPropBrowser.cs`, `SC2PropModelPreview.cs`, `SC2EffectMarkerPicker.cs` (`SC2EffectTemplateCatalog`), `SC2SoundEventPicker.cs` (`SC2SoundEventCatalog`) |
| Viewport drawing | `Scripts/Unified/SC2GameAssetScenePreview.cs` (units, props and reference units), `SC2MarkerScenePreview.cs` (marker models), `SC2ObjectMarkerRenderer.cs` (pick dots), `SC2EffectPreview.cs`, `SC2SelectionOutline.cs` |
| Shaders | `SC2Unit`, `SC2MarkerModel`, `SC2ObjectMarker`, `SC2EffectParticle`, `SC2SelectionOutline` ([shaders](Shaders.md)) |
| Tests | `GameAssetParsingTests`, `EffectTemplateNamesTests`, `FevEventNamesTests`, `ParticleEmitterTests`, `PropFacingTests`, `PropHeightmapTests`, `PropRenderAttributesTests`, `PropDestructableTests` |

## 3. Data

- `SC2Blueprint`: the parsed table of one `.bp` (values are nil, string, number, boolean or table). Both shipped forms are read: development text, and Lua 5.1 bytecode evaluated in a minimal Lua state that exposes only the two blueprint declaration functions; the source is never executed.
- `SC2GameAsset` (`Id` is the lowercase blueprint code such as `uua0101`), with `SC2GameAssetLod`, `SC2GameAssetMaterialSet` and `SC2GameAssetMaterial` (the texture fields `DiffuseName`, `NormalName`, `MaskName`, `TeamName`). A unit's or prop's LODs resolve to SCM files and materials to texture paths; a standard team-colour material without explicit texture names falls back to the conventional sibling texture names of its SCM.
- `SC2ScmModel`: bones, vertices (position, tangent, normal, binormal and UVs), triangles and **material ranges** (name, face count, first index, vertex start and count); blueprint materials and SCM ranges map by their declared order.
- `SC2EmitterBlueprint` with `SC2EmitterCurve`s (animated values: `y` is the value at the key's `x` along the emitter clock, `z` the random width centred on `y`).
- The catalog comes from the **game archives** through the shared reader (`BuildFromArchive` takes entry paths and a byte-reader delegate, so the parsing assembly does not reference the document assembly) or from loose folders. Nothing is bundled.

## 4. Flows

- **Browsing**: the browser indexes blueprints **off the main thread** and loads only visible DDS thumbnails on demand, so opening it never instantiates hundreds of meshes or textures. Picking arms placement in the Objects or Reference tab.
- **Drawing placed units and props**: `SC2GameAssetScenePreview` batches by blueprint id and draws every instance of a blueprint with one `Graphics.DrawMeshInstanced` (shared mesh and materials, a per-instance matrix) instead of a GameObject and renderer per instance; `DrawAllBatches()` must be called once per rendered frame (from the game view's `LateUpdate`). Markers do the same, batched by icon key (27 types at most), and pick dots are one shared quad per category batch,
  chunked at the 1023-instance limit of an instanced draw.
- **Marker models** come from `Resources/MarkerModels/<IconKey>.fbx`; every instance carries the marker's Z mirror, which an instanced draw does not correct, so the marker shader culls front faces by default.
- **Effect preview**: for each Effect marker, the emitters of its template (`EffectTemplates.Marker` of the game's `lua/sim/EffectTemplates.lua`, with the DLC1 copy winning on a name clash; compiled and uncompiled forms both understood) are simulated by `SC2ParticleEmitter` and drawn as textured quads; colour is the particle texture times a ramp texture read by life and ramp row, as in the game's particle shader. Time is in game ticks of 0.1 s; it is **approximate by design**, showing look and size, not frame-exact motion. At most 20 000 particles total.
- **Sound events**: `SC2SoundEventCatalog` lists the events of the install's FMOD files (`sounds/win/fmod/*.fev`, format FEV1) whose path starts with `SC2/SC2/Environments/`, the form a Sound marker stores. The reader walks the header, the wave bank list, the category tree and the nested event groups; an event record's body is skipped because its length varies between versions.
- **Selection outline** of an imported terrain object is a command buffer on the game camera: the renderers are drawn flat into a mask and the pixels just outside the mask get the outline colour.

## 5. Invariants

- **Instanced drawing needs instancing variants in the player.** Unit and prop materials exist only at runtime (`new Material(shader)` of `SC2/Unit`), no asset references them with instancing on, so Unity's build-time variant stripping would remove `INSTANCING_ON`; the draw call then fires with correct data and draws nothing, with no error anywhere in C#.
  `ProjectSettings/GraphicsSettings.asset` therefore has `m_InstancingStripping: 2` ("Keep All"); do not change it back. Terrain and water are ordinary renderers and are not affected. Details: [Project and build settings](ProjectAndBuildSettings.md).
- **Shaders used only at runtime live under `Resources/`** and are loaded by name with `Resources.Load<Shader>` (or a fallback), never found only through `Shader.Find`, which works in the editor and fails in a build when nothing references the shader.
- **SCM winding is not consistent** (roughly half and half relative to each triangle's own normal on the vanilla corpus), so the unit shader renders both sides; culling would punch holes.
- **A blueprint is parsed, never executed.** The parser reads the Lua-table subset it needs.
- **The browser never loads meshes it has not been asked for.**
- **An effect preview is a visual aid**, not an authority on how the effect behaves in the game.
- **Reference units, units and props share the instance key space** (`InstanceKey(true, id)`), which is why their ids come from one counter.

## 6. Editor and build differences

- The parsing assemblies are plain C# and run anywhere. Rendering is the same code in both; the only difference that matters is the shader and variant stripping above. Archive access depends on the configured install ([textures](Textures.md), [import pipeline](ImportPipeline.md)); with no install the browser is empty.

## 7. Extension points and how to change it

- **A new blueprint field the editor needs**: read it from the parsed `SC2Blueprint` (see `SC2ReferenceUnitIntelReader` for the idiom), never by executing Lua.
- **A new viewport instance type**: a batch in the preview class with its own key, and a shader that handles instancing and any mirroring.
- **A new effect parameter**: a curve name in the emitter reader and its use in `SC2ParticleEmitter`.

## 8. Editing, verification and tests

**How to edit it safely.** After a rendering change, check the result in a **player build**, not only in the editor (see the stripping trap). Keep per-frame work cheap: batches are rebuilt only when their input changes.

**Tests today**: the standalone tests above cover blueprint, emitter, event-name and effect-template parsing and the prop rules. The preview classes, the browser and the pickers have no unit test.

**Tests a change must add.** A parser change: a case with a small blueprint, bytecode and text. A prop rule: a case in the matching `Prop*Tests`. A drawing change: a player scenario that places an object and reads back that the instanced draw produced pixels.

## 9. Known limitations and unverified behaviour

- The shaders' agreement with the game's rendering was not verified in the game.
- The set of vanilla blueprints that parse cleanly was not re-run for this document.

## 10. Related documents and review

[Objects](Objects.md), [Reference units](ReferenceUnits.md), [Shaders](Shaders.md), [Textures](Textures.md), [Editor vs build](EditorVsBuild.md), [Project and build settings](ProjectAndBuildSettings.md).
Interactions with other systems: [Objects and scenario](../Interactions/ObjectsAndScenario.md), [Change impact](../Interactions/ChangeImpact.md).
Last reviewed: author, code reading, commit `17863e4`. Second reader: not yet.
