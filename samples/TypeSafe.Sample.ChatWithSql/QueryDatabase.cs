using System.Text.Json.Nodes;
using Npgsql;

namespace TypeSafe.Sample.ChatWithSql;

public interface IQueryDatabase
{
    Task<string> ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

public sealed class PostgresQueryDatabase(NpgsqlDataSource source) : IQueryDatabase
{
    public async Task<string> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var identity = new NpgsqlCommand("SELECT current_user", connection);
        if ((string?)await identity.ExecuteScalarAsync(cancellationToken) != "sample_reader")
            throw new InvalidOperationException("Live SQL requires the sample_reader role from init.sql.");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
        await readOnly.ExecuteNonQueryAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 5 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new JsonArray();
        while (rows.Count < 20 && await reader.ReadAsync(cancellationToken))
        {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i, cancellationToken) ? null : reader.GetValue(i).ToString();
            rows.Add((JsonNode)row);
        }
        return rows.ToJsonString();
    }
}

public sealed class FixtureDatabase : IQueryDatabase
{
    public const string AllowedQuery = "SELECT name FROM customers ORDER BY id LIMIT 5";
    public int ExecutionCount { get; private set; }
    public Task<string> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecutionCount++;
        if (!string.Equals(sql.Trim().TrimEnd(';'), AllowedQuery, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Offline fixture supports only the documented customer query.");
        return Task.FromResult("""[{"name":"Acme"},{"name":"Nova"}]""");
    }
}
