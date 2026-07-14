namespace BigJsonViewer.CorpusGenerator;

public static class ScenarioName
{
    public static CorpusScenario Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "wide-array" => CorpusScenario.WideArray,
        "deep-object" => CorpusScenario.DeepObject,
        "json-lines" or "jsonl" or "ndjson" => CorpusScenario.JsonLines,
        "minified" => CorpusScenario.Minified,
        "large-string" => CorpusScenario.LargeString,
        "whitespace" => CorpusScenario.Whitespace,
        "escaped-strings" => CorpusScenario.EscapedStrings,
        "invalid-utf8" => CorpusScenario.InvalidUtf8,
        "truncated" => CorpusScenario.Truncated,
        "malformed" => CorpusScenario.Malformed,
        _ => throw new ArgumentException($"Unknown scenario '{value}'.")
    };

    public static string ToKebabCase(CorpusScenario scenario) => scenario switch
    {
        CorpusScenario.WideArray => "wide-array",
        CorpusScenario.DeepObject => "deep-object",
        CorpusScenario.JsonLines => "json-lines",
        CorpusScenario.Minified => "minified",
        CorpusScenario.LargeString => "large-string",
        CorpusScenario.Whitespace => "whitespace",
        CorpusScenario.EscapedStrings => "escaped-strings",
        CorpusScenario.InvalidUtf8 => "invalid-utf8",
        CorpusScenario.Truncated => "truncated",
        CorpusScenario.Malformed => "malformed",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };
}
