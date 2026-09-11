# KopiLua — changes from upstream

This folder vendors [KopiLua](https://github.com/Myndale/KopiLua) (a
line-for-line C# port of the Lua 5.1.4/5.1.5 reference implementation:
parser, bytecode compiler/dumper, VM, loader, and standard libraries),
patched to read SupCom2's compiled `scenario.lua` / `_save.lua` /
`_script.lua` files and the engine's own compiled `.lua` scripts.

- **Upstream**: `https://github.com/Myndale/KopiLua.git`
- **Commit vendored**: `88b9267c15f409a7b2430bedcc0fb7d27f5de20f` (2019-03-26)
- **License**: MIT (see [COPYRIGHT](COPYRIGHT), copied unmodified from upstream)
- **Files ported**: all of `KopiLua/src/*.cs` and `KopiLua/src/printf/Tools.cs`,
  **except** `Program.cs`, `Tools.Tests.cs`, `AssemblyInfo.cs`, `luac.cs`
  (console-app entry points and a standalone test harness — not part of the
  library surface this port needs).

Everything below is a real, executed patch — not a plan. Each category was
verified by batch-loading and executing real vanilla SupCom2 `.lua` files
(see the validation section at the end).

## 1. `lua_Number` = `float`, not `double` (the reason this port exists)

Raw byte inspection of real compiled `scenario.lua` files
(`\x1bLua\x51\x00\x01\x04\x04\x04\x04\x00\x42...`) shows the Lua 5.1 bytecode
header's `sizeof(lua_Number)` field is `4`, not `8`. SupCom2's engine was
built with `LUA_NUMBER=float`, the non-default single-precision option Lua
5.1 supports, instead of the standard 8-byte `double`. The bytecode loader
(`lundump.cs` → `LoadHeader`) does a strict `memcmp` of the file's header
against the current build's own `luaU_header()` output, which encodes
`sizeof(lua_Number)` — so this mismatch is fatal ("bad header") unless fixed.

**Fix**: in every file that declares a local `lua_Number`/`LUA_NUMBER` type
alias, changed `System.Double` (or the bare `Double`) to `System.Single` (or
`Single`). Each file declares its own alias independently (KopiLua has no
central numeric-type header), so this is 18 separate one-line edits, not one
central change:

`lapi.cs`, `lauxlib.cs`, `lbaselib.cs`, `lcode.cs`, `ldump.cs`, `liolib.cs`,
`llex.cs`, `llimits.cs`, `lmathlib.cs`, `lobject.cs`, `loslib.cs`,
`lparser.cs`, `ltable.cs`, `ltablib.cs`, `lua.cs`, `luaconf.cs`,
`lundump.cs`, `lvm.cs`.

Example (`luaconf.cs`):

```diff
- using LUA_NUMBER	= System.Double;
+ using LUA_NUMBER	= System.Single;
  ...
- using lua_Number = System.Double;
+ using lua_Number = System.Single;
```

## 2. Mechanical double→float cast fixes (fallout of #1)

Once `lua_Number` became `float`, ~15 call sites that fed a `double`-typed
result (from `System.Math.*`, or KopiLua's own `frexp`/`ldexp`/`modf`/`fmod`
helpers, or the `PI`/`HUGE_VAL` constants, which are still typed `double`)
into a `lua_Number`-typed parameter or field stopped compiling. All fixed
with an explicit `(float)`/`(lua_Number)` cast at the call site — no logic
changes, purely making the existing double-precision math produce a
single-precision result at the boundary.

- **`lmathlib.cs`**: every `lua_pushnumber(L, Math.X(...))` in `math_abs`,
  `math_sin`, `math_sinh`, `math_cos`, `math_cosh`, `math_tan`, `math_tanh`,
  `math_asin`, `math_acos`, `math_atan`, `math_atan2`, `math_ceil`,
  `math_floor`, `math_sqrt`, `math_pow`, `math_log`, `math_log10`,
  `math_exp`; `math_deg`/`math_rad`'s division/multiplication result;
  `math_frexp`/`math_ldexp`'s `frexp`/`ldexp` results; `math_modf`'s `ip`/`fp`
  locals (changed from `double` to `float`); `math_random`'s two
  `Math.Floor(...)` results; `luaopen_math`'s `PI`/`HUGE_VAL` pushes.
