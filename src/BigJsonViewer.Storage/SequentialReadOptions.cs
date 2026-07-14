namespace BigJsonViewer.Storage;

public sealed class SequentialReadOptions
{
    public const int MinimumBufferSize = 4 * 1024 * 1024;
    public const int DefaultBufferSize = 8 * 1024 * 1024;
    public const int MaximumBufferSize = 32 * 1024 * 1024;

    public SequentialReadOptions(int bufferSize = DefaultBufferSize)
    {
        if (bufferSize is < MinimumBufferSize or > MaximumBufferSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferSize),
                bufferSize,
                $"Buffer size must be between {MinimumBufferSize} and {MaximumBufferSize} bytes.");
        }

        BufferSize = bufferSize;
    }

    public int BufferSize { get; }
}
