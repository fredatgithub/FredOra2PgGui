using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
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

  private async void LoadObjectsButton_Click(object sender, RoutedEventArgs e)
  {
    LoadObjectsButton.Background = PendingActionBrush;
    try
    {
      var oracle = CaptureOracleSettings();
      var postgreSql = CapturePostgreSqlSettings();
      ConnectionStatusTextBlock.Text = "Chargement des objets des deux bases...";
      SetConnectionControlsEnabled(false);

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
      PostgreSql = CapturePostgreSqlSettings()
    };
  }

  private DatabaseSettings CaptureOracleSettings()
  {
    return new DatabaseSettings
    {
      Host = OracleHostTextBox.Text.Trim(),
      Port = ReadPort(OraclePortTextBox.Text, "Oracle"),
      Database = OracleServiceTextBox.Text.Trim(),
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
    OracleHostTextBox.Text = settings.Oracle.Host;
    OraclePortTextBox.Text = settings.Oracle.Port > 0 ? settings.Oracle.Port.ToString(CultureInfo.InvariantCulture) : "1521";
    OracleServiceTextBox.Text = settings.Oracle.Database;
    OracleUsernameTextBox.Text = settings.Oracle.Username;
    OraclePasswordBox.Password = settings.Oracle.Password;

    PostgresHostTextBox.Text = settings.PostgreSql.Host;
    PostgresPortTextBox.Text = settings.PostgreSql.Port > 0 ? settings.PostgreSql.Port.ToString(CultureInfo.InvariantCulture) : "5432";
    PostgresDatabaseTextBox.Text = settings.PostgreSql.Database;
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
    OracleUsernameTextBox.IsEnabled = enabled;
    OraclePasswordBox.IsEnabled = enabled;
    PostgresHostTextBox.IsEnabled = enabled;
    PostgresPortTextBox.IsEnabled = enabled;
    PostgresDatabaseTextBox.IsEnabled = enabled;
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
