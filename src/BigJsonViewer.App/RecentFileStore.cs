namespace BigJsonViewer.App;

internal sealed class RecentFileStore
{
    private const int MaximumEntries = 10;
    private readonly string _path;
    private readonly List<string> _paths;

    public RecentFileStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BigJsonViewer");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "recent-files.txt");
        _paths = File.Exists(_path)
            ? File.ReadLines(_path).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumEntries).ToList()
            : [];
    }

    public IReadOnlyList<string> Paths => _paths;

    public void Add(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _paths.RemoveAll(item => string.Equals(item, fullPath, StringComparison.OrdinalIgnoreCase));
        _paths.Insert(0, fullPath);
        if (_paths.Count > MaximumEntries)
        {
            _paths.RemoveRange(MaximumEntries, _paths.Count - MaximumEntries);
        }

        File.WriteAllLines(_path, _paths);
    }
}
