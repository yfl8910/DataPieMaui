using DataPieCore;
using DBUtil;

namespace DataPieMaui.Services.Shared;

internal static class TableSelection
{
    public static string Key(TableStruct table, string type) => type == "SQLSERVER"
        ? SqlQueryBuilder.QuoteIdentifier(table.TableSchemaName, type) + "." + SqlQueryBuilder.QuoteIdentifier(table.Name, type)
        : table.Name;
    public static string Label(TableStruct table, string type) => type == "SQLSERVER" ? table.TableSchemaName + "." + table.Name : table.Name;
    public static TableStruct Resolve(IEnumerable<TableStruct> tables, string value, string type)
        => tables.SingleOrDefault(t => Key(t, type) == value)
            ?? throw new InvalidOperationException("Select a table in the current database. Refresh the schema if the table list changed.");
    public static string SelectAll(TableStruct table, string type)
        => "SELECT * FROM " + (type == "SQLSERVER" ? Key(table, type) : SqlQueryBuilder.QuoteIdentifier(table.Name, type));
}
