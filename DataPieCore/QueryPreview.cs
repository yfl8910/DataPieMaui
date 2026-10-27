using System;
using System.Data;
using DBUtil;

namespace DataPieCore;

public static class QueryPreview
{
    public static (QueryPreviewResult Preview, QueryResultArchive Archive) LoadFull(IDbAccess access, string sql, int limit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if (access.conn.State != ConnectionState.Open) access.conn.Open();
        using var command = access.conn.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 1000;
        using var reader = command.ExecuteReader();
        var archive = QueryResultArchive.Capture(reader);
        try { return (archive.Search(null, limit), archive); }
        catch { archive.Dispose(); throw; }
    }
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
