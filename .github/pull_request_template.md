## What changed and why

<!-- One paragraph: the behaviour that changed and the reason, not the history of how it came about. -->

## Checklist

- [ ] The document of the system was read; its invariants still hold, or the document is updated in this change.
- [ ] The rows of `Docs/Interactions/ChangeImpact.md` that match this change were checked (what depends on it), or: no other system depends on it.
- [ ] A test was added or changed for the behaviour (unit, EditMode or scenario). A bug fix has a test that failed before the fix.
- [ ] `dotnet test` and the EditMode tests were run, and the result matches the known baseline (any other failure is explained).
- [ ] The editor **and** a player build compile.
- [ ] Saved data: a save and load round trip and a save made before the change were tested (or: no saved data changed).
- [ ] Export: the written files were read back and scanned for editor markers and local paths (or: export not touched).
- [ ] Only my own changes are staged; no game textures, no user data, no files Unity rewrites on its own.

## Verification

```
Verified:
Not verified:
Evidence:
```
