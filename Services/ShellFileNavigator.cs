using System.Runtime.InteropServices;
using System.Diagnostics;
namespace DriveLink.Services;

public static class ShellFileNavigator
{
    public static Task OpenContainingFolderAsync(string path)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = CoInitializeEx(IntPtr.Zero, 2) >= 0;
            IntPtr item = IntPtr.Zero;
            try
            {
                if (Directory.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else if (File.Exists(path))
                {
                    Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out item, 0, out _));
                    Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0));
                }
                else throw new FileNotFoundException("File chưa có trên máy hoặc đã di chuyển. Hãy tải về trước.");
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item); if (initialized) CoUninitialize(); }
        }) { IsBackground = true, Name = "DriveLink Explorer" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr item, uint attributesIn, out uint attributesOut);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
}