- **`lobject.cs`**: `luaO_str2d`'s `lua_str2number(...)` return (was
  implicitly a `double`); `l_uacNumber`-to-`lua_Number` vararg cast in the
  internal printf-style formatter (`case 'f':` branch).
- **`luaconf.cs`**: `luai_nummod`/`luai_numpow` (the two `luai_num*`
  primitive-operation helpers actually used at runtime — `+`, `-`, `*`, `/`
  don't need a cast since they stay in `lua_Number` throughout, but `%` and
  `^` route through `Math.Floor`/`Math.Pow`, which are `double`-typed);
  `modf(lua_Number, out lua_Number)`'s two `Math.Floor(a)` calls.
- **`liolib.cs`**: `read_number`'s `parms[0]` cast (was `(double)parms[0]`,
  now `(lua_Number)(double)parms[0]`, since `parms` is populated by a
  `double`-typed `fscanf` call regardless of `lua_Number`'s width).

None of this changes behavior beyond the intended precision drop from
double to float — every cast narrows a value that was always going to be
stored in a 4-byte `lua_Number` field or passed to `lua_pushnumber`, which
already truncates to `lua_Number`'s width.

## 3. Compile-time constants baked in permanently

Upstream's own `KopiLua.csproj` sets
`<DefineConstants>LUA_CORE;_WIN32;LUA_COMPAT_VARARG;LUA_COMPAT_MOD;LUA_COMPAT_GFIND;CATCH_EXCEPTIONS</DefineConstants>`
— i.e. these six symbols are meant to always be defined when building
KopiLua as a library; they are not optional feature flags upstream expects
consumers to toggle. Unity `.asmdef` assemblies have no per-assembly
`DefineConstants` equivalent (global `scripting define symbols` would leak
into every other assembly in the project), so instead of relying on an
external define, the six `#if X` / `#endif` guards were removed and their
bodies made unconditional — reproducing exactly the build configuration
that was validated against real game files, permanently, with no external
switch to get wrong:

- **`LUA_CORE`** (`luaconf.cs`) — gates `op_delegate` and the seven
  `luai_num*` primitive arithmetic helpers (`luai_numadd`/`sub`/`mul`/`div`/
  `mod`/`pow`/`unm`/`eq`/`lt`/`le`/`isnan`). This is the one symbol that is
  a genuine compile blocker without it (no `#else` branch exists) — the VM
  calls these helpers directly, so leaving them undefined breaks the build,
  not just a feature.
- **`CATCH_EXCEPTIONS`** (`ldo.cs`, `luaD_rawrunprotected`) — wraps the
  interpreter's protected-call dispatch in a real C# `try`/`catch` so a
  runtime error surfaces as a Lua error code (what `lua_pcall` returns)
  instead of an uncaught .NET exception. Needed for `lua_pcall` to behave
  like the C API contract this project relies on.
- **`LUA_COMPAT_VARARG`** (`ldo.cs`, `lparser.cs`) — old-style `...`/`arg`
  vararg compatibility, matching Lua 5.0-era scripts. Harmless to always
  enable; SupCom2's Lua 5.1 bytecode doesn't depend on it either way.
- **`LUA_COMPAT_MOD`** (`lmathlib.cs`) — registers `math.mod` as an alias
  for `math.fmod`. Additive; no downside to always having it.
- **`LUA_COMPAT_GFIND`** (`lstrlib.cs`) — registers `string.gfind` as an
  alias for `string.gmatch`. Same, additive.
- **`_WIN32`** (`luaconf.cs`, two blocks) — picks Windows-style
  `LUA_PATH_DEFAULT`/`LUA_DIRSEP` constants over the Unix-style `#else`
  branch. These constants are only consulted by `require`/module path
  resolution, which this port doesn't use (SupCom2 doesn't use Lua's
  built-in `require`; `import()` is the engine's own C++-side loader, not
  part of KopiLua). Kept the `_WIN32` branch to exactly match what the
  validated spike build used; functionally inert either way for this port.

