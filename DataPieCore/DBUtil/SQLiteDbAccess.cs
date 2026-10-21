using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Text;
using System.Linq;
using DataPieCore;

namespace DBUtil
{
    public partial class SQLiteDbAccess : IDbAccess
    {
        public string ConnectionString { get; set; }
        public IDbConnection conn { set; get; }
        public DataBaseType DataBaseType { get; set; }

 

        /// <summary>
        /// 执行sql语句
        /// </summary>
        /// <param name="strSql">要执行的sql语句</param>
        /// <returns>受影响的行数</returns>
        public int ExecuteSql(string strSql)
        {
            try
            {
                using SQLiteCommand cmd = new SQLiteCommand(strSql, (SQLiteConnection)conn);
                if (conn.State != ConnectionState.Open)
                {
                    conn.Open();
                }
                int r = cmd.ExecuteNonQuery();
                return r;
            }
            finally
            {
                conn.Close();
            }
        }

    
        /// <summary>
        /// 获取阅读器
        /// </summary>
        /// <param name="strSql">sql语句</param>
        /// <returns>返回阅读器</returns>
        public IDataReader GetDataReader(string strSql)
        {
            return OwnedDataReader.Open(this, strSql, null, 30);
        }
        /// <summary>
        /// 返回查询结果的数据集
        /// </summary>
        /// <param name="strSql">sql语句</param>
        /// <returns>返回的查询结果集</returns>
        private DataSet GetDataSet(string strSql) => GetDataSet(strSql, null);

        private DataSet GetDataSet(string strSql, IDbDataParameter[] paraArr)
        {
            using var command = new SQLiteCommand(strSql, (SQLiteConnection)conn);
            using var adapter = new SQLiteDataAdapter(command);
            var result = new DataSet();
            try
            {
                if (paraArr != null) command.Parameters.AddRange(paraArr);
                if (conn.State != ConnectionState.Open) conn.Open();
                adapter.Fill(result);
                return result;
            }
            catch { result.Dispose(); throw; }
            finally
            {
                command.Parameters.Clear();
                conn.Close();
            }
        }
        /// <summary>
        /// 返回查询结果的数据表
        /// </summary>
        /// <param name="strSql">sql语句</param>
        /// <returns>返回的查询数据表</returns>
        public DataTable GetDataTable(string strSql)
        {
            DataSet ds = GetDataSet(strSql);
            if (ds.Tables.Count > 0)
            {
                DataTable dt = ds.Tables[0];
                ds.Tables.Remove(dt);
                return dt;
            }
            return null;
        }

        /// <summary>
        /// 实现释放资源的方法
        /// </summary>
        public void Dispose()
        {
            conn?.Dispose();
        }
        /// <summary>
        /// 根据当前的数据库类型和连接字符串创建一个新的数据库操作对象
        /// </summary>
        /// <returns></returns>
        public IDbAccess CreateNewAccess()
        {
            return DbAccessFactory.Create(ConnectionString, DataBaseType);
        }

        List<string> IDbAccess.ShowViews() => ShowViews();

        public int TruncateTable(string tableName)
        {
            return ExecuteSql("DELETE FROM " + QuoteIdentifier(tableName));
        }

        private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

