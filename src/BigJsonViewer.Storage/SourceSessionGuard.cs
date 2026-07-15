using BigJsonViewer.Core;

namespace BigJsonViewer.Storage;

public sealed class SourceSessionGuard
{
    private readonly SourceFileIdentity _originalIdentity;
    private readonly string _path;

    public SourceSessionGuard(SourceMetadata metadata)
    {
        _path = metadata.Path;
        _originalIdentity = metadata.Identity;
    }

    public bool IsStale { get; private set; }

    public async Task<bool> CheckForChangeAsync(CancellationToken cancellationToken = default)
    {
        if (IsStale)
        {
            return true;
        }

        try
        {
            var current = await SourceInspector.InspectAsync(_path, cancellationToken).ConfigureAwait(false);
            IsStale = current.Identity != _originalIdentity;
        }
        catch (FileNotFoundException)
        {
            IsStale = true;
        }

        return IsStale;
    }
}
