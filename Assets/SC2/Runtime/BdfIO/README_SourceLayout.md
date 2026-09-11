# BdfIO Unity Source Layout

This folder contains BdfIO as Unity source scripts, not as prebuilt DLLs.

## Runtime assemblies

- `Core/` -> `BdfIO.Core`
- `Formats/` -> `BdfIO.Formats`, with an asmdef reference to `BdfIO.Core`

The namespaces and public API are kept from the .NET projects where possible. Unity code can use:

```csharp
using BdfIO.Core;
using BdfIO.Formats.Hfield;
using BdfIO.Formats.Terrain;
```

## What is copied

- `BdfIO.Core/*.cs`
- `BdfIO.Formats/*.cs`
- `BdfIO.Formats/Collision2/*.cs`
- `BdfIO.Formats/Costs/*.cs`
- `BdfIO.Formats/Decals/*.cs`
- `BdfIO.Formats/Hfield/*.cs`
- `BdfIO.Formats/Info/*.cs`
- `BdfIO.Formats/MapObjs/*.cs`
- `BdfIO.Formats/Terrain/*.cs`

## What is intentionally not copied

- `bin/`
- `obj/`
- `*.csproj`
- `*.sln`
- CLI tools
- test projects

Unity should compile the parser/writer directly from these scripts without building `BdfIO.Core.dll` or `BdfIO.Formats.dll` first.

## Unity C# compatibility

The Unity copy is adapted to compile as source with C# 9:

- regular namespace blocks instead of file-scoped namespaces;
- no `required` members;
- no `init` accessors;
- no primary constructors;
- no record structs;
- no C# 12 collection expressions.

The original namespaces and public parser/writer entry points are preserved where possible.
