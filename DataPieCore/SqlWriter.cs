using System;
using System.Linq;
using DBUtil;

namespace DataPieCore
{
    public static class SqlWriter
    {
        public static string WriteSelect(TableStruct table, string databaseType, int? rowLimit = null)
        {
            if (rowLimit < 0) throw new ArgumentOutOfRangeException(nameof(rowLimit));
            if (databaseType == "SQLSERVER" && !string.IsNullOrEmpty(table.TableSchemaName))
                return $"SELECT {(rowLimit.HasValue ? $"TOP {rowLimit.Value} " : "")}{string.Join(", ", table.Columns.Select(c => SqlQueryBuilder.QuoteIdentifier(c.Name, databaseType)))} FROM {QuoteTable(table, databaseType)}";
            return SqlQueryBuilder.BuildQuery(table.Columns.Select(column => column.Name).ToArray(),
                table.Name, databaseType, rowLimit);
        }

        public static string WriteSelectCount(TableStruct table, string databaseType)
            => $"SELECT COUNT(*) FROM {QuoteTable(table, databaseType)}";

        public static string WriteUpdate(TableStruct table, string databaseType)
        {
            string assignments = string.Join("," + Environment.NewLine, table.Columns
                .Where(column => !column.IsPrimaryKey)
                .Select(column => $"\t{SqlQueryBuilder.QuoteIdentifier(column.Name, databaseType)} = {column.FinalType}"));
            return $"UPDATE {QuoteTable(table, databaseType)}{Environment.NewLine}SET{Environment.NewLine}{assignments}{Environment.NewLine}WHERE{Environment.NewLine}{BuildKeyPredicate(table, databaseType)}";
        }

        public static string WriteDelete(TableStruct table, string databaseType)
            => $"DELETE FROM {QuoteTable(table, databaseType)}{Environment.NewLine}WHERE{Environment.NewLine}{BuildKeyPredicate(table, databaseType)}";

        private static string QuoteTable(TableStruct table, string databaseType)
            => databaseType == "SQLSERVER" && !string.IsNullOrEmpty(table.TableSchemaName)
                ? SqlQueryBuilder.QuoteIdentifier(table.TableSchemaName, databaseType) + "." + SqlQueryBuilder.QuoteIdentifier(table.Name, databaseType)
                : SqlQueryBuilder.QuoteIdentifier(table.Name, databaseType);

        private static string BuildKeyPredicate(TableStruct table, string databaseType)
            => string.Join(Environment.NewLine + "AND ", table.Columns
                .Where(column => column.IsPrimaryKey)
                .Select(column => $"{SqlQueryBuilder.QuoteIdentifier(column.Name, databaseType)} = /*value:{column.Name}*/"));

        public static string WriteSummary(Column column, string databaseType)
            => $"{SqlQueryBuilder.QuoteIdentifier(column.Name, databaseType)} ({column.FinalType} {(column.IsNullable ? "" : "not ")}null)";
    }
}
