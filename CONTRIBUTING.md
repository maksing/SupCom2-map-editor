# Contributing

This is a short guide; the three documents it links to hold the detail.

- [Editing](Docs/Process/Editing.md): how to change the code safely, and the rules the project keeps.
- [Testing](Docs/Process/Testing.md): which test goes where, how to run the tests, what a change must test.
- [Verification](Docs/Process/Verification.md): how to check a change beyond the tests, and how to report it.
- [Orientation](Docs/Orientation/README.md): which route is live and which is legacy, the big files, where the data is authoritative, the known risks. Read it before your first change.
- [The documentation index](Docs/README.md): every system, every game file format and how the systems interact; [Change impact](Docs/Interactions/ChangeImpact.md) says what else to check for each kind of change.
- [The documentation plan](Docs/DocumentationPlan.md): how the documentation is organised and kept true.

## The rule that matters most

**Writing a test is mandatory for every change of behaviour.** A bug fix starts with a test that fails and ends with it passing. When a
unit test is impossible (interface layout, a shader, behaviour that exists only in a build) a reproducible scenario takes its place, as
described in [Verification](Docs/Process/Verification.md). A change without its test is not finished.

## Before you open a pull request

1. Read the document of the system you changed and keep it true; look the change up in [Change impact](Docs/Interactions/ChangeImpact.md) and check what depends on it.
2. Run `dotnet test` in `Assets/SC2/Tests~/StandaloneCharacterization` and the EditMode tests; compare with the known baseline in
   [Testing](Docs/Process/Testing.md).
3. Compile a player build as well as the editor.
4. Fill in the pull request checklist (`.github/pull_request_template.md`), including what you verified and what you did not.

## The short list of project rules

- Comments are in English and state the durable reason; no dates, no history of how the change came about.
- One change per commit; no attribution trailers; stage explicit paths.
- Never commit the game's textures, and never write into a user's save folder from a test or a scenario.
- A new asset file needs a `.meta` file with a new guid.

## Build and layout

See the [README](README.md): Unity 6000.0.77f1, the project layout, how the standalone sub-projects are built.
