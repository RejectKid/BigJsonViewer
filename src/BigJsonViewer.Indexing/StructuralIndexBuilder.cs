using BigJsonViewer.Core;

namespace BigJsonViewer.Indexing;

internal sealed class StructuralIndexBuilder
{
    private readonly JsonDocumentFormat _format;
    private readonly int _maximumDepth;
    private readonly List<IndexedNode> _pending;
    private readonly List<Frame> _frames;
    private long _nextId = 1;
    private bool _inString;
    private bool _escaped;
    private bool _propertyString;
    private bool _inPrimitive;
    private long _tokenStart;
    private JsonNodeKind _primitiveKind;
    private byte _literalKind;
    private int _primitiveLength;
    private NumberState _numberState;
    private int _utf8ContinuationCount;
    private int _utf8CodePoint;
    private int _utf8Minimum;
    private int _unicodeDigits;
    private int _unicodeValue;
    private bool _pendingHighSurrogate;

    public StructuralIndexBuilder(JsonDocumentFormat format, int maximumDepth, int batchSize)
    {
        _format = format;
        _maximumDepth = maximumDepth;
        _pending = new List<IndexedNode>(batchSize + maximumDepth);
        _frames = new List<Frame>(Math.Min(maximumDepth, 256))
        {
            new Frame(0, JsonNodeKind.Document, 0, FrameState.ExpectValue)
        };
        _pending.Add(new IndexedNode(
            0,
            -1,
            new SourceRange(0, 0),
            new SourceRange(0, 0),
            0,
            JsonNodeKind.Document));
    }

    public long NodeCount => _nextId;

    public int PendingCount => _pending.Count;

    public IReadOnlyList<IndexedNode> Pending => _pending;

    public void ClearPending() => _pending.Clear();

    public void Process(ReadOnlySpan<byte> bytes, long absoluteOffset)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            var position = absoluteOffset + index;
            var value = bytes[index];
            ValidateUtf8Byte(value, position);
            if (_inString)
            {
                ProcessStringByte(value, position);
                continue;
            }

            if (_inPrimitive)
            {
                if (!IsValueDelimiter(value))
                {
                    ValidatePrimitiveByte(value, position);
                    continue;
                }

                FinishPrimitive(position);
            }

            if (IsWhitespace(value))
            {
                if (value == (byte)'\n' && Current.Kind == JsonNodeKind.Document &&
                    Current.State == FrameState.ExpectCommaOrEnd && _format == JsonDocumentFormat.JsonLines)
                {
                    Current.State = FrameState.ExpectValue;
                }

                continue;
            }

