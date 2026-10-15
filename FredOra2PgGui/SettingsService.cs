using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FredOra2PgGui;

public sealed class SettingsService
{
  private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FredOra2PgGui");
  private readonly string _settingsPath;

  public SettingsService()
  {
    _settingsPath = Path.Combine(GetApplicationDirectory(), "settings.json");
  }

  public AppSettings Load()
  {
    if (!File.Exists(_settingsPath))
    {
      var previousSettingsPath = GetPreviousSettingsPath();
      if (File.Exists(previousSettingsPath))
      {
        var previousSettings = ReadSettings(previousSettingsPath);
        Save(previousSettings);
        File.Delete(previousSettingsPath);
        return previousSettings;
      }

      return new AppSettings();
    }

    return ReadSettings(_settingsPath);
  }

  public void Save(AppSettings settings)
  {
    Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
    settings.Oracle.EncryptedHost = Encrypt(settings.Oracle.Host);
    settings.Oracle.EncryptedDatabase = Encrypt(settings.Oracle.Database);
    settings.Oracle.EncryptedUsername = Encrypt(settings.Oracle.Username);
    settings.Oracle.EncryptedPassword = Encrypt(settings.Oracle.Password);
    settings.PostgreSql.EncryptedHost = Encrypt(settings.PostgreSql.Host);
    settings.PostgreSql.EncryptedDatabase = Encrypt(settings.PostgreSql.Database);
    settings.PostgreSql.EncryptedUsername = Encrypt(settings.PostgreSql.Username);
    settings.PostgreSql.EncryptedPassword = Encrypt(settings.PostgreSql.Password);
    var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
    File.WriteAllText(_settingsPath, json, new UTF8Encoding(false));
  }

  public static string GetApplicationDirectory()
  {
    return AppContext.BaseDirectory;
  }

  private static string GetPreviousSettingsPath()
  {
    return Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "FredOra2PgGui",
      "settings.json");
  }

  private static AppSettings ReadSettings(string path)
  {
    var json = JObject.Parse(File.ReadAllText(path));
    var settings = json.ToObject<AppSettings>() ?? new AppSettings();
    settings.Oracle ??= new DatabaseSettings();
    settings.PostgreSql ??= new DatabaseSettings();
    LoadDatabaseSettings(settings.Oracle, json["Oracle"]);
    LoadDatabaseSettings(settings.PostgreSql, json["PostgreSql"]);
    return settings;
  }

  private static void LoadDatabaseSettings(DatabaseSettings settings, JToken? json)
  {
    settings.Host = ReadValue(json, "EncryptedHost", "Host");
    settings.Database = ReadValue(json, "EncryptedDatabase", "Database");
    settings.Username = ReadValue(json, "EncryptedUsername", "Username");
    settings.Password = ReadValue(json, "EncryptedPassword", "Password");
  }

  private static string ReadValue(JToken? json, string encryptedName, string legacyName)
  {
    if (json is JObject databaseJson &&
        databaseJson.TryGetValue(encryptedName, StringComparison.OrdinalIgnoreCase, out var encryptedValue))
    {
      return Decrypt(encryptedValue.Value<string>() ?? string.Empty);
    }

    return json?[legacyName]?.Value<string>() ?? string.Empty;
  }

  private static string Encrypt(string value)
  {
    if (string.IsNullOrEmpty(value))
    {
      return string.Empty;
    }

    var encrypted = ProtectedData.Protect(
      Encoding.UTF8.GetBytes(value),
      Entropy,
      DataProtectionScope.CurrentUser);
    return Convert.ToBase64String(encrypted);
  }

  private static string Decrypt(string encryptedValue)
  {
    if (string.IsNullOrEmpty(encryptedValue))
    {
      return string.Empty;
    }

    var decrypted = ProtectedData.Unprotect(
      Convert.FromBase64String(encryptedValue),
      Entropy,
      DataProtectionScope.CurrentUser);
    return Encoding.UTF8.GetString(decrypted);
  }
}
