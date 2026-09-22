using DeviceHub.Core.History;
using DeviceHub.Core.Models;
using Microsoft.Data.Sqlite;

namespace DeviceHub.Storage.History;

/// <summary>
/// SQLite 历史存储实现（Microsoft.Data.Sqlite 原生 ADO.NET，? 占位符参数化）。
///
/// 三个关键决定：
/// 1. WAL 日志模式——读写不互斥，后台批量写历史时不阻塞并发查询；
/// 2. 建表用 IF NOT EXISTS——首次运行自动建库，没有"先跑建库脚本"这个步骤；
/// 3. 时间以 ISO8601 文本存储（Microsoft.Data.Sqlite 对 DateTime 的默认映射）——
///    字符串序即时间序，(点位, 时间) 复合索引的范围扫描直接可用。
///
/// 安全约定：所有 SQL 都是编译期常量，全部使用命名占位符（@p0…）+ SqliteParameter
/// 传值，值与 SQL 永不相拼。参数对象每条命令创建一次、循环内只换值——少一次参数
/// 解析，这是批量插入该有的写法。
/// 性能：批量插入包在单事务里（事务开销远大于逐行执行），一批要么全进要么全不进。
/// </summary>
public sealed class SqliteHistoryStore : IPointHistoryStore, IAlarmEventStore
{
    private const string InsertPointSql =
        "INSERT INTO PointHistory (PointName, Value, Quality, Timestamp) VALUES (@p0, @p1, @p2, @p3)";

    private const string InsertAlarmSql =
        "INSERT INTO AlarmHistory (AlarmId, PointName, Condition, Level, Event, PeakValue, Description, EventTime, RaisedAt, ClearedAt)" +
        " VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9)";

    private readonly string _connectionString;

    public SqliteHistoryStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString();
        EnsureSchema();
    }

    public Task AppendAsync(IReadOnlyList<PointHistoryRecord> records, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (records.Count == 0)
            {
                return;
            }

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = InsertPointSql;
            // Microsoft.Data.Sqlite 的怪癖：? 占位符按顺序绑定，但参数对象仍必须有名字（名字被忽略）
            var pName = command.Parameters.Add(new SqliteParameter { ParameterName = "@p0" });
            var pValue = command.Parameters.Add(new SqliteParameter { ParameterName = "@p1" });
            var pQuality = command.Parameters.Add(new SqliteParameter { ParameterName = "@p2" });
            var pTimestamp = command.Parameters.Add(new SqliteParameter { ParameterName = "@p3" });

            foreach (var record in records)
            {
                pName.Value = record.PointName;
                pValue.Value = (object?)record.Value ?? DBNull.Value;
                pQuality.Value = record.Quality.ToString();
                pTimestamp.Value = record.Timestamp;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }, cancellationToken);

    public Task<IReadOnlyList<PointHistoryRecord>> QueryAsync(
        string pointName, DateTime from, DateTime to, int limit, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<PointHistoryRecord>>(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            // 倒序取回后由调用侧截断再反转——画趋势线必须从旧到新；
            // 时间范围本身有界，不用 LIMIT 子句
            command.CommandText =
                "SELECT PointName, Value, Quality, Timestamp FROM PointHistory" +
                " WHERE PointName = @pointName AND Timestamp >= @startTime AND Timestamp <= @endTime" +
                " ORDER BY Timestamp DESC";
            command.Parameters.Add(new SqliteParameter { ParameterName = "@pointName", Value = pointName });
            command.Parameters.Add(new SqliteParameter { ParameterName = "@startTime", Value = from });
            command.Parameters.Add(new SqliteParameter { ParameterName = "@endTime", Value = to });

            var newest = new List<PointHistoryRecord>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    newest.Add(new PointHistoryRecord(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetDouble(1),
                        Enum.Parse<PointQuality>(reader.GetString(2)),
                        reader.GetDateTime(3)));
                }
            }

            newest = newest.Take(limit).ToList();
            newest.Reverse();
            return (IReadOnlyList<PointHistoryRecord>)newest;
        }, cancellationToken);

    public Task AppendAsync(IReadOnlyList<AlarmEventRecord> records, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (records.Count == 0)
            {
                return;
            }

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = InsertAlarmSql;
            var parameters = new[]
            {
                command.Parameters.Add(new SqliteParameter { ParameterName = "@p0" }),
                command.Parameters.Add(new SqliteParameter { ParameterName = "@p1" }),
                command.Parameters.Add(new SqliteParameter { ParameterName = "@p2" }),
                command.Parameters.Add(new SqliteParameter { ParameterName = "@p3" }),
                command.Parameters.Add(new SqliteParameter { ParameterName = "@p4" }),
                command.Parameters.Add(new SqliteParameter { ParameterName = "@p5" }),
                command.Parameters.Add(new SqliteParameter { ParameterName = "@p6" }),
                command.Parameters.Add(new SqliteParameter { ParameterName = "@p7" }),
                command.Parameters.Add(new SqliteParameter { ParameterName = "@p8" }),
                command.Parameters.Add(new SqliteParameter { ParameterName = "@p9" }),
            };

            foreach (var record in records)
            {
                parameters[0].Value = record.AlarmId;
                parameters[1].Value = record.PointName;
                parameters[2].Value = record.Condition;
                parameters[3].Value = record.Level;
                parameters[4].Value = record.Event;
                parameters[5].Value = (object?)record.PeakValue ?? DBNull.Value;
                parameters[6].Value = record.Description;
                parameters[7].Value = record.EventTime;
                parameters[8].Value = record.RaisedAt;
                parameters[9].Value = (object?)record.ClearedAt ?? DBNull.Value;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }, cancellationToken);

    public Task<IReadOnlyList<AlarmEventRecord>> QueryRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AlarmEventRecord>>(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT AlarmId, PointName, Condition, Level, Event, PeakValue, Description, EventTime, RaisedAt, ClearedAt" +
                " FROM AlarmHistory ORDER BY EventTime DESC";

            var result = new List<AlarmEventRecord>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read() && result.Count < limit)
                {
                    result.Add(new AlarmEventRecord(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetDouble(5),
                        reader.GetString(6),
                        reader.GetDateTime(7),
                        reader.GetDateTime(8),
                        reader.IsDBNull(9) ? null : reader.GetDateTime(9)));
                }
            }

            return (IReadOnlyList<AlarmEventRecord>)result;
        }, cancellationToken);

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL";
        pragma.ExecuteNonQuery();

        using var schema = connection.CreateCommand();
        schema.CommandText =
            "CREATE TABLE IF NOT EXISTS PointHistory (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, PointName TEXT NOT NULL, Value REAL, Quality TEXT NOT NULL, Timestamp TEXT NOT NULL);" +
            "CREATE INDEX IF NOT EXISTS IX_PointHistory_Name_Time ON PointHistory (PointName, Timestamp);" +
            "CREATE TABLE IF NOT EXISTS AlarmHistory (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, AlarmId INTEGER NOT NULL, PointName TEXT NOT NULL, Condition TEXT NOT NULL," +
            "Level TEXT NOT NULL, Event TEXT NOT NULL, PeakValue REAL, Description TEXT NOT NULL, EventTime TEXT NOT NULL," +
            "RaisedAt TEXT NOT NULL, ClearedAt TEXT);" +
            "CREATE INDEX IF NOT EXISTS IX_AlarmHistory_Time ON AlarmHistory (EventTime);";
        schema.ExecuteNonQuery();
    }
}
