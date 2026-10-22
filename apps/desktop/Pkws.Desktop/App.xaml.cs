using System.Threading;
using System.Windows;

namespace Pkws.Desktop;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\PKWS.Desktop.Singleton.v1";
    private const string ActivationEventName = "Local\\PKWS.Desktop.Activate.v1";
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private RuntimeCoordinator? _runtime;
    private MainWindow? _window;
    private EdgeLauncherWindow? _launcher;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                existingEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The first instance may still be starting; exiting still prevents duplicates.
            }

            Shutdown();
            return;
        }

        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _runtime = new RuntimeCoordinator();
            await _runtime.InitializeAsync();
            _window = new MainWindow(_runtime);
            _launcher = new EdgeLauncherWindow();
            MainWindow = _window;
            _launcher.RevealRequested += (_, _) => ShowNotePanel();
            _window.CollapseRequested += (_, _) => _launcher.ShowCollapsed();
            _window.ExitRequested += (_, _) => ExitApplication();
            _activationWait = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                (_, _) => Dispatcher.Invoke(ShowNotePanel),
                null,
                Timeout.Infinite,
                false);

            if (e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase))
            {
                ShowNotePanel();
            }
            else
            {
                _launcher.ShowCollapsed();
            }
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"PKWS 启动失败：{exception.Message}",
                "PKWS",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ShowNotePanel()
    {
        _launcher?.Hide();
        _window?.ShowAndFocus();
    }

    private void ExitApplication()
    {
        _window?.PrepareForExit();
        _launcher?.Close();
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        _activationEvent?.Dispose();
        if (_runtime is not null)
        {
            _runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_ownsMutex)
        {
            _mutex?.ReleaseMutex();
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
