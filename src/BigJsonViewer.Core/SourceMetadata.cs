namespace BigJsonViewer.Core;

public readonly record struct SourceMetadata(
    string Path,
    SourceFileIdentity Identity,
    SourceEncoding Encoding,
    JsonDocumentFormat Format,
    bool IsProbablySparse,
    string? CompressionKind);
