using System;
using System.IO;
using System.Linq;
using DBUtil;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DataPieCore;

public static class DefinitionSqlValidator
{
    // Parse the complete script, validate its AST, then change only the declaration tokens.
    // Comments, whitespace, string literals and the module body remain byte-for-byte intact.
    public static string PrepareAlter(DefinitionObject target, string sql, bool quotedIdentifier = true)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("Enter a definition.");
        var parser = new TSql160Parser(quotedIdentifier);
        var script = parser.Parse(new StringReader(sql), out var errors) as TSqlScript;
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors.Take(5).Select(e => $"Line {e.Line}, column {e.Column}: {e.Message}")));
        if (script == null || script.Batches.Count != 1 || script.Batches[0].Statements.Count != 1)
            throw new ArgumentException("Submit exactly one view or procedure definition, without GO or additional batches.");
        var statement = script.Batches[0].Statements[0];
        SchemaObjectName name;
        if (target.Kind == "View" && statement is ViewStatementBody view &&
            statement is CreateViewStatement or AlterViewStatement or CreateOrAlterViewStatement)
        {
            if (view.ViewOptions.Any(o => o.OptionKind == ViewOptionKind.Encryption)) throw new ArgumentException("Encrypted definitions cannot be saved in this editor.");
            name = view.SchemaObjectName;
        }
        else if (target.Kind == "Procedure" && statement is ProcedureStatementBody procedure &&
            statement is CreateProcedureStatement or AlterProcedureStatement or CreateOrAlterProcedureStatement)
        {
            if (procedure.MethodSpecifier != null) throw new ArgumentException("CLR procedures cannot be saved in this editor.");
            if (procedure.ProcedureReference.Number != null) throw new ArgumentException("Numbered procedures are not supported.");
            if (procedure.Options.Any(o => o.OptionKind is ProcedureOptionKind.Encryption or ProcedureOptionKind.NativeCompilation))
                throw new ArgumentException("Encrypted or natively compiled procedures are not supported.");
            name = procedure.ProcedureReference.Name;
        }
        else throw new ArgumentException("Only a definition of the selected object type can be saved.");
        // Exact comparison avoids targeting a different object in case-sensitive databases.
        if (name.Identifiers.Count != 2 || name.SchemaIdentifier.Value != target.Schema || name.BaseIdentifier.Value != target.Name)
            throw new ArgumentException($"The definition must target the selected object: {target.DisplayName}. Include its schema and preserve the exact name.");
        var tokens = statement.ScriptTokenStream;
        var declaration = tokens[statement.FirstTokenIndex];
        string result = sql;
        if (statement is CreateOrAlterViewStatement or CreateOrAlterProcedureStatement)
        {
            var alter = tokens.Skip(statement.FirstTokenIndex).First(t => t.TokenType == TSqlTokenType.Alter);
            result = sql.Remove(declaration.Offset, alter.Offset + alter.Text.Length - declaration.Offset).Insert(declaration.Offset, "ALTER");
        }
        else if (statement is CreateViewStatement or CreateProcedureStatement)
            result = sql.Remove(declaration.Offset, declaration.Text.Length).Insert(declaration.Offset, "ALTER");
        // Never pass GO separators to the database, even a trailing empty batch.
        if (tokens.Any(t => t.TokenType == TSqlTokenType.Go)) throw new ArgumentException("Remove GO batch separators before saving.");
        return result;
    }
}
