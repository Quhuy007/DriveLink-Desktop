namespace DriveLink.Services;

public sealed class LocalFileHistory(SavedState state, SecureStore store)
{
    public void Record(string driveId, string path)
    {
        state.LocalFiles[driveId] = Path.GetFullPath(path);
        store.Save(state);
    }
    public string? FindExisting(string driveId)
    {
        if (!state.LocalFiles.TryGetValue(driveId, out var path)) return null;
        return File.Exists(path) || Directory.Exists(path) ? path : null;
    }
    public string? FindKnownPath(string driveId) => state.LocalFiles.GetValueOrDefault(driveId);
}
