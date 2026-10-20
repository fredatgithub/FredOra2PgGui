using System.Data;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace FredOra2PgGui;

public sealed class SqlScriptGeneratorService
{
  public async Task<IReadOnlyList<string>> GenerateAsync(
    DatabaseSettings oracle,
    string targetDatabase,
    string targetSchema,
    string outputDirectory,
    IReadOnlyCollection<string> exportTypes,
    bool createDatabase,
    bool createSchema)
  {
    Directory.CreateDirectory(outputDirectory);
    targetSchema = string.IsNullOrWhiteSpace(targetSchema) ? "public" : targetSchema.Trim();
    var runStamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
    var fileNames = new List<string>();
    if (createDatabase)
    {
      fileNames.Add($"Script-00-CREATE_DATABASE-{runStamp}.sql");
    }

    if (createSchema)
    {
      fileNames.Add($"Script-01-CREATE_SCHEMA-{runStamp}.sql");
    }

    var selectedExports = new[] { "TABLE", "SEQUENCE", "VIEW", "INSERT" }
      .Where(exportType => exportTypes.Contains(exportType, StringComparer.OrdinalIgnoreCase))
      .ToArray();
    for (var index = 0; index < selectedExports.Length; index++)
    {
      fileNames.Add($"Script-{fileNames.Count:00}-{selectedExports[index]}-{runStamp}.sql");
    }

    var existingFiles = fileNames
      .Select(fileName => Path.Combine(outputDirectory, fileName))
      .Where(File.Exists)
      .ToArray();
    if (existingFiles.Length > 0)
    {
      throw new IOException(
        $"Des scripts portant le même horodatage existent déjà. Attendez une seconde avant de relancer la génération : {string.Join(", ", existingFiles)}");
    }

    var generatedFiles = new List<string>();

    if (createDatabase)
    {
      var path = Path.Combine(outputDirectory, fileNames[generatedFiles.Count]);
      await File.WriteAllTextAsync(
        path,
        $"CREATE DATABASE {QuoteIdentifier(targetDatabase)};{Environment.NewLine}",
        new UTF8Encoding(false));
      generatedFiles.Add(path);
    }

    if (createSchema)
    {
      var path = Path.Combine(outputDirectory, fileNames[generatedFiles.Count]);
      await File.WriteAllTextAsync(
        path,
        $"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(targetSchema)};{Environment.NewLine}",
        new UTF8Encoding(false));
      generatedFiles.Add(path);
    }

    if (exportTypes.Count == 0)
    {
      return generatedFiles;
    }

    await using var connection = new OracleConnection(BuildOracleConnectionString(oracle));
    await connection.OpenAsync();
    var tables = await GetTablesAsync(connection, oracle.Owner);
    var duplicateNames = tables
      .GroupBy(table => table.Name, StringComparer.OrdinalIgnoreCase)
      .Where(group => group.Count() > 1)
      .Select(group => group.Key)
      .ToArray();
    if (duplicateNames.Length > 0 &&
        (exportTypes.Contains("TABLE", StringComparer.OrdinalIgnoreCase) ||
         exportTypes.Contains("INSERT", StringComparer.OrdinalIgnoreCase)))
    {
      throw new InvalidOperationException(
        $"Plusieurs propriétaires Oracle possèdent des tables portant le même nom ({string.Join(", ", duplicateNames)}). " +
        "Renseignez le propriétaire Oracle dans l’onglet Connexions pour éviter les collisions dans le schéma PostgreSQL.");
    }

    if (exportTypes.Contains("TABLE", StringComparer.OrdinalIgnoreCase))
    {
      var path = Path.Combine(outputDirectory, fileNames[generatedFiles.Count]);
      await GenerateTablesAsync(connection, tables, targetSchema, path);
      generatedFiles.Add(path);
    }

    if (exportTypes.Contains("SEQUENCE", StringComparer.OrdinalIgnoreCase))
    {
      var path = Path.Combine(outputDirectory, fileNames[generatedFiles.Count]);
      await GenerateSequencesAsync(connection, oracle.Owner, targetSchema, path);
      generatedFiles.Add(path);
    }

    if (exportTypes.Contains("VIEW", StringComparer.OrdinalIgnoreCase))
    {
      var path = Path.Combine(outputDirectory, fileNames[generatedFiles.Count]);
      await GenerateViewsAsync(connection, oracle.Owner, targetSchema, path);
      generatedFiles.Add(path);
    }

    if (exportTypes.Contains("INSERT", StringComparer.OrdinalIgnoreCase))
    {
      var path = Path.Combine(outputDirectory, fileNames[generatedFiles.Count]);
      await GenerateDataAsync(connection, tables, targetSchema, path);
      generatedFiles.Add(path);
    }

    return generatedFiles;
  }