            switch (value)
            {
                case (byte)'"':
                    StartString(position);
                    break;
                case (byte)'{':
                    StartContainer(JsonNodeKind.Object, position);
                    break;
                case (byte)'[':
                    StartContainer(JsonNodeKind.Array, position);
                    break;
                case (byte)'}':
                    CloseContainer(JsonNodeKind.Object, position);
                    break;
                case (byte)']':
                    CloseContainer(JsonNodeKind.Array, position);
                    break;
                case (byte)':':
                    ReadColon(position);
                    break;
                case (byte)',':
                    ReadComma(position);
                    break;
                default:
                    StartPrimitive(value, position);
                    break;
            }
        }
    }

    public void Complete(long sourceLength)
    {
        if (_inString)
        {
            throw Error("Unterminated string", sourceLength);
        }

        if (_utf8ContinuationCount != 0)
        {
            throw Error("Truncated UTF-8 sequence", sourceLength);
        }

        if (_inPrimitive)
        {
            FinishPrimitive(sourceLength);
        }

        if (_frames.Count != 1)
        {
            throw Error("Unterminated container", sourceLength);
        }

        var document = _frames[0];
        if (document.ChildCount == 0)
        {
            throw Error("The document contains no JSON value", sourceLength);
        }

        if (_format != JsonDocumentFormat.JsonLines && document.ChildCount != 1)
        {
            throw Error("More than one root JSON value was found", sourceLength);
        }

        _pending.Add(CreateFrameNode(document, sourceLength, _nextId - 1));
    }

    private void ProcessStringByte(byte value, long position)
    {
        if (_unicodeDigits > 0)
        {
            var digit = value switch
            {
                >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
                _ => throw Error("Invalid Unicode escape", position)
            };
            _unicodeValue = (_unicodeValue << 4) | digit;
            _unicodeDigits--;
            if (_unicodeDigits == 0)
            {
                CompleteUnicodeEscape(position);
            }

            return;
        }

        if (_escaped)
        {
            _escaped = false;
            if (value == (byte)'u')
            {
                _unicodeDigits = 4;
                _unicodeValue = 0;
            }
            else if (value is not ((byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t'))
            {
                throw Error("Invalid string escape", position);
            }

            if (_pendingHighSurrogate && value != (byte)'u')
            {
                throw Error("A high surrogate is not followed by a low surrogate", position);
            }

            return;
        }

        if (value == (byte)'\\')
        {
            _escaped = true;
            return;
        }

        if (value != (byte)'"')
        {
            if (_pendingHighSurrogate)
            {
                throw Error("A high surrogate is not followed by a low surrogate", position);
            }

            if (value < 0x20)
            {
                throw Error("An unescaped control character occurs in a string", position);
            }

            return;
        }

        if (_pendingHighSurrogate)
        {
            throw Error("A high surrogate is not followed by a low surrogate", position);
        }

        _inString = false;
        var range = new SourceRange(_tokenStart, position - _tokenStart + 1);
        if (_propertyString)
        {
            var frame = Current;
            frame.PendingName = new SourceRange(_tokenStart + 1, Math.Max(0, range.Length - 2));
            frame.State = FrameState.ExpectColon;
            return;
        }

        AddScalar(JsonNodeKind.String, range);
    }

    private void StartString(long position)
    {
        var frame = Current;
        _propertyString = frame.Kind == JsonNodeKind.Object &&
            frame.State is FrameState.ExpectPropertyOrEnd or FrameState.ExpectProperty;
        if (!_propertyString)
        {
            EnsureCanReadValue(frame, position);
        }

        _tokenStart = position;
        _inString = true;
        _escaped = false;
        _unicodeDigits = 0;
        _pendingHighSurrogate = false;
    }

    private void StartPrimitive(byte value, long position)
    {
        EnsureCanReadValue(Current, position);
        _primitiveKind = value switch
        {
            (byte)'t' or (byte)'f' => JsonNodeKind.Boolean,
            (byte)'n' => JsonNodeKind.Null,
            (byte)'-' or >= (byte)'0' and <= (byte)'9' => JsonNodeKind.Number,
            _ => throw Error($"Unexpected byte 0x{value:X2}", position)
        };
        _tokenStart = position;
        _inPrimitive = true;
        _literalKind = value;
        _primitiveLength = 0;
        _numberState = NumberState.Start;
        ValidatePrimitiveByte(value, position);
    }

    private void FinishPrimitive(long endOffset)
    {
        _inPrimitive = false;
        var valid = _primitiveKind == JsonNodeKind.Number
            ? _numberState is NumberState.Zero or NumberState.Integer or NumberState.Fraction or NumberState.ExponentDigits
            : _primitiveLength == (_literalKind == (byte)'f' ? 5 : 4);
        if (!valid)
        {
            throw Error("Invalid scalar token", _tokenStart);
        }

        AddScalar(_primitiveKind, new SourceRange(_tokenStart, endOffset - _tokenStart));
    }

    private void AddScalar(JsonNodeKind kind, SourceRange range)
    {
        var parent = Current;
        var id = _nextId++;
        var name = ConsumeName(parent);
        parent.RegisterChild(id);
        parent.State = FrameState.ExpectCommaOrEnd;
        _pending.Add(new IndexedNode(id, parent.Id, range, name, 0, kind, -1, id));
    }

    private void ValidatePrimitiveByte(byte value, long position)
    {
        if (_primitiveKind == JsonNodeKind.Number)
        {
            _numberState = AdvanceNumber(_numberState, value);
            if (_numberState == NumberState.Invalid)
            {
                throw Error("Invalid JSON number", position);
            }
        }
        else
        {
            var expected = _literalKind switch
            {
                (byte)'t' => "true"u8,
                (byte)'f' => "false"u8,
                _ => "null"u8
            };
            if (_primitiveLength >= expected.Length || value != expected[_primitiveLength])
            {
                throw Error("Invalid JSON literal", position);
            }
        }

        _primitiveLength++;
    }

    private void ValidateUtf8Byte(byte value, long position)
    {
        if (_utf8ContinuationCount == 0)
        {
            if (value <= 0x7F)
            {
                return;
            }

            if (value is >= 0xC2 and <= 0xDF)
            {
                _utf8ContinuationCount = 1;
                _utf8CodePoint = value & 0x1F;
                _utf8Minimum = 0x80;
                return;
            }

            if (value is >= 0xE0 and <= 0xEF)
            {
                _utf8ContinuationCount = 2;
                _utf8CodePoint = value & 0x0F;
                _utf8Minimum = 0x800;
                return;
            }

            if (value is >= 0xF0 and <= 0xF4)
            {
                _utf8ContinuationCount = 3;
                _utf8CodePoint = value & 0x07;
                _utf8Minimum = 0x10000;
                return;
            }

            throw Error("Invalid UTF-8 leading byte", position);
        }

        if (value is < 0x80 or > 0xBF)
        {
            throw Error("Invalid UTF-8 continuation byte", position);
        }

        _utf8CodePoint = (_utf8CodePoint << 6) | (value & 0x3F);
        _utf8ContinuationCount--;
        if (_utf8ContinuationCount == 0 &&
            (_utf8CodePoint < _utf8Minimum || _utf8CodePoint > 0x10FFFF ||
             _utf8CodePoint is >= 0xD800 and <= 0xDFFF))
        {
            throw Error("Invalid UTF-8 code point", position);
        }
    }

    private void CompleteUnicodeEscape(long position)
    {
        if (_unicodeValue is >= 0xD800 and <= 0xDBFF)
        {
            if (_pendingHighSurrogate)
            {
                throw Error("Two high surrogates occur without a low surrogate", position);
            }

            _pendingHighSurrogate = true;
        }
        else if (_unicodeValue is >= 0xDC00 and <= 0xDFFF)
        {
            if (!_pendingHighSurrogate)
            {
                throw Error("A low surrogate occurs without a high surrogate", position);
            }

            _pendingHighSurrogate = false;
        }
        else if (_pendingHighSurrogate)
        {
            throw Error("A high surrogate is not followed by a low surrogate", position);
        }
    }

    private static NumberState AdvanceNumber(NumberState state, byte value) => state switch
    {
        NumberState.Start when value == (byte)'-' => NumberState.Sign,
        NumberState.Start when value == (byte)'0' => NumberState.Zero,
        NumberState.Start when value is >= (byte)'1' and <= (byte)'9' => NumberState.Integer,
        NumberState.Sign when value == (byte)'0' => NumberState.Zero,
        NumberState.Sign when value is >= (byte)'1' and <= (byte)'9' => NumberState.Integer,
        NumberState.Integer when value is >= (byte)'0' and <= (byte)'9' => NumberState.Integer,
        NumberState.Zero or NumberState.Integer when value == (byte)'.' => NumberState.DecimalPoint,
        NumberState.DecimalPoint when value is >= (byte)'0' and <= (byte)'9' => NumberState.Fraction,
        NumberState.Fraction when value is >= (byte)'0' and <= (byte)'9' => NumberState.Fraction,
        NumberState.Zero or NumberState.Integer or NumberState.Fraction
            when value is (byte)'e' or (byte)'E' => NumberState.Exponent,
        NumberState.Exponent when value is (byte)'+' or (byte)'-' => NumberState.ExponentSign,
        NumberState.Exponent or NumberState.ExponentSign
            when value is >= (byte)'0' and <= (byte)'9' => NumberState.ExponentDigits,
        NumberState.ExponentDigits when value is >= (byte)'0' and <= (byte)'9' => NumberState.ExponentDigits,
        _ => NumberState.Invalid
    };

    private void StartContainer(JsonNodeKind kind, long position)
    {
        var parent = Current;
        EnsureCanReadValue(parent, position);
        if (_frames.Count >= _maximumDepth)
        {
            throw Error($"Maximum nesting depth {_maximumDepth:N0} exceeded", position);
        }

        var id = _nextId++;
        var name = ConsumeName(parent);
        parent.RegisterChild(id);
        parent.State = FrameState.ExpectCommaOrEnd;
        _pending.Add(new IndexedNode(
            id,
            parent.Id,
            new SourceRange(position, 0),
            name,
            0,
            kind));
        _frames.Add(new Frame(
            id,
            kind,
            position,
            kind == JsonNodeKind.Object ? FrameState.ExpectPropertyOrEnd : FrameState.ExpectValueOrEnd,
            name,
            parent.Id));
    }

    private void CloseContainer(JsonNodeKind kind, long position)
    {
        if (_frames.Count == 1 || Current.Kind != kind)
        {
            throw Error($"Unexpected closing {(char)(kind == JsonNodeKind.Object ? '}' : ']')}", position);
        }

        var frame = Current;
        var mayClose = kind == JsonNodeKind.Object
            ? frame.State is FrameState.ExpectPropertyOrEnd or FrameState.ExpectCommaOrEnd
            : frame.State is FrameState.ExpectValueOrEnd or FrameState.ExpectCommaOrEnd;
        if (!mayClose)
        {
            throw Error("A container ended while a value was expected", position);
        }

        _frames.RemoveAt(_frames.Count - 1);
        _pending.Add(CreateFrameNode(frame, position + 1, _nextId - 1));
    }

    private void ReadColon(long position)
    {
        if (Current.Kind != JsonNodeKind.Object || Current.State != FrameState.ExpectColon)
        {
            throw Error("Unexpected colon", position);
        }

        Current.State = FrameState.ExpectValue;
    }

    private void ReadComma(long position)
    {
        var frame = Current;
        if (frame.State != FrameState.ExpectCommaOrEnd || frame.Kind == JsonNodeKind.Document)
        {
            throw Error("Unexpected comma", position);
        }

        frame.State = frame.Kind == JsonNodeKind.Object
            ? FrameState.ExpectProperty
            : FrameState.ExpectValue;
    }

    private void EnsureCanReadValue(Frame frame, long position)
    {
        var allowed = frame.Kind switch
        {
            JsonNodeKind.Document => frame.State == FrameState.ExpectValue &&
                (_format == JsonDocumentFormat.JsonLines || frame.ChildCount == 0),
            JsonNodeKind.Object => frame.State == FrameState.ExpectValue,
            JsonNodeKind.Array => frame.State is FrameState.ExpectValueOrEnd or FrameState.ExpectValue,
            _ => false
        };
        if (!allowed)
        {
            throw Error("A JSON value was not expected here", position);
        }
    }

    private IndexedNode CreateFrameNode(Frame frame, long endOffset, long subtreeEndId) =>
        new(
            frame.Id,
            frame.ParentId,
            new SourceRange(frame.Start, endOffset - frame.Start),
            frame.NameRange,
            frame.ChildCount,
            frame.Kind,
            frame.FirstChildId,
            subtreeEndId);

    private static SourceRange ConsumeName(Frame frame)
    {
        var result = frame.PendingName;
        frame.PendingName = default;
        return result;
    }

    private static bool IsValueDelimiter(byte value) =>
        IsWhitespace(value) || value is (byte)',' or (byte)']' or (byte)'}';

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private Frame Current => _frames[^1];

    private static JsonIndexException Error(string message, long offset) => new(message, offset);

    private sealed class Frame
    {
        public Frame(
            long id,
            JsonNodeKind kind,
            long start,
            FrameState state,
            SourceRange nameRange = default,
            long parentId = -1)
        {
            Id = id;
            Kind = kind;
            Start = start;
            State = state;
            NameRange = nameRange;
            ParentId = parentId;
        }

        public long Id { get; }
        public long ParentId { get; }
        public JsonNodeKind Kind { get; }
        public long Start { get; }
        public SourceRange NameRange { get; }
        public FrameState State { get; set; }
        public SourceRange PendingName { get; set; }
        public long ChildCount { get; private set; }
        public long FirstChildId { get; private set; } = -1;

        public void RegisterChild(long id)
        {
            if (ChildCount == 0)
            {
                FirstChildId = id;
            }

            ChildCount++;
        }
    }

    private enum FrameState : byte
    {
        ExpectValue,
        ExpectValueOrEnd,
        ExpectProperty,
        ExpectPropertyOrEnd,
        ExpectColon,
        ExpectCommaOrEnd
    }

    private enum NumberState : byte
    {
        Start,
        Sign,
        Zero,
        Integer,
        DecimalPoint,
        Fraction,
        Exponent,
        ExponentSign,
        ExponentDigits,
        Invalid
    }
}
