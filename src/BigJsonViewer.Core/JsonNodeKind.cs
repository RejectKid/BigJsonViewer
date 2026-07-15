namespace BigJsonViewer.Core;

public enum JsonNodeKind : byte
{
    Document,
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null
}