  private static async Task<List<OracleTable>> GetTablesAsync(OracleConnection connection, string owner)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = string.IsNullOrWhiteSpace(owner)
      ? "SELECT owner, table_name FROM all_tables WHERE owner NOT IN ('SYS', 'SYSTEM') ORDER BY owner, table_name"
      : "SELECT owner, table_name FROM all_tables WHERE owner = :owner ORDER BY owner, table_name";
    if (!string.IsNullOrWhiteSpace(owner))
    {
      command.Parameters.Add(new OracleParameter("owner", owner.Trim().ToUpperInvariant()));
    }

    var tables = new List<OracleTable>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
      tables.Add(new OracleTable(reader.GetString(0), reader.GetString(1)));
    }

    return tables;
  }

  private static async Task GenerateTablesAsync(
    OracleConnection connection,
    IReadOnlyList<OracleTable> tables,
    string targetSchema,
    string outputPath)
  {
    await using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
    await writer.WriteLineAsync("-- Tables extraites du dictionnaire Oracle.");
    await writer.WriteLineAsync();
    foreach (var table in tables)
    {
      var columns = await GetColumnsAsync(connection, table);
      if (columns.Count == 0)
      {
        continue;
      }

      var constraints = await GetConstraintsAsync(connection, table);
      await writer.WriteLineAsync($"CREATE TABLE {QuoteIdentifier(targetSchema)}.{QuoteIdentifier(table.Name)} (");
      var definitions = columns.Select(column =>
        $"  {QuoteIdentifier(column.Name)} {column.PostgreSqlType}{(column.Nullable ? string.Empty : " NOT NULL")}" +
        (string.IsNullOrWhiteSpace(column.DefaultValue) ? string.Empty : $" DEFAULT {TranslateDefault(column.DefaultValue)}")).ToList();
      foreach (var constraint in constraints)
      {
        var columnList = string.Join(", ", constraint.Columns.Select(QuoteIdentifier));
        if (constraint.Type == "P")
        {
          definitions.Add($"  CONSTRAINT {QuoteIdentifier(constraint.Name)} PRIMARY KEY ({columnList})");
        }
        else if (constraint.Type == "U")
        {
          definitions.Add($"  CONSTRAINT {QuoteIdentifier(constraint.Name)} UNIQUE ({columnList})");
        }
        else if (constraint.Type == "R" && !string.IsNullOrWhiteSpace(constraint.ReferencedTable))
        {
          var referencedColumns = string.Join(", ", constraint.ReferencedColumns.Select(QuoteIdentifier));
          definitions.Add(
            $"  CONSTRAINT {QuoteIdentifier(constraint.Name)} FOREIGN KEY ({columnList}) " +
            $"REFERENCES {QuoteIdentifier(targetSchema)}.{QuoteIdentifier(constraint.ReferencedTable)} ({referencedColumns})");
        }
      }

      for (var index = 0; index < definitions.Count; index++)
      {
        await writer.WriteLineAsync(definitions[index] + (index == definitions.Count - 1 ? string.Empty : ","));
      }

      await writer.WriteLineAsync(");");
      await writer.WriteLineAsync();
    }
  }

  private static async Task<List<OracleColumn>> GetColumnsAsync(OracleConnection connection, OracleTable table)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT column_name, data_type, data_length, char_length, data_precision, data_scale, nullable, data_default
      FROM all_tab_columns
      WHERE owner = :owner AND table_name = :table_name
      ORDER BY column_id
      """;
    command.Parameters.Add(new OracleParameter("owner", table.Owner));
    command.Parameters.Add(new OracleParameter("table_name", table.Name));

    var columns = new List<OracleColumn>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
      var dataType = reader.GetString(1);
      var dataLength = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
      var charLength = reader.IsDBNull(3)
        ? dataLength
        : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
      int? precision = reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
      int? scale = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture);
      var nullable = reader.GetString(6) == "Y";
      var defaultValue = reader.IsDBNull(7) ? null : reader.GetString(7).Trim();
      columns.Add(new OracleColumn(
        reader.GetString(0),
        MapOracleType(dataType, dataLength, charLength, precision, scale),
        nullable,
        defaultValue));
    }

    return columns;
  }

  private static async Task<List<OracleConstraint>> GetConstraintsAsync(OracleConnection connection, OracleTable table)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = """
      SELECT constraints.constraint_type,
             constraints.constraint_name,
             cols.column_name,
             referenced_constraints.table_name,
             referenced_cols.column_name
      FROM all_constraints constraints
      JOIN all_cons_columns cols
        ON cols.owner = constraints.owner
       AND cols.constraint_name = constraints.constraint_name
       AND cols.table_name = constraints.table_name
      LEFT JOIN all_constraints referenced_constraints
        ON referenced_constraints.owner = constraints.r_owner
       AND referenced_constraints.constraint_name = constraints.r_constraint_name
      LEFT JOIN all_cons_columns referenced_cols
        ON referenced_cols.owner = referenced_constraints.owner
       AND referenced_cols.constraint_name = referenced_constraints.constraint_name
       AND referenced_cols.table_name = referenced_constraints.table_name
       AND referenced_cols.position = cols.position
      WHERE constraints.owner = :owner
        AND constraints.table_name = :table_name
        AND constraints.constraint_type IN ('P', 'U', 'R')
      ORDER BY constraints.constraint_name, cols.position
      """;
    command.Parameters.Add(new OracleParameter("owner", table.Owner));
    command.Parameters.Add(new OracleParameter("table_name", table.Name));

    var constraints = new List<OracleConstraint>();
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
      var name = reader.GetString(1);
      var constraint = constraints.LastOrDefault(item => item.Name == name);
      if (constraint is null)
      {
        constraint = new OracleConstraint(
          reader.GetString(0),
          name,
          [],
          reader.IsDBNull(3) ? null : reader.GetString(3),
          []);
        constraints.Add(constraint);
      }

      constraint.Columns.Add(reader.GetString(2));
      if (!reader.IsDBNull(4))
      {
        constraint.ReferencedColumns.Add(reader.GetString(4));
      }
    }

    return constraints;
  }

  private static async Task GenerateSequencesAsync(
    OracleConnection connection,
    string owner,
    string targetSchema,
    string outputPath)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = string.IsNullOrWhiteSpace(owner)
      ? "SELECT sequence_owner, sequence_name, increment_by, last_number, min_value, max_value, cycle_flag FROM all_sequences WHERE sequence_owner NOT IN ('SYS', 'SYSTEM') ORDER BY sequence_owner, sequence_name"
      : "SELECT sequence_owner, sequence_name, increment_by, last_number, min_value, max_value, cycle_flag FROM all_sequences WHERE sequence_owner = :owner ORDER BY sequence_owner, sequence_name";
    if (!string.IsNullOrWhiteSpace(owner))
    {
      command.Parameters.Add(new OracleParameter("owner", owner.Trim().ToUpperInvariant()));
    }

    await using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
    await writer.WriteLineAsync("-- Séquences extraites du dictionnaire Oracle.");
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
      var name = reader.GetString(1);
      var increment = ReadInteger(reader.GetValue(2));
      var start = ReadInteger(reader.GetValue(3));
      var minimum = ReadInteger(reader.GetValue(4));
      var maximum = ReadInteger(reader.GetValue(5));
      var cycle = reader.GetString(6) == "Y" ? " CYCLE" : " NO CYCLE";
      await writer.WriteLineAsync(
        $"CREATE SEQUENCE {QuoteIdentifier(targetSchema)}.{QuoteIdentifier(name)} " +
        $"INCREMENT BY {increment} MINVALUE {minimum} MAXVALUE {maximum} START WITH {start}{cycle};");
    }
  }

  private static async Task GenerateViewsAsync(
    OracleConnection connection,
    string owner,
    string targetSchema,
    string outputPath)
  {
    await using var command = connection.CreateCommand();
    command.CommandText = string.IsNullOrWhiteSpace(owner)
      ? "SELECT owner, view_name, text FROM all_views WHERE owner NOT IN ('SYS', 'SYSTEM') ORDER BY owner, view_name"
      : "SELECT owner, view_name, text FROM all_views WHERE owner = :owner ORDER BY owner, view_name";
    if (!string.IsNullOrWhiteSpace(owner))
    {
      command.Parameters.Add(new OracleParameter("owner", owner.Trim().ToUpperInvariant()));
    }

    await using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
    await writer.WriteLineAsync("-- Vues Oracle : vérifier la compatibilité PostgreSQL avant exécution.");
    await writer.WriteLineAsync($"SET search_path TO {QuoteIdentifier(targetSchema)};");
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
      var viewName = reader.GetString(1);
      var definition = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim().TrimEnd(';');
      definition = TranslateViewSql(definition);
      await writer.WriteLineAsync($"CREATE OR REPLACE VIEW {QuoteIdentifier(targetSchema)}.{QuoteIdentifier(viewName)} AS");
      await writer.WriteLineAsync(definition + ";");
      await writer.WriteLineAsync();
    }
  }

  private static async Task GenerateDataAsync(
    OracleConnection connection,
    IReadOnlyList<OracleTable> tables,
    string targetSchema,
    string outputPath)
  {
    await using var writer = new StreamWriter(outputPath, false, new UTF8Encoding(false));
    await writer.WriteLineAsync("-- Données Oracle générées sous forme d'instructions INSERT.");
    foreach (var table in tables)
    {
      var columns = await GetColumnsAsync(connection, table);
      if (columns.Count == 0)
      {
        continue;
      }

      await using var command = connection.CreateCommand();
      command.CommandText =
        $"SELECT {string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)))} " +
        $"FROM {QuoteIdentifier(table.Owner)}.{QuoteIdentifier(table.Name)}";
      await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
      var insertPrefix =
        $"INSERT INTO {QuoteIdentifier(targetSchema)}.{QuoteIdentifier(table.Name)} " +
        $"({string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name)))}) VALUES (";
      while (await reader.ReadAsync())
      {
        var values = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
          values[index] = reader.IsDBNull(index) ? "NULL" : FormatSqlLiteral(reader.GetValue(index));
        }

        await writer.WriteLineAsync(insertPrefix + string.Join(", ", values) + ");");
      }
    }
  }

  private static string MapOracleType(
    string dataType,
    int dataLength,
    int charLength,
    int? precision,
    int? scale)
  {
    return dataType.ToUpperInvariant() switch
    {
      "NUMBER" => MapNumberType(precision, scale),
      "FLOAT" => precision is > 0 ? $"numeric({precision})" : "double precision",
      "BINARY_FLOAT" => "real",
      "BINARY_DOUBLE" => "double precision",
      "VARCHAR2" or "VARCHAR" => $"varchar({Math.Max(charLength, 1)})",
      "NVARCHAR2" => $"varchar({Math.Max(charLength, 1)})",
      "CHAR" or "NCHAR" => $"char({Math.Max(charLength, 1)})",
      "CLOB" or "NCLOB" or "LONG" => "text",
      "BLOB" or "RAW" or "LONG RAW" => "bytea",
      "DATE" => "timestamp without time zone",
      "TIMESTAMP WITH TIME ZONE" or "TIMESTAMP WITH LOCAL TIME ZONE" => "timestamp with time zone",
      var type when type.StartsWith("TIMESTAMP", StringComparison.Ordinal) &&
                    type.Contains("WITH TIME ZONE", StringComparison.Ordinal) => "timestamp with time zone",
      var type when type.StartsWith("TIMESTAMP", StringComparison.Ordinal) => "timestamp without time zone",
      "INTERVAL YEAR TO MONTH" or "INTERVAL DAY TO SECOND" => "interval",
      "XMLTYPE" => "xml",
      "ROWID" or "UROWID" => "text",
      _ => throw new NotSupportedException(
        $"Le type Oracle {dataType} (longueur {dataLength}) n’a pas encore de conversion SQL PostgreSQL.")
    };
  }

  private static string MapNumberType(int? precision, int? scale)
  {
    if (scale is not null and not 0)
    {
      return precision is > 0 ? $"numeric({precision},{scale})" : "numeric";
    }

    if (precision is null or 0)
    {
      return "numeric";
    }

    return precision switch
    {
      <= 4 => "smallint",
      <= 9 => "integer",
      <= 18 => "bigint",
      _ => $"numeric({precision})"
    };
  }

  private static string ReadInteger(object value)
  {
    var text = value is OracleDecimal oracleDecimal
      ? oracleDecimal.ToString()
      : Convert.ToString(value, CultureInfo.InvariantCulture);
    return BigInteger.Parse(text!, NumberStyles.Integer, CultureInfo.InvariantCulture)
      .ToString(CultureInfo.InvariantCulture);
  }

  private static string TranslateDefault(string value)
  {
    var translated = value.Trim();
    if (translated.Equals("SYSDATE", StringComparison.OrdinalIgnoreCase) ||
        translated.Equals("SYSTIMESTAMP", StringComparison.OrdinalIgnoreCase))
    {
      return "CURRENT_TIMESTAMP";
    }

    if (Regex.IsMatch(translated, @"^\w+\.NEXTVAL$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
    {
      var sequence = translated[..translated.LastIndexOf('.')];
      return $"nextval('{sequence.Replace("'", "''", StringComparison.Ordinal)}')";
    }

    return translated;
  }

  private static string TranslateViewSql(string definition)
  {
    return Regex.Replace(
      Regex.Replace(definition, @"\bNVL\s*\(", "COALESCE(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
      @"\bSYSDATE\b",
      "CURRENT_TIMESTAMP",
      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
  }

  private static string FormatSqlLiteral(object value)
  {
    return value switch
    {
      string text => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
      char character => $"'{(character == '\'' ? "''" : character.ToString())}'",
      bool boolean => boolean ? "TRUE" : "FALSE",
      byte[] bytes => $"decode('{Convert.ToHexString(bytes)}', 'hex')",
      DateTime dateTime => $"'{dateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}'",
      OracleDecimal number => number.ToString(),
      OracleString text => FormatSqlLiteral(text.Value),
      OracleDate date => FormatSqlLiteral(date.Value),
      OracleTimeStamp timestamp => FormatSqlLiteral(timestamp.Value),
      OracleTimeStampTZ timestamp => FormatSqlLiteral(timestamp.Value),
      OracleTimeStampLTZ timestamp => FormatSqlLiteral(timestamp.Value),
      OracleBlob blob => FormatSqlLiteral(blob.Value),
      OracleClob clob => FormatSqlLiteral(clob.Value),
      OracleBinary binary => FormatSqlLiteral(binary.Value),
      IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
      _ => $"'{value.ToString()?.Replace("'", "''", StringComparison.Ordinal)}'"
    };
  }

  private static string QuoteIdentifier(string identifier)
  {
    return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
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

  private sealed record OracleTable(string Owner, string Name);
  private sealed record OracleColumn(string Name, string PostgreSqlType, bool Nullable, string? DefaultValue);
  private sealed record OracleConstraint(
    string Type,
    string Name,
    List<string> Columns,
    string? ReferencedTable,
    List<string> ReferencedColumns);
}
