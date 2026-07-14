namespace BigJsonViewer.Storage;

/// <summary>
/// Processes one sequential chunk. The chunk memory is valid only for the duration of this callback.
/// Copy data that must be retained after the callback returns.
/// </summary>
public delegate void SequentialChunkHandler(SequentialReadChunk chunk, CancellationToken cancellationToken);
