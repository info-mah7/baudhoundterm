using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Security.KeePassXc;
using Quarterdeck.Core.Sql;
using Quarterdeck.Core.Terminal;

namespace Quarterdeck.App.Views;

/// <summary>
/// A SQL tab: a query editor over a results area, on one open connection. Runs the selection,
/// or everything when nothing is selected. Asks before statements that change data when the
/// shortcut says so, and shows at most <see cref="MaxRows"/> rows per result set.
/// </summary>
internal sealed class SqlView : Grid, IDisposable
{
    private const int MaxRows = 1000;

    private static readonly Brush Ground = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x27));
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xDF, 0xE4, 0xEE));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0xA7));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x75));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x98, 0xC3, 0x79));

    private readonly SessionDefinition _definition;
    private readonly CredentialVault _vault;
    private readonly Action<SessionState, string?> _report;
    private readonly Func<string, string, string?> _promptForSecret;
    private readonly TextBox _editor;
    private readonly TabControl _results = new() { Background = Ground, BorderThickness = new Thickness(0) };
    private readonly Button _run;
    private readonly Button _stop;
    private readonly TextBlock _status = new() { Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private SqlSession? _session;
    private CancellationTokenSource? _running;

    public SqlView(SessionDefinition definition, CredentialVault vault,
                   Action<SessionState, string?> report, Func<string, string, string?> promptForSecret)
    {
        _definition = definition;
        _vault = vault;
        _report = report;
        _promptForSecret = promptForSecret;
        Background = Ground;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 60 });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 60 });

        var bar = new DockPanel { Background = Panel, LastChildFill = true };
        _run = Btn("▶ Run", "Run (F5 or Ctrl+Enter). With text selected, only the selection runs.", async () => await RunAsync());
        _stop = Btn("■ Stop", "Stop the running query", Stop);
        _stop.IsEnabled = false;
        var badge = new TextBlock
        {
            Text = SqlSession.EngineName(definition.SqlEngine) + (definition.SqlReadOnly ? "  ·  read-only" : ""),
            Foreground = definition.SqlReadOnly ? Good : Muted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 8, 0)
        };
        DockPanel.SetDock(_run, Dock.Left);
        DockPanel.SetDock(_stop, Dock.Left);
        DockPanel.SetDock(badge, Dock.Right);
        bar.Children.Add(_run);
        bar.Children.Add(_stop);
        bar.Children.Add(badge);
        bar.Children.Add(_status);
        Children.Add(bar);

        _editor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = Math.Max(11, definition.FontSize),
            Background = Ground,
            Foreground = Text,
            CaretBrush = Text,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Text = Starter(definition.SqlEngine)
        };
        _editor.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.F5 || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
            {
                e.Handled = true;
                await RunAsync();
            }
        };
        SetRow(_editor, 1);
        Children.Add(_editor);

        var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Panel };
        SetRow(splitter, 2);
        Children.Add(splitter);

        SetRow(_results, 3);
        Children.Add(_results);
    }

    private static string Starter(SqlEngine engine) => engine switch
    {
        SqlEngine.SqlServer => "SELECT name, create_date FROM sys.databases;",
        SqlEngine.PostgreSql => "SELECT datname FROM pg_database;",
        SqlEngine.MySql => "SHOW DATABASES;",
        _ => "SELECT name, type FROM sqlite_master;"
    };

    private Button Btn(string text, string tip, Action act)
    {
        var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(6, 4, 0, 4) };
        b.Click += (_, _) => act();
        return b;
    }

    private string Where() => _definition.SqlEngine == SqlEngine.Sqlite
        ? System.IO.Path.GetFileName(_definition.SqlFile)
        : _definition.Host + (_definition.SqlDatabase.Length > 0 ? "/" + _definition.SqlDatabase : "");

    public async Task ConnectAsync()
    {
        await DisposeSessionAsync();
        _report(SessionState.Connecting, $"Connecting to {Where()}...");

        string? user = null, password = null;
        try
        {
            if (_definition.SqlEngine == SqlEngine.Sqlite && _definition.SqlFile.Length == 0)
            {
                throw new InvalidOperationException("No database file set. Choose one in Properties.");
            }

            if (_definition.SqlEngine != SqlEngine.Sqlite && !(_definition.SqlEngine == SqlEngine.SqlServer && _definition.SqlWindowsAuth))
            {
                (user, password) = await LoginResolver.ResolveAsync(_definition, _vault, this, _promptForSecret,
                    _definition.SqlEngine switch { SqlEngine.SqlServer => "mssql", SqlEngine.PostgreSql => "postgresql", _ => "mysql" },
                    SqlSession.DefaultPort(_definition.SqlEngine));
            }

            _session = await SqlSession.OpenAsync(_definition, user, password, CancellationToken.None);
            _report(SessionState.Connected, $"Connected to {Where()} ({SqlSession.EngineName(_definition.SqlEngine)} {_session.ServerVersion}).");
            _editor.Focus();
        }
        catch (OperationCanceledException)
        {
            _report(SessionState.Disconnected, "Cancelled.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _report(SessionState.Failed, FirstLine(ex.Message));
        }
    }

    private async Task RunAsync()
    {
        if (_running is not null)
        {
            return;
        }

        if (_session is null)
        {
            Status("Not connected. Press Reconnect.", Bad);
            return;
        }

        var sql = _editor.SelectionLength > 0 ? _editor.SelectedText : _editor.Text;
        if (sql.Trim().Length == 0)
        {
            return;
        }

        if (_definition.SqlConfirmChanges && !_definition.SqlReadOnly && SqlSafety.ChangesData(sql))
        {
            var shown = sql.Length > 600 ? sql[..600] + "..." : sql;
            var answer = MessageBox.Show(Window.GetWindow(this)!,
                $"This changes data or structure on {Where()}:\n\n{shown}\n\nRun it?",
                "BaudHoundTerm: SQL", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                Status("Not run.", Muted);
                return;
            }
        }

        _running = new CancellationTokenSource();
        _run.IsEnabled = false;
        _stop.IsEnabled = true;
        Status("Running...", Muted);
        try
        {
            var outcome = await _session.RunAsync(sql, MaxRows, _running.Token);
            Show(outcome);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var stopped = _running.IsCancellationRequested;
            ShowError(stopped ? "Stopped." : ex.Message);
            Status(stopped ? "Stopped." : FirstLine(ex.Message), Bad);
        }
        finally
        {
            _running.Dispose();
            _running = null;
            _run.IsEnabled = true;
            _stop.IsEnabled = false;
        }
    }

    private void Stop() => _running?.Cancel();

    private void Show(SqlOutcome outcome)
    {
        _results.Items.Clear();
        var n = 0;
        foreach (var set in outcome.Sets)
        {
            n++;
            _results.Items.Add(new TabItem
            {
                Header = $"Result {n} ({set.Rows.Count}{(set.Truncated ? "+" : "")})",
                Content = Grid(set)
            });
        }

        var lines = new List<string>(outcome.Messages);
        if (outcome.RecordsAffected > 0)
        {
            lines.Add($"{outcome.RecordsAffected} row(s) affected.");
        }

        if (outcome.Sets.Any(s => s.Truncated))
        {
            lines.Add($"Only the first {MaxRows} rows of a result are shown; the rest were not fetched.");
        }

        _results.Items.Add(new TabItem { Header = "Messages", Content = MessagesBox(lines.Count > 0 ? string.Join("\n", lines) : "Done.") });
        _results.SelectedIndex = 0;

        var rows = outcome.Sets.Sum(s => s.Rows.Count);
        Status($"{(outcome.Sets.Count > 0 ? $"{rows}{(outcome.Sets.Any(s => s.Truncated) ? "+" : "")} row(s)" : "Done")}" +
               (outcome.RecordsAffected > 0 ? $" · {outcome.RecordsAffected} affected" : "") +
               $" · {outcome.Elapsed.TotalMilliseconds:0} ms", Good);
    }

    private void ShowError(string message)
    {
        _results.Items.Clear();
        _results.Items.Add(new TabItem { Header = "Messages", Content = MessagesBox(message) });
        _results.SelectedIndex = 0;
    }

    private static DataGrid Grid(SqlResultSet set)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            ItemsSource = set.Rows,
            Background = Ground,
            RowBackground = Ground,
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0x14, 0x19, 0x22)),
            Foreground = Text,
            BorderThickness = new Thickness(0),
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x38)),
            HeadersVisibility = DataGridHeadersVisibility.Column,
            ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            ColumnHeaderStyle = HeaderStyle
        };

        for (var i = 0; i < set.Columns.Count; i++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = set.Columns[i].Length > 0 ? set.Columns[i] : $"(column {i + 1})",
                // Indexing the row array: column names with dots or brackets break name bindings.
                Binding = new Binding($"[{i}]") { TargetNullValue = "NULL" }
            });
        }

        return grid;
    }

    private static readonly Style HeaderStyle = MakeHeaderStyle();

    private static Style MakeHeaderStyle()
    {
        var style = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        style.Setters.Add(new Setter(BackgroundProperty, Panel));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, Muted));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x22, 0x2A, 0x38))));
        return style;
    }

    private static TextBox MessagesBox(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        Background = Ground,
        Foreground = Text,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(8),
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    private void Status(string text, Brush brush)
    {
        _status.Text = text;
        _status.Foreground = brush;
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    public async Task DisconnectAsync()
    {
        _running?.Cancel();
        await DisposeSessionAsync();
        _report(SessionState.Disconnected, "Disconnected.");
    }

    private async Task DisposeSessionAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }

    public void Dispose()
    {
        _running?.Cancel();
        _ = DisposeSessionAsync();
    }
}
