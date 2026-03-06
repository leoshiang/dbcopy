using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DbCopy.Models;
using DbCopy.ViewModels;
using DbType = DbCopy.Models.DbType;

namespace DbCopy.Views;

public partial class ConnectionEditWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DbConnectionInfo? _existing;

    public ConnectionEditWindow()
    {
        _vm = null!;
        InitializeComponent();
    }

    public ConnectionEditWindow(MainViewModel vm, DbConnectionInfo? existing = null)
    {
        _vm = vm;
        _existing = existing;
        InitializeComponent();

        if (existing != null)
        {
            TitleText.Text = "編輯資料庫連線";
            NameInput.Text = existing.Name;
            TypeCombo.SelectedIndex = (int)existing.Type;
            ConnStringInput.Text = existing.ConnectionString;
            ReadOnlyCheck.IsChecked = existing.ReadOnly;
        }
    }

    private async void OnTestConnection(object? sender, RoutedEventArgs e)
    {
        var connString = ConnStringInput.Text?.Trim();
        if (string.IsNullOrEmpty(connString))
        {
            TestStatusText.Text = "請輸入連線字串";
            TestStatusText.Foreground = new SolidColorBrush(Color.Parse("#dc2626"));
            return;
        }

        TestStatusText.Text = "連線中...";
        TestStatusText.Foreground = new SolidColorBrush(Color.Parse("#6b7280"));

        try
        {
            var conn = new DbConnectionInfo
            {
                Type = (DbType)TypeCombo.SelectedIndex,
                ConnectionString = connString
            };

            await _vm.TestConnectionAsync(conn);
            TestStatusText.Text = "✓ 連線成功！";
            TestStatusText.Foreground = new SolidColorBrush(Color.Parse("#059669"));
        }
        catch
        {
            TestStatusText.Text = "✗ 連線失敗";
            TestStatusText.Foreground = new SolidColorBrush(Color.Parse("#dc2626"));
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var name = NameInput.Text?.Trim();
        var connString = ConnStringInput.Text?.Trim();

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(connString))
        {
            TestStatusText.Text = "名稱和連線字串為必填";
            TestStatusText.Foreground = new SolidColorBrush(Color.Parse("#dc2626"));
            return;
        }

        var result = new DbConnectionInfo
        {
            Id = _existing?.Id ?? Guid.NewGuid().ToString(),
            Name = name,
            Type = (DbType)TypeCombo.SelectedIndex,
            ConnectionString = connString,
            ReadOnly = ReadOnlyCheck.IsChecked == true
        };

        Close(result);
    }
}
