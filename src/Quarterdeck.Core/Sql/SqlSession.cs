using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Quarterdeck.Core.Model;

namespace Quarterdeck.Core.Sql;

/// <summary>One result set, every value already turned into display text.</summary>
public sealed record SqlResultSet(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows, bool Truncated);

/// <summary>What a run produced: result sets, rows changed, server messages, time taken.</summary>
public sealed record SqlOutcome(IReadOnlyList<SqlResultSet> Sets, int RecordsAffected, IReadOnlyList<string> Messages, TimeSpan Elapsed);

/// <summary>
/// An open connection for a SQL tab: SQL Server, PostgreSQL, MySQL/MariaDB or a SQLite file.
/// Read-only is enforced by the server where it can be (PostgreSQL, MySQL, SQLite); SQL Server
/// has no session-wide switch, so there the statement check in <see cref="SqlSafety"/> refuses.
/// </summary>
public sealed class SqlSession : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly SessionDefinition _definition;
    private readonly List<string> _messages = new();

    private SqlSession(DbConnection connection, SessionDefinition definition)
    {
        _connection = connection;
        _definition = definition;
        switch (connection)
        {
            case SqlConnection sql:
                sql.InfoMessage += (_, e) => Note(e.Message);
                break;
            case NpgsqlConnection pg:
                pg.Notice += (_, e) => Note($"{e.Notice.Severity}: {e.Notice.MessageText}");
                break;
            case MySqlConnection my:
                my.InfoMessage += (_, e) => { foreach (var err in e.Errors) Note(err.Message); };
                break;
        }
    }

    public string ServerVersion => _connection.ServerVersion;

    public static int DefaultPort(SqlEngine engine) => engine switch
    {
        SqlEngine.SqlServer => 1433,
        SqlEngine.PostgreSql => 5432,
        SqlEngine.MySql => 3306,
        _ => 0
    };

    public static string EngineName(SqlEngine engine) => engine switch
    {
        SqlEngine.SqlServer => "SQL Server",
        SqlEngine.PostgreSql => "PostgreSQL",
        SqlEngine.MySql => "MySQL",
        _ => "SQLite"
    };

    public static async Task<SqlSession> OpenAsync(SessionDefinition d, string? username, string? password, CancellationToken token)
    {
        var port = d.Port > 0 ? d.Port : DefaultPort(d.SqlEngine);
        DbConnection connection = d.SqlEngine switch
        {
            SqlEngine.SqlServer => new SqlConnection(new SqlConnectionStringBuilder
            {
                DataSource = port == 1433 ? d.Host.Trim() : $"{d.Host.Trim()},{port}",
                InitialCatalog = d.SqlDatabase,
                IntegratedSecurity = d.SqlWindowsAuth,
                UserID = d.SqlWindowsAuth ? "" : username ?? "",
                Password = d.SqlWindowsAuth ? "" : password ?? "",
                Encrypt = SqlConnectionEncryptOption.Mandatory,
                TrustServerCertificate = d.SqlTrustServerCertificate,
                ApplicationName = "BaudHoundTerm",
                ConnectTimeout = 15
            }.ConnectionString),

            SqlEngine.PostgreSql => new NpgsqlConnection(new NpgsqlConnectionStringBuilder
            {
                Host = d.Host.Trim(),
                Port = port,
                Database = string.IsNullOrWhiteSpace(d.SqlDatabase) ? null : d.SqlDatabase,
                Username = username,
                Password = password,
                SslMode = d.SqlTrustServerCertificate ? SslMode.Require : SslMode.Prefer,
                ApplicationName = "BaudHoundTerm",
                Timeout = 15,
                Options = d.SqlReadOnly ? "-c default_transaction_read_only=on" : null
            }.ConnectionString),

            SqlEngine.MySql => new MySqlConnection(new MySqlConnectionStringBuilder
            {
                Server = d.Host.Trim(),
                Port = (uint)port,
                Database = d.SqlDatabase,
                UserID = username ?? "",
                Password = password ?? "",
                SslMode = MySqlSslMode.Preferred,
                ConnectionTimeout = 15,
                AllowUserVariables = true
            }.ConnectionString),

            _ => new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = d.SqlFile,
                Mode = d.SqlReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite
            }.ConnectionString)
        };

        if (d.SqlEngine == SqlEngine.Sqlite && !File.Exists(d.SqlFile))
        {
            await connection.DisposeAsync();
            throw new FileNotFoundException($"No database file at {d.SqlFile}");
        }

        try
        {
            await connection.OpenAsync(token);
            if (d.SqlEngine == SqlEngine.MySql && d.SqlReadOnly)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SET SESSION TRANSACTION READ ONLY";
                await cmd.ExecuteNonQueryAsync(token);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return new SqlSession(connection, d);
    }

    /// <summary>
    /// Runs the text as one batch (SQL Server: split on GO lines). At most <paramref name="maxRows"/>
    /// rows are read per result set; the rest is left unread, and the set is marked truncated.
    /// </summary>
    public async Task<SqlOutcome> RunAsync(string sql, int maxRows, CancellationToken token)
    {
        lock (_messages)
        {
            _messages.Clear();
        }

        if (_definition.SqlReadOnly && _definition.SqlEngine == SqlEngine.SqlServer && SqlSafety.ChangesData(sql))
        {
            throw new InvalidOperationException("This shortcut is read-only, and that statement changes data or structure, so it was not sent.");
        }

        var sets = new List<SqlResultSet>();
        var affected = 0;
        var clock = Stopwatch.StartNew();

        foreach (var batch in Batches(sql))
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 0;   // the Stop button cancels; no silent time limit
            using var cancel = token.Register(() =>
            {
                try { cmd.Cancel(); } catch (Exception) { }
            });

            await using var reader = await cmd.ExecuteReaderAsync(token);
            do
            {
                if (reader.FieldCount == 0)
                {
                    continue;
                }

                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                var rows = new List<string?[]>();
                var truncated = false;
                while (await reader.ReadAsync(token))
                {
                    if (rows.Count >= maxRows)
                    {
                        truncated = true;
                        break;
                    }

                    var row = new string?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                    {
                        row[i] = Display(reader, i);
                    }

                    rows.Add(row);
                }

                sets.Add(new SqlResultSet(columns, rows, truncated));
                if (truncated)
                {
                    // Reading on would fetch the rest of a large result just to throw it away.
                    try { cmd.Cancel(); } catch (Exception) { }
                    break;
                }
            }
            while (await reader.NextResultAsync(token));

            if (reader.RecordsAffected > 0)
            {
                affected += reader.RecordsAffected;
            }
        }

        string[] messages;
        lock (_messages)
        {
            messages = _messages.ToArray();
        }

        return new SqlOutcome(sets, affected, messages, clock.Elapsed);
    }

    private void Note(string message)
    {
        lock (_messages)
        {
            _messages.Add(message);
        }
    }

    /// <summary>SQL Server tools split scripts on GO lines; the server itself does not understand GO.</summary>
    private IEnumerable<string> Batches(string sql)
    {
        if (_definition.SqlEngine != SqlEngine.SqlServer)
        {
            yield return sql;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var line in sql.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\s*GO\s*;?\s*$", RegexOptions.IgnoreCase))
            {
                if (current.ToString().Trim().Length > 0)
                {
                    yield return current.ToString();
                }

                current.Clear();
            }
            else
            {
                current.Append(line).Append('\n');
            }
        }

        if (current.ToString().Trim().Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string? Display(DbDataReader reader, int i)
    {
        if (reader.IsDBNull(i))
        {
            return null;
        }

        var value = reader.GetValue(i);
        return value switch
        {
            byte[] bytes => "0x" + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 64))) + (bytes.Length > 64 ? $"... ({bytes.Length} bytes)" : ""),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}

/// <summary>Spots statements that change data or structure, to ask before they run.</summary>
public static class SqlSafety
{
    private static readonly Regex Comments = new(@"--[^\n]*|/\*[\s\S]*?\*/", RegexOptions.Compiled);
    private static readonly Regex Literals = new(@"'(?:[^']|'')*'|""(?:[^""]|"""")*""|`[^`]*`|\[[^\]]*\]", RegexOptions.Compiled);

    // Errs towards asking: SELECT ... INTO also matches, and so does a MySQL SELECT INTO @var.
    private static readonly Regex Changing = new(
        @"\b(insert|update|delete|merge|replace|upsert|create|alter|drop|truncate|rename|grant|revoke|deny|" +
        @"exec|execute|call|do|copy|load|import|vacuum|reindex|cluster|attach|detach|backup|restore|shutdown|kill|" +
        @"dbcc|bulk|lock|unlock|set\s+password)\b|\binto\b|\bpragma\s+\w+\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool ChangesData(string sql)
    {
        var bare = Literals.Replace(Comments.Replace(sql, " "), " ");
        return Changing.IsMatch(bare);
    }
}
