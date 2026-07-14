namespace BigJsonViewer.Storage;

public readonly record struct RandomAccessWindowCacheStatistics(
    long Hits,
    long Misses,
    long CoalescedRequests,
    long Loads,
    long Evictions,
    long ResidentBytes,
    int ResidentWindows,
    int InFlightLoads);
