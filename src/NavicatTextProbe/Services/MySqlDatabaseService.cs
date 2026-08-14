using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Text;
using MySqlConnector;
using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed class MySqlDatabaseService
{
    private const int MaximumCatalogTables = 5_000;
    private const int MaximumCatalogColumns = 30_000;
    private const int MaximumCatalogRelationships = 10_000;
    private const int MaximumCatalogCharacters = 120_000;
    internal static readonly TimeSpan CatalogCacheLifetime = TimeSpan.FromMinutes(15);
    private static readonly string[] SensitiveColumnMarkers =
    [
        "password", "passwd", "secret", "token", "api_key", "apikey", "private_key",
        "id_card", "identity_card", "身份证", "credential", "phone", "mobile", "email",
        "address", "bank_card", "credit_card", "ssn"
    ];

    private readonly ConcurrentDictionary<string, CatalogCacheEntry> _catalogCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _catalogGate = new(1, 1);

    public async Task<DatabaseTestResult> TestConnectionAsync(
        DatabaseConnectionSettings settings,
        string password,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new MySqlConnection(BuildConnectionString(settings, password));
            await connection.OpenAsync(cancellationToken);
            var tlsActive = false;
            await using (var command = new MySqlCommand("SHOW STATUS LIKE 'Ssl_cipher'", connection)
            {
                CommandTimeout = settings.QueryTimeoutSeconds
            })
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    tlsActive = !reader.IsDBNull(1) && !string.IsNullOrWhiteSpace(reader.GetString(1));
                }
            }

            var visibleSchemas = 0;
            var visibleTables = 0;
            const string catalogCountSql = """
                SELECT COUNT(DISTINCT t.TABLE_SCHEMA), COUNT(*)
                FROM information_schema.TABLES AS t
                WHERE t.TABLE_SCHEMA NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
                """;
            await StartReadOnlyTransactionAsync(connection, settings.QueryTimeoutSeconds, cancellationToken);
            try
            {
                await using var command = new MySqlCommand(catalogCountSql, connection)
                {
                    CommandTimeout = settings.QueryTimeoutSeconds
                };
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    visibleSchemas = reader.GetInt32(0);
                    visibleTables = reader.GetInt32(1);
                }
            }
            finally
            {
                await RollbackQuietlyAsync(connection, settings.QueryTimeoutSeconds);
            }

            return new DatabaseTestResult(
                true,
                $"连接成功，可见 {visibleSchemas} 个业务库 / {visibleTables} 个表或视图；" +
                (tlsActive ? "当前连接已使用 TLS。" : "当前连接未使用 TLS。"),
                connection.ServerVersion,
                connection.Database,
                tlsActive);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DatabaseTestResult(false, CompactError(ex.Message), string.Empty, settings.Database, false);
        }
    }

    public async Task<DatabaseCatalogSnapshot> GetCatalogSnapshotAsync(
        DatabaseConnectionSettings settings,
        string password,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var cacheKey = BuildCacheKey(settings);
        if (!forceRefresh &&
            _catalogCache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Snapshot;
        }

        await _catalogGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh &&
                _catalogCache.TryGetValue(cacheKey, out cached) &&
                cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached.Snapshot;
            }

            var snapshot = await LoadCatalogSnapshotAsync(settings, password, cancellationToken);
            _catalogCache[cacheKey] = new CatalogCacheEntry(snapshot, DateTimeOffset.UtcNow.Add(CatalogCacheLifetime));
            return snapshot;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    public void ClearCatalogCache() => _catalogCache.Clear();

    private static async Task<DatabaseCatalogSnapshot> LoadCatalogSnapshotAsync(
        DatabaseConnectionSettings settings,
        string password,
        CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(BuildConnectionString(settings, password));
        await connection.OpenAsync(cancellationToken);
        await StartReadOnlyTransactionAsync(connection, settings.QueryTimeoutSeconds, cancellationToken);
        try
        {
            var catalogTimeout = Math.Clamp(settings.QueryTimeoutSeconds * 2, 5, 20);
            var tables = new Dictionary<(string Schema, string Table), CatalogTable>();
            const string tablesSql = """
                SELECT t.TABLE_SCHEMA, t.TABLE_NAME, t.TABLE_TYPE, t.TABLE_ROWS
                FROM information_schema.TABLES AS t
                WHERE t.TABLE_SCHEMA NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
                ORDER BY CASE WHEN @preferred <> '' AND t.TABLE_SCHEMA = @preferred THEN 0 ELSE 1 END,
                         t.TABLE_SCHEMA, t.TABLE_NAME
                LIMIT 5000
                """;
            await using (var command = new MySqlCommand(tablesSql, connection)
            {
                CommandTimeout = catalogTimeout
            })
            {
                command.Parameters.AddWithValue("@preferred", settings.Database ?? string.Empty);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var schema = reader.GetString(0);
                    var table = reader.GetString(1);
                    long? estimatedRows = reader.IsDBNull(3)
                        ? null
                        : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture);
                    tables[(schema, table)] = new CatalogTable(
                        schema,
                        table,
                        reader.GetString(2),
                        estimatedRows,
                        []);
                }
            }

            var columnCount = 0;
            const string columnsSql = """
                SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME,
                       CASE WHEN CHAR_LENGTH(c.COLUMN_TYPE) <= 80 THEN c.COLUMN_TYPE ELSE c.DATA_TYPE END,
                       c.IS_NULLABLE, c.COLUMN_KEY, c.EXTRA
                FROM information_schema.COLUMNS AS c
                WHERE c.TABLE_SCHEMA NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
                ORDER BY CASE WHEN @preferred <> '' AND c.TABLE_SCHEMA = @preferred THEN 0 ELSE 1 END,
                         c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
                LIMIT 30000
                """;
            await using (var command = new MySqlCommand(columnsSql, connection)
            {
                CommandTimeout = catalogTimeout
            })
            {
                command.Parameters.AddWithValue("@preferred", settings.Database ?? string.Empty);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var key = (reader.GetString(0), reader.GetString(1));
                    if (!tables.TryGetValue(key, out var table)) continue;
                    table.Columns.Add(new CatalogColumn(
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4).Equals("YES", StringComparison.OrdinalIgnoreCase),
                        reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                        reader.IsDBNull(6) ? string.Empty : reader.GetString(6)));
                    columnCount++;
                }
            }

            var relationships = new List<CatalogRelationship>();
            const string relationshipsSql = """
                SELECT k.CONSTRAINT_SCHEMA, k.TABLE_NAME, k.COLUMN_NAME,
                       k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME
                FROM information_schema.KEY_COLUMN_USAGE AS k
                WHERE k.REFERENCED_TABLE_NAME IS NOT NULL
                  AND k.CONSTRAINT_SCHEMA NOT IN ('information_schema', 'mysql', 'performance_schema', 'sys')
                ORDER BY CASE WHEN @preferred <> '' AND k.CONSTRAINT_SCHEMA = @preferred THEN 0 ELSE 1 END,
                         k.CONSTRAINT_SCHEMA, k.TABLE_NAME, k.ORDINAL_POSITION
                LIMIT 10000
                """;
            await using (var command = new MySqlCommand(relationshipsSql, connection)
            {
                CommandTimeout = catalogTimeout
            })
            {
                command.Parameters.AddWithValue("@preferred", settings.Database ?? string.Empty);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    relationships.Add(new CatalogRelationship(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5)));
                }
            }

            return BuildCatalogSnapshot(
                tables.Values,
                relationships,
                columnCount,
                tables.Count >= MaximumCatalogTables ||
                columnCount >= MaximumCatalogColumns ||
                relationships.Count >= MaximumCatalogRelationships,
                settings.Database ?? string.Empty);
        }
        finally
        {
            await RollbackQuietlyAsync(connection, settings.QueryTimeoutSeconds);
        }
    }

    private static DatabaseCatalogSnapshot BuildCatalogSnapshot(
        IEnumerable<CatalogTable> source,
        IEnumerable<CatalogRelationship> relationshipSource,
        int columnCount,
        bool sourceTruncated,
        string preferredDatabase)
    {
        var tables = source
            .OrderBy(table => string.Equals(table.Schema, preferredDatabase, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(table => table.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var schemas = tables
            .GroupBy(table => table.Schema, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var relationships = relationshipSource
            .OrderBy(relation => string.Equals(relation.Schema, preferredDatabase, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(relation => relation.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(relation => relation.Table, StringComparer.OrdinalIgnoreCase)
            .ThenBy(relation => relation.Column, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var builder = new StringBuilder();
        var outputTruncated = sourceTruncated;

        bool Append(string value)
        {
            if (builder.Length + value.Length > MaximumCatalogCharacters)
            {
                outputTruncated = true;
                return false;
            }
            builder.Append(value);
            return true;
        }

        Append($"可访问数据库目录：{schemas.Length} 个库，{tables.Length} 个表/视图，已读取 {columnCount} 个字段。" +
               (string.IsNullOrWhiteSpace(preferredDatabase) ? "\n" : $"优先数据库：`{EscapeIdentifier(preferredDatabase)}`。\n"));
        Append("表名总览（rows≈ 为存储引擎统计估值，不是实时精确计数）：\n");
        foreach (var schema in schemas)
        {
            var tableList = string.Join(", ", schema.Select(table =>
                $"`{EscapeIdentifier(table.Name)}`{FormatEstimatedRows(table.EstimatedRows)}"));
            if (!Append($"SCHEMA `{EscapeIdentifier(schema.Key)}`: {tableList}\n")) break;
        }

        if (relationships.Length > 0)
        {
            Append("\n外键关系：\n");
            foreach (var relation in relationships)
            {
                if (!Append($"`{EscapeIdentifier(relation.Schema)}`.`{EscapeIdentifier(relation.Table)}`." +
                            $"`{EscapeIdentifier(relation.Column)}` -> " +
                            $"`{EscapeIdentifier(relation.ReferencedSchema)}`.`{EscapeIdentifier(relation.ReferencedTable)}`." +
                            $"`{EscapeIdentifier(relation.ReferencedColumn)}`\n"))
                {
                    break;
                }
            }
        }

        Append("\n字段详情（优先数据库排在前面）：\n");
        foreach (var table in tables)
        {
            var columns = table.Columns.Count == 0
                ? "（字段不可见或目录已裁剪）"
                : string.Join(", ", table.Columns.Select(FormatColumn));
            var type = table.Type.Equals("VIEW", StringComparison.OrdinalIgnoreCase) ? "VIEW" : "TABLE";
            if (!Append($"{type} `{EscapeIdentifier(table.Schema)}`.`{EscapeIdentifier(table.Name)}`" +
                        $"{FormatEstimatedRows(table.EstimatedRows)}: {columns}\n"))
            {
                break;
            }
        }
        if (outputTruncated)
        {
            const string marker = "\n[目录已按本地安全预算裁剪；需要精确细节时可调用只读数据库工具查询 information_schema。]";
            if (builder.Length + marker.Length <= MaximumCatalogCharacters) builder.Append(marker);
        }

        return new DatabaseCatalogSnapshot
        {
            Summary = builder.ToString(),
            SchemaCount = schemas.Length,
            TableCount = tables.Length,
            ColumnCount = columnCount,
            WasTruncated = outputTruncated
        };
    }

    public async Task<DatabaseContextResult> ExecuteReadOnlyAsync(
        DatabaseConnectionSettings settings,
        string password,
        DatabaseQueryPlan plan,
        string catalogSummary,
        CancellationToken cancellationToken)
    {
        if (!SafeMySqlQueryValidator.TryValidate(plan.Sql, out var safeSql, out var validationError))
        {
            return DatabaseContextResult.Failure(validationError, catalogSummary);
        }

        try
        {
            await using var connection = new MySqlConnection(BuildConnectionString(settings, password));
            await connection.OpenAsync(cancellationToken);
            await StartReadOnlyTransactionAsync(connection, settings.QueryTimeoutSeconds, cancellationToken);
            try
            {
                await using var command = new MySqlCommand(safeSql, connection)
                {
                    CommandTimeout = settings.QueryTimeoutSeconds
                };
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var summary = new StringBuilder();
                var columns = Enumerable.Range(0, reader.FieldCount)
                    .Select(reader.GetName)
                    .ToArray();
                var baseColumns = reader.GetColumnSchema()
                    .Select(column => column.BaseColumnName ?? string.Empty)
                    .ToArray();
                summary.AppendLine(string.Join(" | ", columns.Select(EscapeCell)));
                summary.AppendLine(string.Join(" | ", columns.Select(_ => "---")));

                var rowCount = 0;
                var truncated = false;
                var byteCount = Encoding.UTF8.GetByteCount(summary.ToString());
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (rowCount >= settings.MaximumRows)
                    {
                        truncated = true;
                        break;
                    }

                    var cells = new string[reader.FieldCount];
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        cells[index] = IsSensitiveColumn(columns[index]) ||
                                       index < baseColumns.Length && IsSensitiveColumn(baseColumns[index])
                            ? "[MASKED]"
                            : FormatValue(reader, index);
                    }
                    var line = string.Join(" | ", cells.Select(EscapeCell)) + "\n";
                    var lineBytes = Encoding.UTF8.GetByteCount(line);
                    if (byteCount + lineBytes > settings.MaximumResultBytes)
                    {
                        truncated = true;
                        break;
                    }
                    summary.Append(line);
                    byteCount += lineBytes;
                    rowCount++;
                }

                return new DatabaseContextResult
                {
                    IsSuccess = true,
                    Message = truncated
                        ? $"数据库只读查询成功，结果已限制为 {rowCount} 行。"
                        : $"数据库只读查询成功，共读取 {rowCount} 行。",
                    SchemaSummary = catalogSummary,
                    Query = safeSql,
                    QueryReason = plan.Reason,
                    RowsSummary = summary.ToString(),
                    RowCount = rowCount,
                    WasTruncated = truncated
                };
            }
            finally
            {
                await RollbackQuietlyAsync(connection, settings.QueryTimeoutSeconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DatabaseContextResult.Failure($"数据库只读查询失败：{CompactError(ex.Message)}", catalogSummary);
        }
    }

    private static string BuildConnectionString(DatabaseConnectionSettings settings, string password)
    {
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Username))
        {
            throw new InvalidOperationException("请先填写数据库主机和用户名。");
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = settings.Host,
            Port = (uint)settings.Port,
            UserID = settings.Username,
            Password = password,
            Database = settings.Database,
            SslMode = ParseSslMode(settings.SslMode),
            ConnectionTimeout = (uint)settings.ConnectionTimeoutSeconds,
            DefaultCommandTimeout = (uint)settings.QueryTimeoutSeconds,
            Pooling = true,
            MinimumPoolSize = 0,
            MaximumPoolSize = 4,
            ConnectionReset = true,
            AllowLoadLocalInfile = false,
            AllowUserVariables = false
        };
        return builder.ConnectionString;
    }

    private static MySqlSslMode ParseSslMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "disabled" or "none" => MySqlSslMode.Disabled,
        "required" => MySqlSslMode.Required,
        "verifyca" => MySqlSslMode.VerifyCA,
        "verifyfull" => MySqlSslMode.VerifyFull,
        _ => MySqlSslMode.Preferred
    };

    private static async Task StartReadOnlyTransactionAsync(
        MySqlConnection connection,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("START TRANSACTION READ ONLY", connection)
        {
            CommandTimeout = timeoutSeconds
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RollbackQuietlyAsync(MySqlConnection connection, int timeoutSeconds)
    {
        try
        {
            await using var command = new MySqlCommand("ROLLBACK", connection)
            {
                CommandTimeout = timeoutSeconds
            };
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch
        {
            // Disposing the connection is the final fallback after cancellation or network loss.
        }
    }

    private static string FormatValue(MySqlDataReader reader, int index)
    {
        if (reader.IsDBNull(index)) return "NULL";
        var value = reader.GetValue(index);
        if (value is byte[]) return "[BINARY]";
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= 1_000 ? text : text[..1_000] + "…";
    }

    private static bool IsSensitiveColumn(string name) => SensitiveColumnMarkers.Any(marker =>
        name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string EscapeCell(string value) => value
        .Replace("|", "\\|", StringComparison.Ordinal)
        .ReplaceLineEndings(" ");

    private static string FormatColumn(CatalogColumn column)
    {
        var flags = new List<string>();
        if (!column.Nullable) flags.Add("NOT NULL");
        if (!string.IsNullOrWhiteSpace(column.Key)) flags.Add(column.Key);
        if (!string.IsNullOrWhiteSpace(column.Extra)) flags.Add(column.Extra);
        return $"`{EscapeIdentifier(column.Name)}` {column.Type}" +
               (flags.Count == 0 ? string.Empty : " " + string.Join(' ', flags));
    }

    private static string FormatEstimatedRows(long? rows) => rows is null
        ? string.Empty
        : $"(rows≈{rows.Value.ToString("N0", CultureInfo.InvariantCulture)})";

    private static string EscapeIdentifier(string value)
    {
        var normalized = new string(value
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray())
            .Replace("`", "``", StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= 256 ? normalized : normalized[..256] + "…";
    }

    private static string BuildCacheKey(DatabaseConnectionSettings settings) =>
        $"{settings.Host}|{settings.Port}|{settings.Username}|{settings.Database}|{settings.SslMode}";

    private static string CompactError(string message)
    {
        var compact = message.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 300 ? compact : compact[..300] + "…";
    }

    private sealed record CatalogTable(
        string Schema,
        string Name,
        string Type,
        long? EstimatedRows,
        List<CatalogColumn> Columns);

    private sealed record CatalogColumn(
        string Name,
        string Type,
        bool Nullable,
        string Key,
        string Extra);

    private sealed record CatalogRelationship(
        string Schema,
        string Table,
        string Column,
        string ReferencedSchema,
        string ReferencedTable,
        string ReferencedColumn);

    private sealed record CatalogCacheEntry(DatabaseCatalogSnapshot Snapshot, DateTimeOffset ExpiresAt);
}