## 4. Culture-invariant number lexing (`luaconf.cs`, `lua_str2number`)

`lua_str2number` (used by both the lexer, for numeric literals in text
source, and the `tonumber()` base-library builtin at runtime) parsed the
accumulated digit string with `Convert.ToDouble(str.ToString())` — no
`IFormatProvider`, so it used `CultureInfo.CurrentCulture`. On any machine
whose locale uses `,` as the decimal separator (e.g. `ru-RU` — the locale
this port was developed and tested on), parsing a perfectly valid Lua
numeric literal like `12.5` throws a `FormatException`; the surrounding
`catch` swallows it, resets the cursor to "0 characters consumed", and the
lexer reports a bogus `malformed number` error — or, if hit via `tonumber()`
inside a running script, a Lua runtime error instead of a number.

This is a genuine, pre-existing upstream bug, not something introduced by
this port's other patches — but it was *found* by this port, via a
real, confusing symptom: a hand-written test fixture containing
`norushradius = 12.5` failed to load with "malformed number near '12.5'",
which led straight back to this line. It also retroactively explains the
one `*_scenario.lua` "genuine lexer bug" this port's own earlier validation
notes attributed to a specific file (`SC2_MP_001`, "malformed number near
'65.000000'") — that failure was never about that file; it was this same
locale bug, and disappears with this fix (see the corrected validation
table below).

**Fix**: `Convert.ToDouble(str, CultureInfo.InvariantCulture)` — Lua source
syntax always uses `.` as the decimal point regardless of host locale, so
the parse must be culture-invariant, never locale-dependent. One line
changed in `luaconf.cs`, plus a `using System.Globalization;` added.

## 5. Unrelated .NET-version incompatibility fix

`lstrlib.cs`'s `writer()` (used only by `string.dump()`, i.e. serializing a
Lua *function* back into bytecode — the write path, not the read path this
port needs) uses `System.Runtime.Serialization.Formatters.Binary.BinaryFormatter`,
which recent .NET SDKs flag as obsolete (`SYSLIB0011`) and, depending on
project settings, can treat as a build error. Since this code path is never
exercised by loading/reading scenario data, it was left in place (not
deleted, to stay a faithful, minimal-diff port) but wrapped with
`#pragma warning disable SYSLIB0011` / `#pragma warning restore SYSLIB0011`
around just the `BinaryFormatter` usage, so the assembly compiles cleanly
regardless of the host project's obsolete-API warning level.

## 6. What was deliberately left untouched

- No new public API surface was added on top of KopiLua's C-API-mirroring
  static methods (`lua_open`, `luaL_loadbuffer`, `lua_pcall`,
  `lua_getglobal`, `lua_next`, etc.) — this is a straight port, not a
  wrapper. A higher-level `SC2ScenarioLegacyBridge`-style reader belongs in
  `SC2.Core`, not in this vendored folder.
- `import()` and the ~30 SupCom2 engine-native Lua globals real
  `_script.lua`/AI scripts call (`ArmyBrains`, `ForkThread`, `WaitSeconds`,
  `categories.*`, `CreateAttachedEmitter`, ...) are **not** stubbed here.
  They don't exist outside the running game engine and are out of scope:
  this port's job is parsing/executing the *data* files (`scenario.lua`,
  `_save.lua`), not simulating SupCom2's C++ runtime. See the Validation
  section below for exactly how far real `_script.lua` files get without
  them.

## Validation

