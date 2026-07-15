using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace BigJsonViewer.App;

public sealed class JsonSyntaxPresenter : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<JsonSyntaxPresenter, string?>(nameof(Text));

    private static readonly IBrush PlainBrush = Brush.Parse("#D7E2F2");
    private static readonly IBrush PropertyBrush = Brush.Parse("#8EAAFF");
    private static readonly IBrush StringBrush = Brush.Parse("#72D7B5");
    private static readonly IBrush NumberBrush = Brush.Parse("#F3BD73");
    private static readonly IBrush KeywordBrush = Brush.Parse("#C69BFF");
    private static readonly IBrush PunctuationBrush = Brush.Parse("#71829B");
    private static readonly IBrush CommentBrush = Brush.Parse("#60728C");
    private readonly SelectableTextBlock _textBlock;

    public JsonSyntaxPresenter()
    {
        _textBlock = new SelectableTextBlock
        {
            Padding = new Thickness(14),
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Monospace"),
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = PlainBrush,
            SelectionBrush = Brush.Parse("#5369B8")
        };
        Content = new ScrollViewer
        {
            Content = _textBlock,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            Render(change.GetNewValue<string?>() ?? string.Empty);
        }
    }

    private void Render(string text)
    {
        var inlines = new InlineCollection();
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            var value = text[index];
            if (char.IsWhiteSpace(value))
            {
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                Add(inlines, text[start..index], PlainBrush);
            }
            else if (value == '"')
            {
                index++;
                var escaped = false;
                while (index < text.Length)
                {
                    var current = text[index++];
                    if (current == '"' && !escaped)
                    {
                        break;
                    }

                    escaped = current == '\\' && !escaped;
                    if (current != '\\')
                    {
                        escaped = false;
                    }
                }

                var next = index;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                Add(inlines, text[start..index], next < text.Length && text[next] == ':' ? PropertyBrush : StringBrush);
            }
            else if (value == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n')
                {
                    index++;
                }

                Add(inlines, text[start..index], CommentBrush, FontStyle.Italic);
            }
            else if (value == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < text.Length && (text[index] != '*' || text[index + 1] != '/'))
                {
                    index++;
                }

                index = Math.Min(text.Length, index + 2);
                Add(inlines, text[start..index], CommentBrush, FontStyle.Italic);
            }
            else if (value is '-' or >= '0' and <= '9')
            {
                index++;
                while (index < text.Length && text[index] is >= '0' and <= '9' or '.' or 'e' or 'E' or '+' or '-')
                {
                    index++;
                }

                Add(inlines, text[start..index], NumberBrush);
            }
            else if (char.IsLetter(value))
            {
                index++;
                while (index < text.Length && char.IsLetter(text[index]))
                {
                    index++;
                }

                var token = text[start..index];
                Add(inlines, token, token is "true" or "false" or "null" ? KeywordBrush : PlainBrush);
            }
            else
            {
                index++;
                Add(inlines, text[start..index], value is '{' or '}' or '[' or ']' or ':' or ',' ? PunctuationBrush : PlainBrush);
            }
        }

        _textBlock.Inlines = inlines;
    }

    private static void Add(InlineCollection inlines, string text, IBrush brush, FontStyle fontStyle = FontStyle.Normal)
    {
        inlines.Add(new Run(text)
        {
            Foreground = brush,
            FontStyle = fontStyle
        });
    }
}
