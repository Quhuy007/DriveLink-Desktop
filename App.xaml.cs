using System.Windows;
using System.Windows.Threading;
namespace DriveLink;
public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? wake;
    private RegisteredWaitHandle? wait;
    private bool ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        var key = "Local\\DriveLink.QuangHuy." + Environment.UserName;
        instance = new Mutex(true, key, out ownsInstance);
        if (!ownsInstance)
        {
            try { using var signal = EventWaitHandle.OpenExisting(key + ".Show"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        wake = new EventWaitHandle(false, EventResetMode.AutoReset, key + ".Show");
        wait = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray()), null, Timeout.Infinite, false);
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        wait?.Unregister(null); wake?.Dispose();
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose(); base.OnExit(e);
    }
}