Before this port, the exact same patch set was built and run as a
standalone spike (`C:\Temp\KopiLuaSpike\`, outside this repo) against three
real corpora extracted from vanilla SupCom2 game data, using
`luaL_loadbuffer` (bytecode parsing) and `lua_pcall` (execution) with small
identity/table-building stub globals for the `STRING`/`FLOAT`/`INTEGER`/
`BOOLEAN`/`VECTOR3`/`QUATERNION`/`RECTANGLE`/`GROUP` wrapper functions the
data files call. Numbers below are AFTER the §4 culture fix — see that
section for what changed and why it mattered for two of these three
corpora, not just the one file it was first found on:

| Corpus | Files | `luaL_loadbuffer` OK | `lua_pcall` OK |
|---|---|---|---|
| `*_scenario.lua` (per-map metadata) | 56 | **56/56 (100%)** | **56/56 (100%)** |
| `gamedata.scd/lua/**/*.lua` (engine/AI scripts) | 386 | 385/386 (99.7%) | 202/385 (52.5%, up from 125 pre-fix — rest blocked by real engine-native globals, expected, see §6) |
| `*_save.lua` + `*_script.lua` (per-map layout + mission logic) | 112 | **112/112 (100%)** | 92/112 (up from 91 pre-fix — `_script.lua`'s remaining gap is `import()`+engine globals, expected, see §6) |

The one remaining load failure (`sim\aibrain.lua` in the engine/AI corpus)
is a genuine, unrelated syntax error — `'then' expected near '!'` — an
actual malformed-Lua issue in that one file, not a locale or format
problem. Before the §4 fix, this table showed one additional "load failure"
each in the `*_scenario.lua` and `*_save.lua`/`*_script.lua` rows
(`SC2_MP_001`, both times) that this port's own earlier notes mischaracterized
as "a genuine lexer bug in that one file" — it wasn't about that file at
all; every one of those was the same §4 locale bug, and all disappeared
once it was fixed. `pcall` counts for the `lua/` engine-script corpus rose
substantially too (125→202) even though none of those bytecode files needed
re-*loading* — `tonumber()` is a Lua base-library builtin that also calls
the buggy code path at runtime, so real scripts calling it on a numeric
string were failing execution, not just files with numeric literals in
their own source text.

Deep field-level verification against `SC2_CA_C01_save.lua`'s known-correct
uncompiled source confirmed byte-perfect (within expected float32 rounding)
extraction of nested `Areas`/`rectangle` and `MasterChain`/`Markers`/
`position` data through the patched `RECTANGLE`/`VECTOR3` stubs.

This exact patched source (byte-identical, re-diffed against the spike
after copying) was rebuilt in a **separate** verification project with the
compile-time constants baked in (§3) and *no* externally-supplied
`DefineConstants`/`AllowUnsafeBlocks` — i.e. exactly the configuration Unity
will actually compile — and re-run against the `*_scenario.lua` corpus with
identical results, confirming the bake-in introduced no behavior change.

## Verified inside the real Unity Editor

`SC2.Core.asmdef` now references `SC2.ThirdParty.KopiLua`. This was verified
with a real Unity 6000.0.77f1 batch-mode compile (`Unity.exe -batchmode
-nographics -quit -projectPath ... -logFile ...`) run against a throwaway
scratch project assembled from the live dev project's `ProjectSettings/` +
`Packages/` plus this repo's `Assets/SC2` — not the live project itself, so
nothing there was touched. The build log shows Unity's real Roslyn compiler
(`Csc`) building `SC2.ThirdParty.KopiLua.dll` and then `SC2.Core.dll` (which
references it), followed by `CopyFiles` placing both into
`Library/ScriptAssemblies/` — Unity's build pipeline only reaches
`CopyFiles` after a `Csc` step succeeds, so this is a genuine, zero-error
compile, not an assumption. No `error CS` lines anywhere in the log.

## `SC2ScenarioDocument`/`SC2ScenarioLegacyBridge` now consume this library

`Scripts/MapDocument/Domain/Scenario/SC2ScenarioLegacyBridge.cs` loads a
`_scenario.lua` blob through this library and reads the `ScenarioInfo`
table's scalar fields via the Lua C API — see
`SC2MapDocument_Etap4_Scenario_KopiLuaPort_Status.md` for its own status.
Its standalone tests (`ScenarioDocumentBridgeTests.cs`) exercise the full
real `*_scenario.lua` corpus (56/56 now pass, including the map that
originally surfaced the §4 culture bug) plus a synthetic fixture, and are
what re-surfaced and confirmed the §4 fix in the first place.

## Not yet done

- Only compilation was verified in the Unity Editor, not runtime behavior —
  the real corpus batch tests were run in the standalone spike/verification
  builds (plain .NET), not inside a running Unity process. A
  `lua_open`/`luaL_loadbuffer` smoke test from inside an actual Unity
  EditMode test has not been done yet.
