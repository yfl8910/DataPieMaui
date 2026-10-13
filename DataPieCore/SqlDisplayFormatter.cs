using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
namespace DataPieCore;

// A conservative display formatter: never rewrites identifiers, literals or SQL tokens.
public static class SqlDisplayFormatter
{
    private record Token(string Text, bool Protected, bool Line = false);
    public static string Format(string sql)
    {
        if (sql.Length > 2_000_000) throw new InvalidOperationException("SQL is too large to format.");
        var tokens = Scan(sql);
        var output = new StringBuilder();
        int depth = 0;
        bool fresh = true;
        void NewLine() { if (!fresh) { output.AppendLine(); fresh = true; } }
        foreach (var token in tokens)
        {
            var upper = token.Text.ToUpperInvariant();
            bool clause = !token.Protected && upper is "SELECT" or "FROM" or "WHERE" or "JOIN" or "LEFT" or "RIGHT" or "INNER" or "GROUP" or "ORDER" or "HAVING" or "UNION" or "WHEN" or "ELSE" or "BEGIN" or "END" or "UPDATE" or "INSERT" or "DELETE" or "SET" or "RETURN";
            if (!token.Protected && upper == "END") depth = Math.Max(0, depth - 1);
            if (clause || token.Line) NewLine();
            if (fresh) { output.Append(' ', Math.Min(depth, 20) * 4); fresh = false; }
            else output.Append(' ');
            output.Append(token.Text);
            if (!token.Protected && upper == "BEGIN") depth++;
            if (token.Line || token.Text == ";" || token.Text == "," || !token.Protected && upper == "BEGIN") NewLine();
        }
        var formatted = output.ToString().Trim();
        if (!tokens.Select(t => t.Text).SequenceEqual(Scan(formatted).Select(t => t.Text)))
            throw new InvalidOperationException("Formatting could not preserve all SQL tokens. Original text retained.");
        return formatted;
    }
    private static List<Token> Scan(string sql)
    {
        var result = new List<Token>();
        for (int i = 0; i < sql.Length;)
        {
            if (char.IsWhiteSpace(sql[i])) { i++; continue; }
            int start = i;
            // Preserve standalone SQL Server batch separators and repeat counts.
            if ((sql[i] is 'G' or 'g') && (i == 0 || sql.LastIndexOf('\n', i - 1) is var lineStart && sql.Substring(lineStart + 1, i - lineStart - 1).Trim().Length == 0))
            {
                int end = sql.IndexOf('\n', i); if (end < 0) end = sql.Length;
                string line = sql.Substring(i, end - i).TrimEnd('\r', ' ', '\t');
                if (Regex.IsMatch(line, @"^GO(?:[ \t]+[0-9]+)?(?:[ \t]*--.*)?$", RegexOptions.IgnoreCase))
                { result.Add(new Token(line, true, true)); i = end; continue; }
            }
            if (i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-')
            {
                int end = sql.IndexOf('\n', i); if (end < 0) end = sql.Length;
                result.Add(new Token(sql.Substring(i, end-i).TrimEnd('\r'), true, true)); i = end; continue;
            }
            if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
            {
                i += 2; int nesting = 1;
                while (i < sql.Length && nesting > 0)
                {
                    if (i+1 < sql.Length && sql[i]=='/' && sql[i+1]=='*') { nesting++; i+=2; }
                    else if (i+1 < sql.Length && sql[i]=='*' && sql[i+1]=='/') { nesting--; i+=2; }
                    else i++;
                }
                if (nesting != 0) throw new InvalidOperationException("Unclosed SQL comment. Original text retained.");
                result.Add(new Token(sql.Substring(start,i-start),true)); continue;
            }
            if ((sql[i] is 'N' or 'n' or 'X' or 'x') && i+1 < sql.Length && sql[i+1]=='\'') i++;
            if (sql[i] is '\'' or '"' or '[' or '`')
            {
                char close = sql[i]=='[' ? ']' : sql[i]; i++; bool closed=false;
                while (i < sql.Length)
                {
                    if (sql[i++] != close) continue;
                    if (i < sql.Length && sql[i]==close) { i++; continue; }
                    closed=true; break;
                }
                if (!closed) throw new InvalidOperationException("Unclosed SQL string or identifier. Original text retained.");
                result.Add(new Token(sql.Substring(start,i-start),true)); continue;
            }
            if (sql[i] is '(' or ')' or ',' or ';') i++;
            else
            {
                while (i < sql.Length && !char.IsWhiteSpace(sql[i]) && sql[i] is not ('\'' or '"' or '[' or '`' or '(' or ')' or ',' or ';'))
                {
                    if (i+1 < sql.Length && (sql[i]=='-' && sql[i+1]=='-' || sql[i]=='/' && sql[i+1]=='*')) break;
                    i++;
                }
                if (i == start) i++;
            }
            result.Add(new Token(sql.Substring(start,i-start),false));
        }
        return result;
    }
}
