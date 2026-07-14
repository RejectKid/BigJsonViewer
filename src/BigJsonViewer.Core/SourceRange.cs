namespace BigJsonViewer.Core;

public readonly record struct SourceRange(long Offset, long Length)
{
    public long End => checked(Offset + Length);

    public bool Contains(long position) => position >= Offset && position < End;
}
