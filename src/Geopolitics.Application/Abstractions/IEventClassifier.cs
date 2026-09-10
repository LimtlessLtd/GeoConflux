using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

/// <param name="EventType">Classified category of the reported event.</param>
/// <param name="Severity">Assessed severity.</param>
/// <param name="Confidence">0-1 score. The UI presents this rather than implying certainty.</param>
/// <param name="Method">How the classification was produced, e.g. <c>source-declared</c> or <c>keyword</c>.</param>
public sealed record EventClassification(EventType EventType, Severity Severity, double Confidence, string Method);

/// <summary>
/// Assigns a category and severity to observation text. The Sprint 2 implementation is a
/// deterministic keyword classifier; an AI-backed classifier replaces it behind this interface
/// and keeps the deterministic one as its fallback when enrichment fails or fails validation.
/// </summary>
public interface IEventClassifier
{
    EventClassification Classify(string text);
}
