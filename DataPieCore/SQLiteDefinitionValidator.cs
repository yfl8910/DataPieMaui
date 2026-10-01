using System;
using System.Collections.Generic;
using System.Linq;
using DBUtil;

namespace DataPieCore;

// Delimit statements without interpreting semicolons inside literals, identifiers or comments.
public static class SQLiteDefinitionValidator
{
    internal static List<List<string>> Statements(string sql)
        => Statements(sql, out _);

    internal static List<List<string>> Statements(string sql, out List<string> text)
    {
        text = new();
        int statementStart = 0;
        var result = new List<List<string>>();
        var tokens = new List<string>();
        for (int i = 0; i < sql.Length;)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            { while (i < sql.Length && sql[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new ArgumentException("Unterminated SQL comment.");
                i = end + 2; continue;
            }
            if (c == ';') { if (tokens.Count > 0) { result.Add(tokens); text.Add(sql.Substring(statementStart, i - statementStart)); } tokens = new(); statementStart = ++i; continue; }
            int start = i++;
            if (c is '\'' or '"' or '`' or '[')
            {
                char close = c == '[' ? ']' : c;
                bool closed = false;
                while (i < sql.Length)
                {
                    if (sql[i++] != close) continue;
                    if (c != '[' && i < sql.Length && sql[i] == close) { i++; continue; }
                    closed = true; break;
                }
                if (!closed) throw new ArgumentException("Unterminated SQL literal or identifier.");
            }
            else if (char.IsLetterOrDigit(c) || c is '_' or '@' or ':' or '$' or '?')
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
            else if (i < sql.Length && new[] { "||", "<=", ">=", "!=", "<>", "==", "<<", ">>" }.Contains(sql.Substring(start, 2))) i++;
            tokens.Add(sql.Substring(start, i - start));
        }
        if (tokens.Count > 0) { result.Add(tokens); text.Add(sql.Substring(statementStart)); }
        if (result.Count == 0) throw new ArgumentException("SQL cannot be empty.");
        return result;
    }

    public static string Prepare(DefinitionObject target, string sql)
    {
        var statements = Statements(sql);
        if (target.Kind == "Procedure")
        {
            foreach (var statement in statements)
                if (!new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "REPLACE", "WITH" }.Contains(statement[0].ToUpperInvariant()))
                    throw new ArgumentException("Scripts support SELECT, INSERT, UPDATE, DELETE and WITH statements only. Schema, transaction and connection commands are not allowed.");
            return sql;
        }
        if (target.Kind != "View" || target.Schema != "main" || statements.Count != 1)
            throw new ArgumentException("Provide one CREATE VIEW statement for the selected main view.");
        var t = statements[0];
        if (t.Count < 5 || !t[0].Equals("CREATE", StringComparison.OrdinalIgnoreCase) || !t[1].Equals("VIEW", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provide CREATE VIEW without TEMP or IF NOT EXISTS.");
        int nameIndex = 2;
        if (t.Count > 4 && t[3] == ".")
        {
            if (!Identifier(t[2]).Equals("main", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only main views can be edited.");
            nameIndex = 4;
        }
        if (!Identifier(t[nameIndex]).Equals(target.Name, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The view name must match the selected object.");
        return sql;
    }

    private static string Identifier(string value)
        => value[0] switch
        {
            '[' => value.Substring(1, value.Length - 2),
            '"' or '`' or '\'' => value.Substring(1, value.Length - 2).Replace(new string(value[0], 2), value[0].ToString()),
            _ => value
        };
}
