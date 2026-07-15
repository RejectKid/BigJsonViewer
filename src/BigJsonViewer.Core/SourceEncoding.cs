namespace BigJsonViewer.Core;

public enum SourceEncoding : byte
{
    Utf8,
    Utf8Bom,
    Utf16LittleEndian,
    Utf16BigEndian,
    Utf32LittleEndian,
    Utf32BigEndian,
    Unknown
}
