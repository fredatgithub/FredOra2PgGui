using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FredOra2PgGui;

public partial class MainWindow : Window
{
  private readonly SettingsService _settingsService = new();
  private readonly DatabaseMetadataService _metadataService = new();
  private readonly ObservableCollection<string> _logEntries = [];
  private readonly ObservableCollection<GeneratedScript> _generatedScripts = [];
  private static readonly Brush PendingActionBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
  private static readonly Brush SuccessfulActionBrush = new SolidColorBrush(Color.FromRgb(40, 167, 69));
  private static readonly Brush FailedActionBrush = new SolidColorBrush(Color.FromRgb(220, 53, 69));
  private bool _settingsLoaded;

  public ObservableCollection<string> LogEntries => _logEntries;

  public MainWindow()
  {
    InitializeComponent();
    var version = Assembly.GetExecutingAssembly().GetName().Version;
    if (version is not null)
    {
      Title = $"Fred ora2pg GUI - v{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    DataContext = this;
    ScriptsDataGrid.ItemsSource = _generatedScripts;

    LoggingService.Initialize();
    LoggingService.LogMessage += AddLogEntry;
    foreach (var entry in LoggingService.ReadRecentEntries())
    {
      AddLogEntry(entry);
    }

    try
    {
      var settings = _settingsService.Load();
      ApplySettings(settings);
      ApplyWindowSettings(settings);
      RefreshGeneratedScripts(false);
      _settingsLoaded = true;
    }
    catch (Exception exception)
    {
      LoggingService.Error("Impossible de charger les paramètres locaux.", exception);
      MessageBox.Show(
        this,
        $"Les paramètres locaux n'ont pas pu être chargés. Vérifiez le journal.\n\n{exception.Message}",
        "Chargement des paramètres",
        MessageBoxButton.OK,
        MessageBoxImage.Warning);
    }
  }

  private async void TestOracleButton_Click(object sender, RoutedEventArgs e)
  {
    await TestConnectionAsync(
      "Oracle",
      TestOracleButton,
      () => _metadataService.TestOracleConnectionAsync(CaptureOracleSettings()));
  }

  private async void TestPostgresButton_Click(object sender, RoutedEventArgs e)
  {
    await TestConnectionAsync(
      "PostgreSQL",
      TestPostgresButton,
      () => _metadataService.TestPostgreSqlConnectionAsync(CapturePostgreSqlSettings()));
  }

  private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
  {
    SaveSettingsButton.Background = PendingActionBrush;
    try
    {
      SaveCurrentSettings();
      _settingsLoaded = true;
      SaveSettingsButton.Background = SuccessfulActionBrush;
      ConnectionStatusTextBlock.Text = "Paramètres enregistrés localement.";
    }
    catch (Exception exception)
    {
      SaveSettingsButton.Background = FailedActionBrush;
      LoggingService.Error("Impossible d'enregistrer les paramètres.", exception);
      ConnectionStatusTextBlock.Text = "Échec de l'enregistrement des paramètres.";
      MessageBox.Show(
        this,
        $"Les paramètres n'ont pas pu être enregistrés.\n\n{exception.Message}",
        "Enregistrement des paramètres",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }
  }

  private void BrowseOra2PgExecutableButton_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog
    {
      Title = "Sélectionner l’exécutable ora2pg",
      Filter = "Exécutables|*.exe;*.com|Tous les fichiers|*.*",
      CheckFileExists = true
    };

    if (dialog.ShowDialog(this) == true)
    {
      Ora2PgExecutableTextBox.Text = dialog.FileName;
    }
  }

  private void BrowseOra2PgConfigButton_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFileDialog
    {
      Title = "Sélectionner le fichier de configuration ora2pg",
      Filter = "Configuration ora2pg|*.conf;*.cfg|Tous les fichiers|*.*",
      CheckFileExists = true
    };

    if (dialog.ShowDialog(this) == true)
    {
      Ora2PgConfigFileTextBox.Text = dialog.FileName;
    }
  }

  private void BrowseScriptsOutputDirectoryButton_Click(object sender, RoutedEventArgs e)
  {
    var dialog = new OpenFolderDialog
    {
      Title = "Sélectionner le dossier de sortie des scripts",
      FolderName = ScriptsOutputDirectoryTextBox.Text
    };

    if (dialog.ShowDialog(this) == true)
    {
      ScriptsOutputDirectoryTextBox.Text = dialog.FolderName;
      RefreshGeneratedScripts(true);
    }
  }

  private void RefreshScriptsButton_Click(object sender, RoutedEventArgs e)
  {
    RefreshGeneratedScripts(true);
  }

