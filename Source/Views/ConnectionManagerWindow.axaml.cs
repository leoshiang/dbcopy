using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DbCopy.Models;
using DbCopy.ViewModels;
using DbType = DbCopy.Models.DbType;

namespace DbCopy.Views;

public partial class ConnectionManagerWindow : Window
{
    private readonly MainViewModel _vm;

    public static readonly FuncValueConverter<DbType, string> TypeToLabelConverter =
        new(t => t == DbType.SqlServer ? "MSSQL" : "PG");

    public static readonly FuncValueConverter<DbType, IBrush> TypeToBgConverter =
        new(t => t == DbType.SqlServer
            ? new SolidColorBrush(Color.Parse("#0891b2"))
            : new SolidColorBrush(Color.Parse("#059669")));

    public ConnectionManagerWindow()
    {
        _vm = null!;
        InitializeComponent();
    }

    public ConnectionManagerWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        RefreshList();
    }

    private void RefreshList()
    {
        ConnectionList.ItemsSource = null;
        ConnectionList.ItemsSource = _vm.Connections;
    }

    private async void OnAddConnection(object? sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionEditWindow(_vm);
        var result = await dialog.ShowDialog<DbConnectionInfo?>(this);
        if (result != null)
        {
            _vm.Connections.Add(result);
            _vm.SaveConnections();
            _vm.RefreshConnectionDropdowns();
            RefreshList();
        }
    }

    private async void OnEditConnection(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var conn = _vm.Connections.FirstOrDefault(c => c.Id == id);
        if (conn == null) return;

        var dialog = new ConnectionEditWindow(_vm, conn);
        var result = await dialog.ShowDialog<DbConnectionInfo?>(this);
        if (result != null)
        {
            var idx = _vm.Connections.ToList().FindIndex(c => c.Id == result.Id);
            if (idx >= 0) _vm.Connections[idx] = result;
            _vm.SaveConnections();
            _vm.RefreshConnectionDropdowns();
            RefreshList();
        }
    }

    private void OnDeleteConnection(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var conn = _vm.Connections.FirstOrDefault(c => c.Id == id);
        if (conn != null)
        {
            _vm.Connections.Remove(conn);
            _vm.SaveConnections();
            _vm.RefreshConnectionDropdowns();
            RefreshList();
        }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "匯出連線",
            SuggestedFileName = $"db-connections-{DateTime.Now:yyyyMMdd}.json",
            FileTypeChoices =
            [
                new FilePickerFileType("JSON") { Patterns = ["*.json"] }
            ]
        });

        if (file != null)
        {
            _vm.ExportConnections(file.Path.LocalPath);
        }
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "匯入連線",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON") { Patterns = ["*.json"] }
            ]
        });

        if (files.Count > 0)
        {
            _vm.ImportConnections(files[0].Path.LocalPath);
            RefreshList();
        }
    }
}
