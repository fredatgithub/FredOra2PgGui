using Newtonsoft.Json;

namespace FredOra2PgGui;

public sealed class AppSettings
{
  public double WindowLeft { get; set; } = double.NaN;
  public double WindowTop { get; set; } = double.NaN;
  public double WindowWidth { get; set; } = 1080;
  public double WindowHeight { get; set; } = 720;
  public bool IsMaximized { get; set; }
  public DatabaseSettings Oracle { get; set; } = new();
  public DatabaseSettings PostgreSql { get; set; } = new();
}

public sealed class DatabaseSettings
{
  [JsonIgnore]
  public string Host { get; set; } = string.Empty;
  public int Port { get; set; }
  [JsonIgnore]
  public string Database { get; set; } = string.Empty;
  [JsonIgnore]
  public string Username { get; set; } = string.Empty;
  public string EncryptedHost { get; set; } = string.Empty;
  public string EncryptedDatabase { get; set; } = string.Empty;
  public string EncryptedUsername { get; set; } = string.Empty;
  public string EncryptedPassword { get; set; } = string.Empty;

  [JsonIgnore]
  public string Password { get; set; } = string.Empty;
}

public sealed class DatabaseObjectPair
{
  public string SourceName { get; set; } = string.Empty;
  public string TargetName { get; set; } = string.Empty;
}
