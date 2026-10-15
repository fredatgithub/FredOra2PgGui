using System.IO;
using System.Globalization;
using System.Collections.Generic;
using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Layout;

namespace FredOra2PgGui;

public static class LoggingService
{
  private static readonly ILog Logger = LogManager.GetLogger(typeof(LoggingService));

  public static event Action<string>? LogMessage;

  public static void Initialize()
  {
    var directory = SettingsService.GetApplicationDirectory();
    Directory.CreateDirectory(directory);

    var layout = new PatternLayout
    {
      ConversionPattern = "%date{ISO8601} %-5level %message%newline%exception"
    };
    layout.ActivateOptions();

    var appender = new FileAppender
    {
      Name = "LocalLogFile",
      File = Path.Combine(directory, "fredora2pg.log"),
      AppendToFile = true,
      ImmediateFlush = true,
      LockingModel = new FileAppender.MinimalLock(),
      Layout = layout
    };
    appender.ActivateOptions();

    BasicConfigurator.Configure(appender);
    Info("Application démarrée.");
  }

  public static IReadOnlyList<string> ReadRecentEntries(int maximumEntries = 500)
  {
    var path = Path.Combine(SettingsService.GetApplicationDirectory(), "fredora2pg.log");
    if (!File.Exists(path) || maximumEntries <= 0)
    {
      return Array.Empty<string>();
    }

    var recentEntries = new Queue<string>(maximumEntries);
    foreach (var line in File.ReadLines(path))
    {
      if (recentEntries.Count == maximumEntries)
      {
        recentEntries.Dequeue();
      }

      recentEntries.Enqueue(line);
    }

    return recentEntries.ToArray();
  }

  public static void Info(string message)
  {
    Logger.Info(message);
    Publish(message);
  }

  public static void Error(string message, Exception exception)
  {
    Logger.Error(message, exception);
    Publish($"{message} - {exception.Message}");
  }

  private static void Publish(string message)
  {
    var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {message}";
    LogMessage?.Invoke(line);
  }
}
