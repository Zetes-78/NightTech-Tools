using System.Windows;
using System.Windows.Threading;
using NightTech.Core.Logging;

namespace NightTech.FieldKit;

public partial class App : Application
{
    public static AppLog Log { get; } = new("Field Kit", KitPaths.Logs);

    public static string ToolVersion { get; } =
        typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        Log.Info($"Night Tech Field Kit {ToolVersion} started on {Environment.MachineName}" + (KitPaths.Portable ? " (portable)." : "."));
        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unexpected error.", e.Exception);
        MessageBox.Show("Something unexpected went wrong:\n\n" + e.Exception.Message +
                        "\n\nDetails are in the log folder:\n" + Log.Folder,
            "Night Tech Field Kit", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
