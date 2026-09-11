// SC2LuaBytecodeValue.cs
// Minimal generic Lua value tree - the bytecode-execution counterpart of
// SC2LuaField/SC2LuaValue's text-based world. See SC2SaveLuaBytecodeReader
// for how a real _save.lua/_scenario.lua bytecode file gets turned into
// one of these.

using System.Collections.Generic;

public enum SC2LuaBytecodeValueKind { Nil, Boolean, Number, String, Table }

public sealed class SC2LuaBytecodeValue
{
    public SC2LuaBytecodeValueKind Kind;
    public bool BoolValue;
    public double NumberValue;
    public string StringValue;
    public SC2LuaBytecodeTable TableValue;

    public static SC2LuaBytecodeValue Nil() => new() { Kind = SC2LuaBytecodeValueKind.Nil };
    public static SC2LuaBytecodeValue Bool(bool b) => new() { Kind = SC2LuaBytecodeValueKind.Boolean, BoolValue = b };
    public static SC2LuaBytecodeValue Num(double n) => new() { Kind = SC2LuaBytecodeValueKind.Number, NumberValue = n };
    public static SC2LuaBytecodeValue Str(string s) => new() { Kind = SC2LuaBytecodeValueKind.String, StringValue = s };
    public static SC2LuaBytecodeValue Tab(SC2LuaBytecodeTable t) => new() { Kind = SC2LuaBytecodeValueKind.Table, TableValue = t };
}

/// <summary>Items = the table's 1-based sequential array part (Lua's own
/// "#t" ipairs range); Fields = everything else, keyed by string (numeric
/// keys outside the sequential range are stringified, matching how this
/// project's other Lua tooling - SC2SaveLuaParser et al. - already treats
/// non-array keys).</summary>
public sealed class SC2LuaBytecodeTable
{
    public readonly List<SC2LuaBytecodeValue> Items = new();
    public readonly Dictionary<string, SC2LuaBytecodeValue> Fields = new(System.StringComparer.Ordinal);
}
