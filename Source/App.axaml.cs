using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DbCopy.ViewModels;
using DbCopy.Views;
using Serilog;

namespace DbCopy;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File("Logs/log-.txt", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
