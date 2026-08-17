using System.Windows;
using System.Windows.Threading;

namespace XiaomiSMBViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        LibVLCSharp.Shared.Core.Initialize();

        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "Blad", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
