using System;
using System.Data;
using System.Threading;
using DBUtil;

namespace DataPieCore;

public static class QueryPreview
{
    public static (QueryPreviewResult Preview, QueryResultArchive Archive) LoadFull(IDbAccess access, string sql, int limit,
        Action<QueryPreviewResult> previewReady = null, CancellationToken cancellation = default)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        cancellation.ThrowIfCancellationRequested();
        if (access.conn.State != ConnectionState.Open) access.conn.Open();
        using var command = access.conn.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 1000;
        using var registration = cancellation.Register(() =>
        {
            try
            {
                if (command.Connection is Microsoft.Data.Sqlite.SqliteConnection sqlite)
                    SQLitePCL.raw.sqlite3_interrupt(sqlite.Handle);
                else if (command.Connection is System.Data.SQLite.SQLiteConnection plainSqlite)
                    plainSqlite.Cancel();
                else command.Cancel();
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Query cancellation request failed: {0}", ex.Message); }
        });
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            QueryPreviewResult first = null;
            var archive = QueryResultArchive.Capture(reader, limit, previewReady == null ? null : data =>
            {
                first = data;
                previewReady(data);
            }, cancellation);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                return (first ?? archive.Search(null, limit), archive);
            }
            catch { archive.Dispose(); throw; }
        }
        catch (System.Data.Common.DbException ex) when (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException("Query canceled.", ex, cancellation);
        }
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
