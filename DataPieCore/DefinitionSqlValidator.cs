using System;
using System.Collections.Generic;
using System.Linq;
using DBUtil;

namespace DataPieCore;

// Declaration guard only. SQL Server validates the complete body in the save transaction.
public static class DefinitionSqlValidator
{
    // Export only: normalize the declaration to CREATE and the current catalog name.
    // Do not apply the interactive editor's conservative body restrictions.
    internal static string PrepareCreateForExport(DefinitionObject target, string sql, bool quotedIdentifier)
    {
        var tokens = Scan(sql, quotedIdentifier);
        int position = 0;
        if (tokens.Count == 0 || !(tokens[0].Is("CREATE") || tokens[0].Is("ALTER"))) throw new ArgumentException("Unrecognized module declaration.");
        position++;
        var replacements = new List<(int Start, int Length, string Value)> { (tokens[0].Start, tokens[0].Length, "CREATE") };
        if (position < tokens.Count && tokens[position].Is("OR"))
        {
            if (!tokens[0].Is("CREATE") || position + 1 >= tokens.Count || !tokens[position + 1].Is("ALTER")) throw new ArgumentException("Unrecognized module declaration.");
            foreach (var token in tokens.Skip(position).Take(2)) replacements.Add((token.Start, token.Length, new string(' ', token.Length)));
            position += 2;
        }
        if (position >= tokens.Count || !(target.Kind == "View" ? tokens[position].Is("VIEW") : tokens[position].Is("PROC") || tokens[position].Is("PROCEDURE"))) throw new ArgumentException("Unexpected module type.");
        position++;
        if (position >= tokens.Count || !tokens[position].Identifier) throw new ArgumentException("Module name missing.");
        int firstName = position++;
        if (position < tokens.Count && tokens[position].Symbol("."))
        {
            position++;
            if (position >= tokens.Count || !tokens[position].Identifier) throw new ArgumentException("Module name missing.");
            position++;
        }
        if (position >= tokens.Count || tokens[position].Symbol(".") || tokens[position].Symbol(";")) throw new ArgumentException("Multipart or numbered modules are not supported.");
        replacements.Add((tokens[firstName].Start, tokens[position - 1].Start + tokens[position - 1].Length - tokens[firstName].Start,
            SqlQueryBuilder.QuoteIdentifier(target.Schema, "SQLSERVER") + "." + SqlQueryBuilder.QuoteIdentifier(target.Name, "SQLSERVER")));
        foreach (var change in replacements.OrderByDescending(c => c.Start)) sql = sql.Remove(change.Start, change.Length).Insert(change.Start, change.Value);
        return sql;
    }
    internal static string PrepareCreateOrAlter(DefinitionObject target, string sql)
    {
        string altered = PrepareAlter(target, sql);
        var first = Scan(altered, true)[0];
        return altered.Substring(0, first.Start) + "CREATE OR ALTER" + altered.Substring(first.Start + first.Length);
    }
    private sealed record Token(string Text, int Start, int Length, bool Word, bool Identifier)
    {
        public bool Is(string keyword) => Word && Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);
        public bool Symbol(string value) => !Word && !Identifier && Text == value;
    }

    public static string PrepareAlter(DefinitionObject target, string sql, bool quotedIdentifier = true)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("Enter a definition.");
        var tokens = Scan(sql, quotedIdentifier);
        int position = 0;
        bool Take(string word)
        {
            if (position >= tokens.Count || !tokens[position].Is(word)) return false;
            position++; return true;
        }
        if (tokens.Count == 0) throw new ArgumentException("Enter a definition.");
        var first = tokens[0];
        if (Take("CREATE"))
        {
            if (Take("OR") && !Take("ALTER")) throw new ArgumentException("Expected CREATE OR ALTER.");
        }
        else if (!Take("ALTER")) throw new ArgumentException("Start with CREATE, ALTER or CREATE OR ALTER.");
        int declarationEnd = tokens[position - 1].Start + tokens[position - 1].Length;
        bool correctKind = target.Kind switch
        {
            "View" => Take("VIEW"),
            "Procedure" => Take("PROCEDURE") || Take("PROC"),
            _ => false
        };
        if (!correctKind) throw new ArgumentException("Only a definition of the selected object type can be saved.");
        if (position + 2 >= tokens.Count || !tokens[position].Identifier || !tokens[position + 1].Symbol(".") || !tokens[position + 2].Identifier ||
            tokens[position].Text != target.Schema || tokens[position + 2].Text != target.Name)
            throw new ArgumentException($"The definition must target the selected object: {target.DisplayName}. Include its schema and preserve the exact name.");
        position += 3;
        if (position >= tokens.Count || tokens[position].Symbol(".") || tokens[position].Symbol(";"))
            throw new ArgumentException("Multipart or numbered module names are not supported; include a definition body.");
        if (tokens.Any(t => t.Is("GO"))) throw new ArgumentException("Remove GO batch separators before saving.");
        // Conservative: quoted identifiers and strings are exempt; bare option keywords
        // are excluded throughout the module, including unusual header forms.
        if (tokens.Skip(position).Any(t => t.Is("ENCRYPTION") || t.Is("NATIVE_COMPILATION") || t.Is("EXTERNAL")))
            throw new ArgumentException("Encrypted, natively compiled and CLR definitions are not supported.");
        if (!tokens.Skip(position).Any(t => t.Is("AS"))) throw new ArgumentException("Include AS and a definition body.");
        if (target.Kind == "View") GuardView(tokens, position);
        // Procedure statements remain part of its body. Do not split or execute them.
        // Preserve comments, literals and body text; replace declaration keywords only.
        if (first.Is("ALTER")) return sql;
        if (declarationEnd == first.Start + first.Length)
            return sql.Remove(first.Start, first.Length).Insert(first.Start, "ALTER");
        // Keep comments between CREATE / OR / ALTER intact by blanking only words.
        foreach (var token in tokens.Take(3).Reverse())
            sql = sql.Remove(token.Start, token.Length).Insert(token.Start, token == first ? "ALTER" : new string(' ', token.Length));
        return sql;
    }

    private static void GuardView(List<Token> tokens, int start)
    {
        // Refuse extra commands with or without optional semicolons. This intentionally
        // accepts a conservative subset; it is not a replacement T-SQL grammar.
        string[] forbidden = { "INSERT", "UPDATE", "DELETE", "MERGE", "EXEC", "EXECUTE", "CREATE", "ALTER", "DROP", "TRUNCATE", "GRANT", "DENY", "REVOKE", "USE", "SET", "DBCC", "BACKUP", "RESTORE", "KILL", "RECONFIGURE", "SHUTDOWN", "WAITFOR", "PRINT", "RAISERROR", "THROW", "RETURN", "DECLARE", "BEGIN", "IF", "WHILE", "INTO", "COMMIT", "ROLLBACK", "SAVE", "BREAK", "CONTINUE", "GOTO", "OPEN", "CLOSE", "FETCH", "DEALLOCATE", "CHECKPOINT", "RECEIVE", "SEND" };
        int depth = 0;
        bool selectAtDepthZero = false;
        for (int i = start; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (forbidden.Any(token.Is)) throw new ArgumentException("Views must contain only their SELECT query; additional commands are not supported.");
            if (token.Symbol(";") && i != tokens.Count - 1)
                throw new ArgumentException("Submit one view definition, with no additional statements.");
            if (token.Symbol("(")) depth++;
            if (token.Symbol(")") && --depth < 0) throw new ArgumentException("Unbalanced parentheses.");
            if (token.Is("SELECT") && depth == 0)
            {
                bool setOperator = i > 0 && (tokens[i - 1].Is("UNION") || tokens[i - 1].Is("INTERSECT") || tokens[i - 1].Is("EXCEPT") ||
                    i > 1 && tokens[i - 1].Is("ALL") && tokens[i - 2].Is("UNION"));
                if (selectAtDepthZero && !setOperator) throw new ArgumentException("Additional SELECT statements are not supported.");
                selectAtDepthZero = true;
            }
        }
        if (depth != 0 || tokens[^1].Is("SELECT") || tokens[^1].Is("AS")) throw new ArgumentException("Incomplete view definition.");
    }

    private static List<Token> Scan(string sql, bool quotedIdentifier)
    {
        var tokens = new List<Token>();
        for (int i = 0; i < sql.Length;)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            { while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r') i++; continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int nesting = 1; i += 2;
                while (i < sql.Length && nesting > 0)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { nesting++; i += 2; }
                    else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { nesting--; i += 2; }
                    else i++;
                }
                if (nesting != 0) throw new ArgumentException("Unterminated SQL comment.");
                continue;
            }
            int start = i++;
            if (c is '[' or '"' or '\'')
            {
                char close = c == '[' ? ']' : c;
                bool closed = false;
                while (i < sql.Length)
                {
                    if (sql[i++] != close) continue;
                    if (i < sql.Length && sql[i] == close) { i++; continue; }
                    closed = true; break;
                }
                if (!closed) throw new ArgumentException("Unterminated SQL literal or identifier.");
                bool identifier = c == '[' || c == '"' && quotedIdentifier;
                string value = identifier ? sql.Substring(start + 1, i - start - 2).Replace(new string(close, 2), close.ToString()) : sql.Substring(start, i - start);
                tokens.Add(new(value, start, i - start, false, identifier));
            }
            else
            {
                bool word = char.IsLetter(c) || c is '_' or '@' or '#';
                if (word) while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '#' or '$')) i++;
                tokens.Add(new(sql.Substring(start, i - start), start, i - start, word, word));
            }
        }
        return tokens;
    }
}
