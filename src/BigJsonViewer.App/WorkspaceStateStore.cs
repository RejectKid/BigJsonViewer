using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BigJsonViewer.App;

internal sealed class WorkspaceStateStore
{
    private readonly string _root;

    public WorkspaceStateStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BigJsonViewer",
            "workspaces"))
    {
    }

    internal WorkspaceStateStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public WorkspaceStateData Load(string sourcePath)
    {
        var path = GetPath(sourcePath);
        if (!File.Exists(path))
        {
            return new WorkspaceStateData();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, WorkspaceStateJsonContext.Default.WorkspaceStateData) ?? new WorkspaceStateData();
        }
        catch (JsonException)
        {
            return new WorkspaceStateData();
        }
        catch (IOException)
        {
            return new WorkspaceStateData();
        }
    }

    public void Save(string sourcePath, WorkspaceStateData state)
    {
        var path = GetPath(sourcePath);
        var temporaryPath = path + ".tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, WorkspaceStateJsonContext.Default.WorkspaceStateData);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private string GetPath(string sourcePath)
    {
        var normalized = Path.GetFullPath(sourcePath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_root, $"{hash}.json");
    }
}

internal sealed class WorkspaceStateData
{
    public long SelectedNodeId { get; set; } = -1;

    public int SelectedDetailTab { get; set; }

    public List<long> ExpandedNodeIds { get; set; } = [];

    public List<string> SearchHistory { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WorkspaceStateData))]
internal partial class WorkspaceStateJsonContext : JsonSerializerContext;
