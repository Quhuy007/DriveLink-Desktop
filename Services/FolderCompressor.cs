using System.Diagnostics;
using System.IO.Compression;
namespace DriveLink.Services;

public sealed record CompressionTools(string? SevenZip, string? WinRar, string? Rar)
{
    public static CompressionTools Detect()
    {
        string? Find(string relative)
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Distinct())
            {
                var candidate = Path.Combine(root, relative);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
        return new(Find(@"7-Zip\7z.exe"), Find(@"WinRAR\WinRAR.exe"), Find(@"WinRAR\Rar.exe"));
    }
    public bool CanCreateRar => Rar is not null || WinRar is not null;
    public string Summary => $"7-Zip: {(SevenZip is null ? "chưa có" : "đã tìm thấy")} · WinRAR: {(WinRar is null && Rar is null ? "chưa có" : "đã tìm thấy")}";
}
public sealed class PackedFolder(string archivePath, string sourceDirectory, long originalBytes, string tool) : IDisposable
{
    public string ArchivePath { get; } = archivePath;
    public string SourceDirectory { get; } = sourceDirectory;
    public long OriginalBytes { get; } = originalBytes;
    public long ArchiveBytes => new FileInfo(ArchivePath).Length;
    public string Tool { get; } = tool;
    public void Dispose()
    {
        if (File.Exists(ArchivePath)) File.Delete(ArchivePath);
        var container = Path.GetDirectoryName(ArchivePath)!;
        if (Directory.Exists(container) && !Directory.EnumerateFileSystemEntries(container).Any()) Directory.Delete(container);
    }
}
public sealed class FolderCompressor(CompressionTools? installed = null, string? temporaryRoot = null)
{
    private readonly CompressionTools tools = installed ?? CompressionTools.Detect();
    private readonly string tempRoot = temporaryRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveLink", "UploadArchives");
    public async Task<PackedFolder> PackAsync(string directory, UserSettings settings, IProgress<string>? progress, CancellationToken ct)
    {
        var root = new DirectoryInfo(Path.GetFullPath(directory));
        if (!root.Exists || root.Parent is null) throw new InvalidOperationException("Chọn một thư mục cụ thể, không chọn cả ổ đĩa.");
        if (settings.ArchiveFormat is not ("zip" or "rar")) throw new InvalidOperationException("Định dạng nén không hợp lệ.");
        var stagingRoot = Path.GetFullPath(tempRoot);
        if ((stagingRoot + Path.DirectorySeparatorChar).StartsWith(root.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Thư mục được chọn chứa vùng tạo file nén của ứng dụng. Hãy chọn một thư mục con cụ thể.");
        progress?.Report("Đang kiểm tra thư mục: " + root.Name);
        var tree = await Task.Run(() => Scan(root, ct), ct);
        ct.ThrowIfCancellationRequested();
        var container = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(container);
        var archive = Path.Combine(container, root.Name + "." + settings.ArchiveFormat);
        var level = settings.HighCompression ? "5" : "3";
        string tool;
        try
        {
            if (settings.ArchiveFormat == "rar")
            {
                if (settings.CompressionTool is "7zip" or "builtin") throw new InvalidOperationException("RAR cần WinRAR. Chọn công cụ Tự động hoặc WinRAR trong cài đặt.");
                var executable = tools.Rar ?? tools.WinRar ?? throw new InvalidOperationException("Chưa tìm thấy WinRAR để tạo RAR. Chọn ZIP hoặc cài WinRAR.");
                tool = "WinRAR";
                progress?.Report("Đang nén RAR bằng WinRAR: " + root.Name);
                var arguments = new List<string> { "a", "-cfg-", "-r", "-m" + level, "-s", "-y", "-inul" };
                if (!Path.GetFileName(executable).Equals("Rar.exe", StringComparison.OrdinalIgnoreCase)) arguments.Add("-ibck");
                arguments.AddRange(["--", archive, "." + Path.DirectorySeparatorChar + root.Name]);
                await RunExternalAsync(executable, root.Parent.FullName, arguments.ToArray(), ct);
            }
            else if ((settings.CompressionTool is "auto" or "7zip") && tools.SevenZip is { } sevenZip)
            {
                tool = "7-Zip"; progress?.Report("Đang nén ZIP bằng 7-Zip: " + root.Name);
                await RunExternalAsync(sevenZip, root.Parent.FullName,
                    ["a", "-tzip", "-mm=Deflate", settings.HighCompression ? "-mx=9" : "-mx=5", "-y", "-bso0", "-bsp0", "-spd", "--", archive, "." + Path.DirectorySeparatorChar + root.Name], ct);
            }
            else if ((settings.CompressionTool is "auto" or "winrar") && tools.WinRar is { } winrar)
            {
                tool = "WinRAR"; progress?.Report("Đang nén ZIP bằng WinRAR: " + root.Name);
                await RunExternalAsync(winrar, root.Parent.FullName,
                    ["a", "-cfg-", "-afzip", "-r", "-m" + level, "-y", "-inul", "-ibck", "--", archive, "." + Path.DirectorySeparatorChar + root.Name], ct);
            }
            else
            {
                if (settings.CompressionTool is not ("auto" or "builtin")) throw new InvalidOperationException("Công cụ nén đã chọn chưa được cài. Chọn Tự động hoặc ZIP tích hợp.");
                tool = "ZIP tích hợp"; progress?.Report("Đang tạo ZIP: " + root.Name);
                await Task.Run(() => CreateZip(root, tree.Entries, archive, settings.HighCompression, ct), ct);
            }
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(archive) || new FileInfo(archive).Length == 0) throw new IOException("Công cụ nén chưa tạo được tệp. Không gửi thư mục chưa đóng gói hoàn chỉnh.");
            return new(archive, root.FullName, tree.Bytes, tool);
        }
        catch
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (!Directory.EnumerateFileSystemEntries(container).Any()) Directory.Delete(container);
            throw;
        }
    }
    private static (List<FileSystemInfo> Entries, long Bytes) Scan(DirectoryInfo root, CancellationToken ct)
    {
        var entries = new List<FileSystemInfo>(); var pending = new Stack<DirectoryInfo>(); pending.Push(root); long bytes = 0;
        if ((root.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Chưa hỗ trợ nén thư mục liên kết/junction.");
        while (pending.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Thư mục có file hoặc thư mục liên kết. Bỏ liên kết trước khi đóng gói để tránh gửi dữ liệu ngoài thư mục.");
                entries.Add(entry);
                if (entry is DirectoryInfo sub) pending.Push(sub);
                else if (entry is FileInfo file) bytes += file.Length;
            }
        }
        return (entries, bytes);
    }
    private static void CreateZip(DirectoryInfo root, List<FileSystemInfo> entries, string target, bool strong, CancellationToken ct)
    {
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        zip.CreateEntry(root.Name + "/");
        var buffer = new byte[65536];
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = root.Name + "/" + Path.GetRelativePath(root.FullName, entry.FullName).Replace('\\', '/');
            if (entry is DirectoryInfo) { zip.CreateEntry(name + "/"); continue; }
            var file = zip.CreateEntry(name, strong ? CompressionLevel.SmallestSize : CompressionLevel.Optimal);
            using var input = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var destination = file.Open();
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { ct.ThrowIfCancellationRequested(); destination.Write(buffer, 0, read); }
        }
    }
    private static async Task RunExternalAsync(string executable, string workingDirectory, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Không khởi chạy được công cụ nén.");
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        if (process.ExitCode != 0) throw new IOException($"Công cụ nén báo lỗi/cảnh báo ({process.ExitCode}). File gốc được giữ nguyên; bản nén chưa được gửi.");
    }
}
