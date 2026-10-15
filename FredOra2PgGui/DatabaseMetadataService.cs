using System.Data.Common;
using Oracle.ManagedDataAccess.Client;
using Npgsql;

namespace FredOra2PgGui;

public enum DatabaseObjectType
{
  Tables,
  Views,
  Triggers,
  Procedures,
  Sequences
}

public sealed class DatabaseMetadataService
{
  public async Task<IReadOnlyList<string>> GetOracleObjectsAsync(
    DatabaseSettings settings,
    DatabaseObjectType objectType)
  {
    await using var connection = new OracleConnection(BuildOracleConnectionString(settings));
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = objectType switch
    {
      DatabaseObjectType.Tables =>
        "SELECT owner || '.' || table_name FROM all_tables WHERE owner NOT IN ('SYS', 'SYSTEM') ORDER BY owner, table_name",
      DatabaseObjectType.Views =>
        "SELECT owner || '.' || view_name FROM all_views WHERE owner NOT IN ('SYS', 'SYSTEM') ORDER BY owner, view_name",
      DatabaseObjectType.Triggers =>
        "SELECT owner || '.' || trigger_name FROM all_triggers WHERE owner NOT IN ('SYS', 'SYSTEM') ORDER BY owner, trigger_name",
      DatabaseObjectType.Procedures =>
        "SELECT DISTINCT owner || '.' || procedure_name FROM all_procedures WHERE procedure_name IS NOT NULL AND owner NOT IN ('SYS', 'SYSTEM') ORDER BY owner || '.' || procedure_name",
      DatabaseObjectType.Sequences =>
        "SELECT sequence_owner || '.' || sequence_name FROM all_sequences WHERE sequence_owner NOT IN ('SYS', 'SYSTEM') ORDER BY sequence_owner, sequence_name",
      _ => throw new ArgumentOutOfRangeException(nameof(objectType))
    };

    return await ReadNamesAsync(command);
  }

  public async Task<IReadOnlyList<string>> GetPostgreSqlObjectsAsync(
    DatabaseSettings settings,
    DatabaseObjectType objectType)
  {
    await using var connection = new NpgsqlConnection(BuildPostgreSqlConnectionString(settings));
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = objectType switch
    {
      DatabaseObjectType.Tables =>
        "SELECT table_schema || '.' || table_name FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY table_schema, table_name",
      DatabaseObjectType.Views =>
        "SELECT table_schema || '.' || table_name FROM information_schema.views WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY table_schema, table_name",
      DatabaseObjectType.Triggers =>
        "SELECT DISTINCT event_object_schema || '.' || trigger_name FROM information_schema.triggers WHERE trigger_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY event_object_schema || '.' || trigger_name",
      DatabaseObjectType.Procedures =>
        "SELECT routine_schema || '.' || routine_name FROM information_schema.routines WHERE routine_type = 'PROCEDURE' AND routine_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY routine_schema, routine_name",
      DatabaseObjectType.Sequences =>
        "SELECT sequence_schema || '.' || sequence_name FROM information_schema.sequences WHERE sequence_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY sequence_schema, sequence_name",
      _ => throw new ArgumentOutOfRangeException(nameof(objectType))
    };

    return await ReadNamesAsync(command);
  }

  public async Task TestOracleConnectionAsync(DatabaseSettings settings)
  {
    await using var connection = new OracleConnection(BuildOracleConnectionString(settings));
    await connection.OpenAsync();
  }

  public async Task TestPostgreSqlConnectionAsync(DatabaseSettings settings)
  {
    await using var connection = new NpgsqlConnection(BuildPostgreSqlConnectionString(settings));
    await connection.OpenAsync();
  }

  private static string BuildOracleConnectionString(DatabaseSettings settings)
  {
    var builder = new OracleConnectionStringBuilder
    {
      DataSource = $"//{settings.Host}:{settings.Port}/{settings.Database}",
      UserID = settings.Username,
      Password = settings.Password
    };
    return builder.ConnectionString;
  }

  private static string BuildPostgreSqlConnectionString(DatabaseSettings settings)
  {
    var builder = new NpgsqlConnectionStringBuilder
    {
      Host = settings.Host,
      Port = settings.Port,
      Database = settings.Database,
      Username = settings.Username,
      Password = settings.Password
    };
    return builder.ConnectionString;
  }

  private static async Task<IReadOnlyList<string>> ReadNamesAsync(DbCommand command)
  {
    var names = new List<string>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
      names.Add(reader.GetString(0));
    }

    return names;
  }
}
