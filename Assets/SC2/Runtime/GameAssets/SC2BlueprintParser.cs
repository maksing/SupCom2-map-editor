using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

/// <summary>
/// Read-only parser for the Lua-table subset used by SC2 unit and prop blueprints.
/// It intentionally does not execute blueprint source.
/// </summary>
public static class SC2BlueprintParser
{
    public static SC2Blueprint ParseFile(string path) => Parse(File.ReadAllText(path), path);

    public static SC2Blueprint Parse(string source, string sourcePath = null)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        var parser = new Parser(source);
        string declaration = parser.ReadOptionalDeclaration();
        SC2BlueprintValue root = parser.ReadValue();
        if (root.Kind != SC2BlueprintValueKind.Table)
            throw new FormatException("Blueprint root must be a Lua table.");
        return new SC2Blueprint(declaration, sourcePath, root);
    }

    private sealed class Parser
    {
        private readonly Lexer lexer;
        private Token current;

        public Parser(string source)
        {
            lexer = new Lexer(source);
            current = lexer.Next();
        }

        public string ReadOptionalDeclaration()
        {
            if (current.Kind == TokenKind.Identifier && !string.Equals(current.Text, "return", StringComparison.Ordinal))
            {
                Token possibleName = current;
                Next();
                if (current.Kind == TokenKind.LeftBrace) return possibleName.Text;
                throw Error("Expected blueprint table after declaration.");
            }
            if (IsIdentifier("return")) Next();
            return null;
        }

        public SC2BlueprintValue ReadValue()
        {
            switch (current.Kind)
            {
                case TokenKind.LeftBrace: return ReadTable();
                case TokenKind.String: return Take(SC2BlueprintValue.String(current.Text));
                case TokenKind.Number:
                    double value;
                    if (!double.TryParse(current.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        throw Error("Invalid number '" + current.Text + "'.");
                    return Take(SC2BlueprintValue.Number(value));
                case TokenKind.Identifier:
                    if (IsIdentifier("true")) return Take(SC2BlueprintValue.Boolean(true));
                    if (IsIdentifier("false")) return Take(SC2BlueprintValue.Boolean(false));
                    if (IsIdentifier("nil")) return Take(SC2BlueprintValue.Nil());
                    // A few blueprint values use unquoted enum identifiers. Preserve them as strings.
                    return Take(SC2BlueprintValue.String(current.Text));
                default: throw Error("Expected a blueprint value.");
            }
        }

        private SC2BlueprintValue ReadTable()
        {
            Expect(TokenKind.LeftBrace);
            var table = new SC2BlueprintTable();
            while (current.Kind != TokenKind.RightBrace)
            {
                if (current.Kind == TokenKind.End) throw Error("Unterminated blueprint table.");
                if (current.Kind == TokenKind.Comma || current.Kind == TokenKind.Semicolon) { Next(); continue; }

                if (current.Kind == TokenKind.LeftBracket)
                {
                    Next();
                    SC2BlueprintValue key = ReadValue();
                    Expect(TokenKind.RightBracket);
                    Expect(TokenKind.Equals);
                    table.Fields[key.AsString()] = ReadValue();
                }
                else if (current.Kind == TokenKind.Identifier)
                {
                    Token name = current;
                    Next();
                    if (current.Kind == TokenKind.Equals)
                    {
                        Next();
                        table.Fields[name.Text] = ReadValue();
                    }
                    else
                    {
                        table.Items.Add(SC2BlueprintValue.String(name.Text));
                    }
                }
                else
                {
                    table.Items.Add(ReadValue());
                }
            }
            Expect(TokenKind.RightBrace);
            return SC2BlueprintValue.Table(table);
        }

        private SC2BlueprintValue Take(SC2BlueprintValue value) { Next(); return value; }
        private bool IsIdentifier(string text) => current.Kind == TokenKind.Identifier && string.Equals(current.Text, text, StringComparison.Ordinal);
        private void Expect(TokenKind kind) { if (current.Kind != kind) throw Error("Expected " + kind + "."); Next(); }
        private void Next() { current = lexer.Next(); }
        private FormatException Error(string text) => new FormatException(text + " Line " + current.Line + ", column " + current.Column + ".");
    }

    private enum TokenKind { End, Identifier, String, Number, LeftBrace, RightBrace, LeftBracket, RightBracket, Equals, Comma, Semicolon }
    private struct Token { public TokenKind Kind; public string Text; public int Line; public int Column; }

    private sealed class Lexer
    {
        private readonly string text; private int index; private int line = 1; private int column = 1;
        public Lexer(string text) { this.text = text; }
        public Token Next()
        {
            SkipWhitespaceAndComments();
            var token = new Token { Line = line, Column = column };
            if (index >= text.Length) { token.Kind = TokenKind.End; return token; }
            char c = text[index];
            if (char.IsLetter(c) || c == '_') { token.Kind = TokenKind.Identifier; token.Text = ReadWhile(ch => char.IsLetterOrDigit(ch) || ch == '_'); return token; }
            if (char.IsDigit(c) || c == '-' || c == '+') { token.Kind = TokenKind.Number; token.Text = ReadWhile(ch => char.IsDigit(ch) || ch == '.' || ch == '-' || ch == '+' || ch == 'e' || ch == 'E'); return token; }
            if (c == '\'' || c == '"') { token.Kind = TokenKind.String; token.Text = ReadString(c); return token; }
            index++; column++;
            switch (c) { case '{': token.Kind = TokenKind.LeftBrace; break; case '}': token.Kind = TokenKind.RightBrace; break; case '[': token.Kind = TokenKind.LeftBracket; break; case ']': token.Kind = TokenKind.RightBracket; break; case '=': token.Kind = TokenKind.Equals; break; case ',': token.Kind = TokenKind.Comma; break; case ';': token.Kind = TokenKind.Semicolon; break; default: throw new FormatException("Unexpected character '" + c + "' at line " + token.Line + ", column " + token.Column + "."); }
            return token;
        }
        private void SkipWhitespaceAndComments()
        {
            while (index < text.Length)
            {
                if (char.IsWhiteSpace(text[index])) { Advance(); continue; }
                if (text[index] == '-' && index + 1 < text.Length && text[index + 1] == '-')
                {
                    Advance(); Advance();
                    if (index + 1 < text.Length && text[index] == '[' && text[index + 1] == '[')
                    { Advance(); Advance(); while (index + 1 < text.Length && !(text[index] == ']' && text[index + 1] == ']')) Advance(); if (index + 1 < text.Length) { Advance(); Advance(); } }
                    else while (index < text.Length && text[index] != '\n') Advance();
                    continue;
                }
                break;
            }
        }
        private string ReadWhile(Func<char, bool> predicate) { int start = index; while (index < text.Length && predicate(text[index])) Advance(); return text.Substring(start, index - start); }
        private string ReadString(char quote)
        {
            Advance(); var result = new System.Text.StringBuilder();
            while (index < text.Length && text[index] != quote)
            {
                if (text[index] == '\\' && index + 1 < text.Length) { Advance(); char escaped = text[index]; result.Append(escaped == 'n' ? '\n' : escaped == 't' ? '\t' : escaped); Advance(); }
                else { result.Append(text[index]); Advance(); }
            }
            if (index >= text.Length) throw new FormatException("Unterminated string at line " + line + ".");
            Advance(); return result.ToString();
        }
        private void Advance() { if (text[index++] == '\n') { line++; column = 1; } else column++; }
    }
}

public enum SC2BlueprintValueKind { Nil, String, Number, Boolean, Table }

public sealed class SC2BlueprintValue
{
    public SC2BlueprintValueKind Kind { get; private set; }
    public string StringValue { get; private set; }
    public double NumberValue { get; private set; }
    public bool BooleanValue { get; private set; }
    public SC2BlueprintTable TableValue { get; private set; }
    public static SC2BlueprintValue Nil() => new SC2BlueprintValue { Kind = SC2BlueprintValueKind.Nil };
    public static SC2BlueprintValue String(string value) => new SC2BlueprintValue { Kind = SC2BlueprintValueKind.String, StringValue = value };
    public static SC2BlueprintValue Number(double value) => new SC2BlueprintValue { Kind = SC2BlueprintValueKind.Number, NumberValue = value };
    public static SC2BlueprintValue Boolean(bool value) => new SC2BlueprintValue { Kind = SC2BlueprintValueKind.Boolean, BooleanValue = value };
    public static SC2BlueprintValue Table(SC2BlueprintTable value) => new SC2BlueprintValue { Kind = SC2BlueprintValueKind.Table, TableValue = value };
    public string AsString() => Kind == SC2BlueprintValueKind.String ? StringValue : Kind == SC2BlueprintValueKind.Number ? NumberValue.ToString(CultureInfo.InvariantCulture) : Kind == SC2BlueprintValueKind.Boolean ? BooleanValue.ToString().ToLowerInvariant() : string.Empty;
}

public sealed class SC2BlueprintTable
{
    public readonly Dictionary<string, SC2BlueprintValue> Fields = new Dictionary<string, SC2BlueprintValue>(StringComparer.OrdinalIgnoreCase);
    public readonly List<SC2BlueprintValue> Items = new List<SC2BlueprintValue>();
    public SC2BlueprintValue Get(string name) { SC2BlueprintValue value; return Fields.TryGetValue(name, out value) ? value : null; }
    public SC2BlueprintTable GetTable(string name) { var value = Get(name); return value != null && value.Kind == SC2BlueprintValueKind.Table ? value.TableValue : null; }
    public string GetString(string name) { var value = Get(name); return value == null ? null : value.AsString(); }
    public bool GetBool(string name, bool fallback = false) { var value = Get(name); return value != null && value.Kind == SC2BlueprintValueKind.Boolean ? value.BooleanValue : fallback; }
    public float GetSingle(string name, float fallback = 0f) { var value = Get(name); return value != null && value.Kind == SC2BlueprintValueKind.Number ? (float)value.NumberValue : fallback; }
}

public sealed class SC2Blueprint
{
    public string Declaration { get; private set; }
    public string SourcePath { get; private set; }
    public SC2BlueprintValue Root { get; private set; }
    public SC2BlueprintTable Table => Root.TableValue;
    public SC2Blueprint(string declaration, string sourcePath, SC2BlueprintValue root) { Declaration = declaration; SourcePath = sourcePath; Root = root; }
}
