using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using DataPieCore;

namespace DBUtil
{
    /// <summary>
    /// SqlServer数据库访问对象
    /// </summary>
    public partial class SqlServerDbAccess : IDbAccess
    {

        /// <summary>
        /// 连接字符串
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// 连接对象
        /// </summary>
        public IDbConnection conn { set; get; }

        /// <summary>
        /// 数据库类型
        /// </summary>
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
                using SqlCommand cmd = new SqlCommand(strSql, (SqlConnection)conn)
                {
                    CommandTimeout = 1000
                };

                if (conn.State != ConnectionState.Open)
                    conn.Open();

                return cmd.ExecuteNonQuery();
            }
            finally
            {
                try
                {
                    conn?.Close();
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 获取阅读器
        /// </summary>
        /// <param name="strSql">sql语句</param>
        /// <returns>返回阅读器</returns>
        public IDataReader GetDataReader(string strSql)
        {
            return OwnedDataReader.Open(this, strSql, null, 10000);
        }

        /// <summary>
        /// 返回查询结果的数据表
        /// </summary>
        /// <param name="strSql">sql语句</param>
        /// <returns>返回的查询数据表</returns>
        public DataTable GetDataTable(string strSql)
        {
            using var command = new SqlCommand(strSql, (SqlConnection)conn);
            using var adapter = new SqlDataAdapter(command);
            var table = new DataTable("Table");

            try
            {
                if (conn.State != ConnectionState.Open)
                    conn.Open();
                adapter.Fill(table);
                if (table.Columns.Count > 0)
                    return table;
                table.Dispose();
                return null;
            }
            catch
            {
                table.Dispose();
                throw;
            }
            finally
            {
                conn.Close();
            }
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

        #region 批量插入操作

        public bool BulkInsert(string tableName, IDataReader reader)
            => BulkInsert(tableName, reader, null, false);

        public bool BulkInsert(string tableName, IDataReader reader, string sourceFile)
            => BulkInsert(tableName, reader, sourceFile, true);

        private bool BulkInsert(string tableName, IDataReader reader, string sourceFile, bool validateFieldLengths)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException(nameof(tableName));
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            string destination = SqlQueryBuilder.QuoteSqlServerTableName(tableName);

            using SqlConnection connection = new SqlConnection(ConnectionString);
            connection.Open();
            using SqlBulkCopy bulkCopy = new SqlBulkCopy(connection)
            {
                DestinationTableName = destination
            };

            // Discover columns using the SAME connection used for bulk copy
            var destinationColumns = new HashSet<string>(StringComparer.Ordinal);
            var lengthLimits = new Dictionary<string, int>(StringComparer.Ordinal);
            var blankAsNullColumns = new HashSet<string>(StringComparer.Ordinal);
            using (var schemaCmd = new SqlCommand($"SELECT TOP (0) * FROM {destination}", connection))
            using (var schemaReader = schemaCmd.ExecuteReader())
            {
                using var schema = validateFieldLengths ? schemaReader.GetSchemaTable() : null;
                for (int i = 0; i < schemaReader.FieldCount; i++)
                {
                    destinationColumns.Add(schemaReader.GetName(i));
                    string type = schemaReader.GetDataTypeName(i);
                    if (BlankImportValuesBecomeNull(type)) blankAsNullColumns.Add(schemaReader.GetName(i));
                    if (validateFieldLengths && (type == "nvarchar" || type == "nchar"))
                    {
                        int size = Convert.ToInt32(schema.Rows[i]["ColumnSize"]);
                        if (size > 0 && size < int.MaxValue)
                            lengthLimits.Add(schemaReader.GetName(i), size);
                    }
                }
            }

            AddColumnMappings(bulkCopy, reader, destinationColumns);

            bulkCopy.WriteToServer(new ImportLengthDataReader(reader, lengthLimits, sourceFile, blankAsNullColumns));
            return true;
        }

        internal static bool BlankImportValuesBecomeNull(string type) => type.ToLowerInvariant() is
            "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney"
            or "float" or "real" or "bit" or "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time";

        internal static void AddColumnMappings(SqlBulkCopy bulkCopy, IDataReader reader, HashSet<string> destinationColumns)
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                if (destinationColumns.Remove(name))
                    bulkCopy.ColumnMappings.Add(i, name);
            }

            if (bulkCopy.ColumnMappings.Count == 0)
                throw new InvalidOperationException("No matching columns were found in the destination table.");
        }

        public int TruncateTable(string tableName)
            => ExecuteSql("TRUNCATE TABLE " + SqlQueryBuilder.QuoteSqlServerTableName(tableName));

        #endregion 批量插入操作

        #region 存储过程操作

        /// <summary>
        /// 执行存储过程，返回影响行数
        /// </summary>
        public int RunProcedure(string storedProcName)
        {
            using var connection = new SqlConnection(ConnectionString);
            using var command = new SqlCommand(storedProcName, connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 100000
            };
            connection.Open();
            return command.ExecuteNonQuery();
        }

        #endregion 存储过程操作
    }
}
