using System.Text.Json;
using System.Text.Json.Serialization;

namespace Geopolitics.AiEvaluationTests;

/// <param name="Language">Expected BCP-47 tag of the original text.</param>
/// <param name="EventType">Expected wire-format category.</param>
/// <param name="Severity">Expected wire-format severity.</param>
/// <param name="LocationName">Expected place name, or <see langword="null"/> when the text names none this system knows.</param>
/// <param name="Entities">Expected named actors. Empty where the fixture has none worth asserting.</param>
public sealed record ExpectedLabels(
    string Language,
    string EventType,
    string Severity,
    string? LocationName,
    IReadOnlyList<string> Entities);

public sealed record EvaluationCase(
    string Id,
    string SourceName,
    string? Title,
    string Content,
    ExpectedLabels Expected);

public sealed record EvaluationDataset(int DatasetVersion, string Notice, IReadOnlyList<EvaluationCase> Cases)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Loads the fixtures copied next to the test assembly. Failing loudly when the file is missing
    /// matters: an evaluation that silently ran over zero cases would report a perfect score.
    /// </summary>
    public static EvaluationDataset Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ai-evaluation", "fixtures.json");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Evaluation fixtures were not found at '{path}'. They are copied from tests/data/ai-evaluation by the project file.",
                path);
        }

        var dataset = JsonSerializer.Deserialize<EvaluationDataset>(File.ReadAllText(path), Options)
            ?? throw new InvalidOperationException("The evaluation fixture file deserialised to nothing.");

        return dataset.Cases.Count == 0
            ? throw new InvalidOperationException("The evaluation fixture file contains no cases.")
            : dataset;
    }
}
