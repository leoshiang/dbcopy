using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using DbCopy.ViewModels;

namespace DbCopy.Views;

public partial class MainWindow : Window
{
    // Value converters as static instances
    public static readonly FuncValueConverter<bool, IBrush> BoolToBannerBgConverter =
        new(v => v ? new SolidColorBrush(Color.Parse("#d1fae5")) : new SolidColorBrush(Color.Parse("#fecaca")));

    public static readonly FuncValueConverter<bool, IBrush> BoolToBannerFgConverter =
        new(v => v ? new SolidColorBrush(Color.Parse("#065f46")) : new SolidColorBrush(Color.Parse("#991b1b")));

    public static readonly FuncValueConverter<bool, IBrush> BoolToRowBgConverter =
        new(v => v ? new SolidColorBrush(Color.Parse("#d1fae5")) : Brushes.White);

    public MainWindow()
    {
        // Register converters before InitializeComponent
        Resources["BoolToBannerBg"] = BoolToBannerBgConverter;
        Resources["BoolToBannerFg"] = BoolToBannerFgConverter;
        Resources["BoolToRowBg"] = BoolToRowBgConverter;

        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext!;

    private async void OnManageConnections(object? sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionManagerWindow(Vm);
        await dialog.ShowDialog(this);
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb)
        {
            Vm.SelectAll(cb.IsChecked == true);
        }
    }

    private async void OnShowError(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string errorMsg })
        {
            var dialog = new Window
            {
                Title = "同步錯誤詳情",
                Width = 500,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = errorMsg,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Margin = new Avalonia.Thickness(16),
                        FontSize = 13,
                    }
                }
            };
            await dialog.ShowDialog(this);
        }
    }
}
