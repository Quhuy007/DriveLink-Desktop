using System.Windows;
using System.Windows.Threading;
namespace DriveLink;
public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App(); app.InitializeComponent(); app.Run();
    }
    private Mutex? instance;
    private EventWaitHandle? wake;
    private RegisteredWaitHandle? wait;
    private bool ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        // Bản dựng thử giao diện: chạy song song được với app thật, không đọc tài khoản và không gọi Google.
        var preview = e.Args.FirstOrDefault(arg => arg.StartsWith("--ui-preview", StringComparison.OrdinalIgnoreCase))?.ToLowerInvariant();
        if (preview is "--ui-preview" or "--ui-preview-glass")
        {
            base.OnStartup(e);
            Window window = preview == "--ui-preview-glass" ? new Preview.GlassShell() : new Preview.PreviewShell();
            window.Show();
            return;
        }
        var key = "Local\\DriveLink.QuangHuy." + Environment.UserName;
        instance = new Mutex(true, key, out ownsInstance);
        if (!ownsInstance)
        {
            try { using var signal = EventWaitHandle.OpenExisting(key + ".Show"); signal.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        wake = new EventWaitHandle(false, EventResetMode.AutoReset, key + ".Show");
        wait = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray()), null, Timeout.Infinite, false);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => { if (sender is Window window) UiPresentation.Fit(window); }));
        base.OnStartup(e);
        // Mở cửa sổ chính bằng code (thay cho StartupUri) để tham số --ui-preview chọn được cửa sổ khác.
        new MainWindow().Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        wait?.Unregister(null); wake?.Dispose();
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose(); base.OnExit(e);
    }
}
