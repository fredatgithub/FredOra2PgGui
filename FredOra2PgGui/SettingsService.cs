using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

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
    settings.Oracle.EncryptedPassword = EncryptPassword(settings.Oracle.Password);
    settings.PostgreSql.EncryptedPassword = EncryptPassword(settings.PostgreSql.Password);
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
    var json = File.ReadAllText(path);
    var settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
    settings.Oracle ??= new DatabaseSettings();
    settings.PostgreSql ??= new DatabaseSettings();
    settings.Oracle.Password = DecryptPassword(settings.Oracle.EncryptedPassword);
    settings.PostgreSql.Password = DecryptPassword(settings.PostgreSql.EncryptedPassword);
    return settings;
  }

  private static string EncryptPassword(string password)
  {
    if (string.IsNullOrEmpty(password))
    {
      return string.Empty;
    }

    var encrypted = ProtectedData.Protect(
      Encoding.UTF8.GetBytes(password),
      Entropy,
      DataProtectionScope.CurrentUser);
    return Convert.ToBase64String(encrypted);
  }

  private static string DecryptPassword(string encryptedPassword)
  {
    if (string.IsNullOrEmpty(encryptedPassword))
    {
      return string.Empty;
    }

    var decrypted = ProtectedData.Unprotect(
      Convert.FromBase64String(encryptedPassword),
      Entropy,
      DataProtectionScope.CurrentUser);
    return Encoding.UTF8.GetString(decrypted);
  }
}