  private async void CreateScriptsButton_Click(object sender, RoutedEventArgs e)
  {
    CreateScriptsButton.Background = PendingActionBrush;
    PleaseWaitWindow? waitWindow = null;

    try
    {
      var executable = Ora2PgExecutableTextBox.Text.Trim();
      var configFile = Ora2PgConfigFileTextBox.Text.Trim();
      var outputDirectory = ScriptsOutputDirectoryTextBox.Text.Trim();
      var exportType = (Ora2PgExportTypeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();

      if (string.IsNullOrWhiteSpace(executable))
      {
        throw new InvalidOperationException("Sélectionnez l’exécutable ora2pg ou indiquez son nom s’il est accessible dans le PATH.");
      }

      if (Path.IsPathRooted(executable) && !File.Exists(executable))
      {
        throw new FileNotFoundException("L’exécutable ora2pg sélectionné est introuvable.", executable);
      }

      if (string.IsNullOrWhiteSpace(configFile) || !File.Exists(configFile))
      {
        throw new FileNotFoundException("Sélectionnez un fichier de configuration ora2pg existant.", configFile);
      }

      if (string.IsNullOrWhiteSpace(outputDirectory))
      {
        throw new InvalidOperationException("Sélectionnez le dossier de sortie des scripts.");
      }

      if (string.IsNullOrWhiteSpace(exportType))
      {
        throw new InvalidOperationException("Sélectionnez un type d’export ora2pg.");
      }

      Directory.CreateDirectory(outputDirectory);
      var outputPath = Path.Combine(
        outputDirectory,
        $"ora2pg-{exportType}-{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.sql");
      var startInfo = new ProcessStartInfo
      {
        FileName = executable,
        WorkingDirectory = outputDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
      };
      startInfo.ArgumentList.Add("--conf");
      startInfo.ArgumentList.Add(configFile);
      startInfo.ArgumentList.Add("--type");
      startInfo.ArgumentList.Add(exportType);
      startInfo.ArgumentList.Add("--out");
      startInfo.ArgumentList.Add(outputPath);

      LoggingService.Info($"Démarrage d’ora2pg pour l’export {exportType}.");
      CreateScriptsButton.IsEnabled = false;
      RefreshScriptsButton.IsEnabled = false;
      waitWindow = new PleaseWaitWindow { Owner = this };
      waitWindow.Show();
      IsEnabled = false;

      using var process = new Process { StartInfo = startInfo };
      if (!process.Start())
      {
        throw new InvalidOperationException("Le processus ora2pg n’a pas pu démarrer.");
      }

      var standardOutputTask = process.StandardOutput.ReadToEndAsync();
      var standardErrorTask = process.StandardError.ReadToEndAsync();
      await process.WaitForExitAsync();
      var standardOutput = await standardOutputTask;
      var standardError = await standardErrorTask;
      if (process.ExitCode != 0)
      {
        var details = string.Join(
          Environment.NewLine,
          new[] { standardError.Trim(), standardOutput.Trim() }.Where(text => !string.IsNullOrWhiteSpace(text)));
        throw new InvalidOperationException(
          $"ora2pg a terminé avec le code {process.ExitCode}.{Environment.NewLine}{details}".Trim());
      }

      if (!File.Exists(outputPath))
      {
        throw new InvalidOperationException(
          $"ora2pg s’est terminé sans erreur, mais le script attendu n’a pas été créé : {outputPath}");
      }

      RefreshGeneratedScripts(false);
      CreateScriptsButton.Background = SuccessfulActionBrush;
      LoggingService.Info($"Script ora2pg créé : {outputPath}");
      MessageBox.Show(
        this,
        $"Script créé avec succès :{Environment.NewLine}{outputPath}",
        "Création des scripts",
        MessageBoxButton.OK,
        MessageBoxImage.Information);
    }
    catch (Exception exception)
    {
      CreateScriptsButton.Background = FailedActionBrush;
      LoggingService.Error("Échec de la génération du script ora2pg.", exception);
      MessageBox.Show(
        this,
        $"Le script n’a pas pu être créé.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
        "Création des scripts",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }
    finally
    {
      waitWindow?.Close();
      IsEnabled = true;
      CreateScriptsButton.IsEnabled = true;
      RefreshScriptsButton.IsEnabled = true;
    }
  }

  private async void LoadObjectsButton_Click(object sender, RoutedEventArgs e)
  {
    LoadObjectsButton.Background = PendingActionBrush;
    var waitWindow = new PleaseWaitWindow
    {
      Owner = this
    };
    var waitWindowShown = false;

    try
    {
      var oracle = CaptureOracleSettings();
      var postgreSql = CapturePostgreSqlSettings();
      ConnectionStatusTextBlock.Text = "Chargement des objets des deux bases...";
      SetConnectionControlsEnabled(false);
      waitWindow.Show();
      waitWindowShown = true;
      IsEnabled = false;

      var objectTypes = Enum.GetValues<DatabaseObjectType>();
      var results = new Dictionary<DatabaseObjectType, (IReadOnlyList<string> Source, IReadOnlyList<string> Target)>();

      foreach (var objectType in objectTypes)
      {
        var source = await _metadataService.GetOracleObjectsAsync(oracle, objectType);
        var target = await _metadataService.GetPostgreSqlObjectsAsync(postgreSql, objectType);
        results[objectType] = (source, target);
      }

      TablesDataGrid.ItemsSource = CreatePairs(results[DatabaseObjectType.Tables]);
      ViewsDataGrid.ItemsSource = CreatePairs(results[DatabaseObjectType.Views]);
      TriggersDataGrid.ItemsSource = CreatePairs(results[DatabaseObjectType.Triggers]);
      ProceduresDataGrid.ItemsSource = CreatePairs(results[DatabaseObjectType.Procedures]);
      SequencesDataGrid.ItemsSource = CreatePairs(results[DatabaseObjectType.Sequences]);

      var count = results.Values.Sum(result => result.Source.Count + result.Target.Count);
      LoadObjectsButton.Background = SuccessfulActionBrush;
      ConnectionStatusTextBlock.Text = $"Inventaire chargé : {count} objets trouvés au total.";
      LoggingService.Info($"Inventaire des objets chargé ({count} objets).");
    }
    catch (Exception exception)
    {
      LoadObjectsButton.Background = FailedActionBrush;
      LoggingService.Error("Échec du chargement des objets des bases.", exception);
      ConnectionStatusTextBlock.Text = "Échec du chargement des objets. Consultez l'onglet Log.";
      MessageBox.Show(
        this,
        $"Impossible de récupérer l'inventaire des bases.\n\n{exception.Message}",
        "Chargement des objets",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }
    finally
    {
      if (waitWindowShown)
      {
        waitWindow.Close();
      }

      IsEnabled = true;
      SetConnectionControlsEnabled(true);
    }
  }

  private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
  {
    if (!_settingsLoaded)
    {
      LoggingService.Info("Fermeture sans sauvegarde des paramètres car leur chargement a échoué.");
      return;
    }

    try
    {
      SaveCurrentSettings();
      LoggingService.Info("Paramètres et dimensions de fenêtre enregistrés.");
    }
    catch (Exception exception)
    {
      LoggingService.Error("Impossible d'enregistrer les paramètres à la fermeture.", exception);
      MessageBox.Show(
        this,
        $"Les paramètres n'ont pas pu être enregistrés à la fermeture.\n\n{exception.Message}",
        "Enregistrement des paramètres",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }
  }

  private async Task TestConnectionAsync(string databaseName, Button testButton, Func<Task> testConnection)
  {
    testButton.Background = PendingActionBrush;
    try
    {
      ConnectionStatusTextBlock.Text = $"Test de connexion à {databaseName}...";
      SetConnectionControlsEnabled(false);
      await testConnection();
      testButton.Background = SuccessfulActionBrush;
      ConnectionStatusTextBlock.Text = $"Connexion à {databaseName} réussie.";
      LoggingService.Info($"Connexion à {databaseName} réussie.");
    }
    catch (Exception exception)
    {
      testButton.Background = FailedActionBrush;
      LoggingService.Error($"Échec de la connexion à {databaseName}.", exception);
      ConnectionStatusTextBlock.Text = $"Échec de la connexion à {databaseName}. Consultez l'onglet Log.";
      MessageBox.Show(
        this,
        $"La connexion à {databaseName} a échoué.\n\n{exception.Message}",
        "Test de connexion",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }
    finally
    {
      SetConnectionControlsEnabled(true);
    }
  }

  private void SaveCurrentSettings()
  {
    var settings = CreateSettings();
    var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
    settings.WindowLeft = bounds.Left;
    settings.WindowTop = bounds.Top;
    settings.WindowWidth = bounds.Width;
    settings.WindowHeight = bounds.Height;
    settings.IsMaximized = WindowState == WindowState.Maximized;
    _settingsService.Save(settings);
  }

  private AppSettings CreateSettings()
  {
    return new AppSettings
    {
      Oracle = CaptureOracleSettings(),
      PostgreSql = CapturePostgreSqlSettings(),
      Ora2PgExecutable = Ora2PgExecutableTextBox.Text.Trim(),
      Ora2PgConfigFile = Ora2PgConfigFileTextBox.Text.Trim(),
      ScriptsOutputDirectory = ScriptsOutputDirectoryTextBox.Text.Trim(),
      Ora2PgExportType = (Ora2PgExportTypeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "TABLE"
    };
  }

  private DatabaseSettings CaptureOracleSettings()
  {
    return new DatabaseSettings
    {
      Host = OracleHostTextBox.Text.Trim(),
      Port = ReadPort(OraclePortTextBox.Text, "Oracle"),
      Database = OracleServiceTextBox.Text.Trim(),
      Owner = OracleOwnerTextBox.Text.Trim(),
      Username = OracleUsernameTextBox.Text.Trim(),
      Password = OraclePasswordBox.Password
    };
  }

  private DatabaseSettings CapturePostgreSqlSettings()
  {
    return new DatabaseSettings
    {
      Host = PostgresHostTextBox.Text.Trim(),
      Port = ReadPort(PostgresPortTextBox.Text, "PostgreSQL"),
      Database = PostgresDatabaseTextBox.Text.Trim(),
      Schema = PostgresSchemaTextBox.Text.Trim(),
      Username = PostgresUsernameTextBox.Text.Trim(),
      Password = PostgresPasswordBox.Password
    };
  }

  private static int ReadPort(string value, string databaseName)
  {
    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
        port is < 1 or > 65535)
    {
      throw new InvalidOperationException($"Le port de {databaseName} doit être un entier compris entre 1 et 65535.");
    }

    return port;
  }

  private void ApplySettings(AppSettings settings)
  {
    Ora2PgExecutableTextBox.Text = settings.Ora2PgExecutable;
    Ora2PgConfigFileTextBox.Text = settings.Ora2PgConfigFile;
    ScriptsOutputDirectoryTextBox.Text = settings.ScriptsOutputDirectory;
    Ora2PgExportTypeComboBox.SelectedValue = settings.Ora2PgExportType;

    OracleHostTextBox.Text = settings.Oracle.Host;
    OraclePortTextBox.Text = settings.Oracle.Port > 0 ? settings.Oracle.Port.ToString(CultureInfo.InvariantCulture) : "1521";
    OracleServiceTextBox.Text = settings.Oracle.Database;
    OracleOwnerTextBox.Text = settings.Oracle.Owner;
    OracleUsernameTextBox.Text = settings.Oracle.Username;
    OraclePasswordBox.Password = settings.Oracle.Password;

    PostgresHostTextBox.Text = settings.PostgreSql.Host;
    PostgresPortTextBox.Text = settings.PostgreSql.Port > 0 ? settings.PostgreSql.Port.ToString(CultureInfo.InvariantCulture) : "5432";
    PostgresDatabaseTextBox.Text = settings.PostgreSql.Database;
    PostgresSchemaTextBox.Text = settings.PostgreSql.Schema;
    PostgresUsernameTextBox.Text = settings.PostgreSql.Username;
    PostgresPasswordBox.Password = settings.PostgreSql.Password;
  }

  private void ApplyWindowSettings(AppSettings settings)
  {
    var left = settings.WindowLeft;
    var top = settings.WindowTop;
    var width = settings.WindowWidth;
    var height = settings.WindowHeight;
    var virtualScreen = new Rect(
      SystemParameters.VirtualScreenLeft,
      SystemParameters.VirtualScreenTop,
      SystemParameters.VirtualScreenWidth,
      SystemParameters.VirtualScreenHeight);

    if (double.IsFinite(left) &&
        double.IsFinite(top) &&
        double.IsFinite(width) &&
        double.IsFinite(height) &&
        width >= MinWidth &&
        height >= MinHeight &&
        new Rect(left, top, width, height).IntersectsWith(virtualScreen))
    {
      WindowStartupLocation = WindowStartupLocation.Manual;
      Width = Math.Min(width, virtualScreen.Width);
      Height = Math.Min(height, virtualScreen.Height);
      Left = Math.Clamp(left, virtualScreen.Left, virtualScreen.Right - Width);
      Top = Math.Clamp(top, virtualScreen.Top, virtualScreen.Bottom - Height);
    }

    if (settings.IsMaximized)
    {
      WindowState = WindowState.Maximized;
    }
  }

  private void SetConnectionControlsEnabled(bool enabled)
  {
    TestOracleButton.IsEnabled = enabled;
    TestPostgresButton.IsEnabled = enabled;
    SaveSettingsButton.IsEnabled = enabled;
    LoadObjectsButton.IsEnabled = enabled;
    OracleHostTextBox.IsEnabled = enabled;
    OraclePortTextBox.IsEnabled = enabled;
    OracleServiceTextBox.IsEnabled = enabled;
    OracleOwnerTextBox.IsEnabled = enabled;
    OracleUsernameTextBox.IsEnabled = enabled;
    OraclePasswordBox.IsEnabled = enabled;
    PostgresHostTextBox.IsEnabled = enabled;
    PostgresPortTextBox.IsEnabled = enabled;
    PostgresDatabaseTextBox.IsEnabled = enabled;
    PostgresSchemaTextBox.IsEnabled = enabled;
    PostgresUsernameTextBox.IsEnabled = enabled;
    PostgresPasswordBox.IsEnabled = enabled;
  }

  private void AddLogEntry(string message)
  {
    if (!Dispatcher.CheckAccess())
    {
      Dispatcher.BeginInvoke(() => AddLogEntry(message));
      return;
    }

    _logEntries.Add(message);
    while (_logEntries.Count > 500)
    {
      _logEntries.RemoveAt(0);
    }

    if (LogListBox.Items.Count > 0)
    {
      LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
    }
  }

  private void RefreshGeneratedScripts(bool showErrors)
  {
    var outputDirectory = ScriptsOutputDirectoryTextBox.Text.Trim();
    if (string.IsNullOrWhiteSpace(outputDirectory))
    {
      _generatedScripts.Clear();
      return;
    }

    try
    {
      _generatedScripts.Clear();
      if (!Directory.Exists(outputDirectory))
      {
        return;
      }

      foreach (var path in Directory.EnumerateFiles(outputDirectory, "*.sql", SearchOption.AllDirectories)
                 .OrderByDescending(File.GetLastWriteTime))
      {
        var fileInfo = new FileInfo(path);
        var name = fileInfo.Name;
        var type = "SQL";
        if (name.StartsWith("ora2pg-", StringComparison.OrdinalIgnoreCase))
        {
          var typeEnd = name.IndexOf('-', "ora2pg-".Length);
          if (typeEnd > "ora2pg-".Length)
          {
            type = name["ora2pg-".Length..typeEnd];
          }
        }

        _generatedScripts.Add(new GeneratedScript
        {
          Name = name,
          Type = type,
          Location = fileInfo.FullName,
          Size = FormatFileSize(fileInfo.Length),
          Modified = fileInfo.LastWriteTime
        });
      }
    }
    catch (Exception exception)
    {
      LoggingService.Error("Impossible d’actualiser la liste des scripts.", exception);
      if (showErrors)
      {
        MessageBox.Show(
          this,
          $"La liste des scripts n’a pas pu être actualisée.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
          "Liste des scripts",
          MessageBoxButton.OK,
          MessageBoxImage.Error);
      }
    }
  }

  private static string FormatFileSize(long size)
  {
    string[] units = ["o", "Ko", "Mo", "Go", "To"];
    var value = (double)size;
    var unitIndex = 0;
    while (value >= 1024 && unitIndex < units.Length - 1)
    {
      value /= 1024;
      unitIndex++;
    }

    return $"{value:0.##} {units[unitIndex]}";
  }

  private void LogListBox_PreviewKeyDown(object sender, KeyEventArgs e)
  {
    if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
    {
      CopySelectedLogLine();
      e.Handled = true;
    }
  }

  private void CopyLogLineMenuItem_Click(object sender, RoutedEventArgs e)
  {
    CopySelectedLogLine();
  }

  private void CopySelectedLogLine()
  {
    if (LogListBox.SelectedItem is not string selectedLine)
    {
      return;
    }

    try
    {
      Clipboard.SetText(selectedLine);
    }
    catch (Exception exception)
    {
      LoggingService.Error("Impossible de copier la ligne du journal.", exception);
      MessageBox.Show(
        this,
        $"La ligne du journal n'a pas pu être copiée.\n\n{exception.Message}",
        "Copie du journal",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }
  }

  private static List<DatabaseObjectPair> CreatePairs(
    (IReadOnlyList<string> Source, IReadOnlyList<string> Target) names)
  {
    var count = Math.Max(names.Source.Count, names.Target.Count);
    var pairs = new List<DatabaseObjectPair>(count);
    for (var index = 0; index < count; index++)
    {
      pairs.Add(new DatabaseObjectPair
      {
        SourceName = index < names.Source.Count ? names.Source[index] : string.Empty,
        TargetName = index < names.Target.Count ? names.Target[index] : string.Empty
      });
    }

    return pairs;
  }
}