        public bool BulkInsert(string tableName, IDataReader reader)
        {
            if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentException(nameof(tableName));
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            if (names.Length == 0) throw new ArgumentException("No columns to import.");
            using var connection = new SQLiteConnection(ConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            var blankAsNull = new bool[names.Length];
            var primaryKeys = new List<(string Name, string Type)>();
            using (var schema = connection.CreateCommand())
            {
                schema.Transaction = transaction;
                schema.CommandText = "PRAGMA table_info(" + QuoteIdentifier(tableName) + ")";
                using var columns = schema.ExecuteReader();
                while (columns.Read())
                {
                    if (Convert.ToInt32(columns[5]) > 0) primaryKeys.Add((Convert.ToString(columns[1]), Convert.ToString(columns[2])));
                    int index = Array.FindIndex(names, n => string.Equals(n, Convert.ToString(columns[1]), StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) blankAsNull[index] = NumericOrDateColumn(Convert.ToString(columns[2]) ?? "");
                }
            }
            bool hasPrimaryIndex = false;
            using (var indexes = connection.CreateCommand())
            {
                indexes.Transaction = transaction;
                indexes.CommandText = "PRAGMA index_list(" + QuoteIdentifier(tableName) + ")";
                using var indexReader = indexes.ExecuteReader();
                while (indexReader.Read()) if (Convert.ToString(indexReader[3]) == "pk") hasPrimaryIndex = true;
            }
            string identity = primaryKeys.Count == 1 && primaryKeys[0].Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) && !hasPrimaryIndex
                ? primaryKeys[0].Name : null;
            var ordinals = Enumerable.Range(0, names.Length).Where(i => !string.Equals(names[i], identity, StringComparison.OrdinalIgnoreCase)).ToArray();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = ordinals.Length == 0 ? "INSERT INTO " + QuoteIdentifier(tableName) + " DEFAULT VALUES"
                : "INSERT INTO " + QuoteIdentifier(tableName) + " (" +
                string.Join(",", ordinals.Select(i => QuoteIdentifier(names[i]))) + ") VALUES (" +
                string.Join(",", Enumerable.Range(0, ordinals.Length).Select(i => "@p" + i)) + ")";
            var parameters = new SQLiteParameter[ordinals.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                parameters[i] = new SQLiteParameter("@p" + i);
                command.Parameters.Add(parameters[i]);
            }
            command.Prepare();
            int rowNumber = 0;
            while (reader.Read())
            {
                rowNumber++;
                int column = -1;
                try
                {
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        column = ordinals[i];
                        var value = reader.GetValue(column) ?? DBNull.Value;
                        if (blankAsNull[column] && value is string text && string.IsNullOrWhiteSpace(text)) value = DBNull.Value;
                        // SQLiteParameter retains inferred types; each row may contain a different CLR type.
                        parameters[i].ResetDbType();
                        parameters[i].Value = value;
                    }
                    column = -1;
                    command.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    var location = column >= 0 ? ", column " + names[column] : "";
                    throw new InvalidOperationException($"Import into {tableName} failed at data row {rowNumber}{location}: {ex.Message}", ex);
                }
            }
            transaction.Commit();
            return true;
        }
        private static bool NumericOrDateColumn(string declaredType)
        {
            var type = declaredType.ToUpperInvariant();
            // Honor SQLite affinity precedence: INT before text, text before numeric.
            if (type.Contains("INT")) return true;
            if (type.Contains("CHAR") || type.Contains("CLOB") || type.Contains("TEXT") || type.Contains("BLOB") || type.Length == 0) return false;
            return true; // REAL, FLOAT, DOUBLE and NUMERIC affinity (including DATE/DATETIME).
        }
        public int RunProcedure(string storedProcName)
        {
            return RunProcedure(storedProcName, Array.Empty<IDataParameter>(), out _);
        }

        internal int RunProcedure(string storedProcName, IDataParameter[] parameters, out int rowsAffected)
        {
            var script = SQLiteProcedureScripts.Load(ConnectionString, storedProcName);
            var bound = SQLiteProcedureScripts.Bind(script, parameters);
            try
            {
                if (conn.State != ConnectionState.Open) conn.Open();
                using var transaction = ((SQLiteConnection)conn).BeginTransaction();
                using var command = new SQLiteCommand(script.Sql, (SQLiteConnection)conn)
                {
                    Transaction = transaction,
                    CommandTimeout = 600
                };
                command.Parameters.AddRange(bound.Cast<SQLiteParameter>().ToArray());
                rowsAffected = command.ExecuteNonQuery();
                transaction.Commit();
                return rowsAffected;
            }
            finally
            {
                conn.Close();
            }
        }

    }
}
