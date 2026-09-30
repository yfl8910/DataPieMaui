using System;
using System.Data;
using DBUtil;

namespace DataPieCore;

public static class QueryPreview
{
    // Do not rewrite arbitrary SQL: it may contain CTEs, batches or procedures.
    public static QueryPreviewResult Load(IDbAccess access, string sql, int limit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if (access.conn.State != ConnectionState.Open) access.conn.Open();
        using var command = access.conn.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 1000;
        using var reader = command.ExecuteReader();
        var result = QueryPreviewResult.Read(reader, limit);
        if (result.Truncated) command.Cancel();
        return result;
    }
}
