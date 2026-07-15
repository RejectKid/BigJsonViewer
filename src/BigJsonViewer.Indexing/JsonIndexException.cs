namespace BigJsonViewer.Indexing;

public sealed class JsonIndexException : Exception
{
    public JsonIndexException(string message, long offset)
        : base($"{message} (byte {offset:N0})")
    {
        Offset = offset;
    }

    public long Offset { get; }
}
