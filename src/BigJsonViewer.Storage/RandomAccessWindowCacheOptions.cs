using System.Numerics;

namespace BigJsonViewer.Storage;

public sealed class RandomAccessWindowCacheOptions
{
    public const int MinimumWindowSize = 64 * 1024;
    public const int DefaultWindowSize = 1024 * 1024;
    public const int MaximumWindowSize = 8 * 1024 * 1024;
    public const long DefaultCapacityBytes = 64L * 1024 * 1024;

    public RandomAccessWindowCacheOptions(
        int windowSize = DefaultWindowSize,
        long capacityBytes = DefaultCapacityBytes)
    {
        if (windowSize is < MinimumWindowSize or > MaximumWindowSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowSize),
                windowSize,
                $"Window size must be between {MinimumWindowSize} and {MaximumWindowSize} bytes.");
        }

        if (!BitOperations.IsPow2(windowSize))
        {
            throw new ArgumentException("Window size must be a power of two.", nameof(windowSize));
        }

        if (capacityBytes < windowSize || capacityBytes % windowSize != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacityBytes),
                capacityBytes,
                "Capacity must be a positive whole number of windows.");
        }

        WindowSize = windowSize;
        CapacityBytes = capacityBytes;
    }

    public int WindowSize { get; }

    public long CapacityBytes { get; }
}
