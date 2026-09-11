namespace Geopolitics.Domain;

/// <summary>
/// A named actor mentioned by an observation. Extraction is a semantic task, so these values come
/// from a language model and are treated accordingly: they are bounded, normalised, and presented
/// as claims made about the text rather than as verified facts about the world.
/// </summary>
public sealed class ExtractedEntity
{
    public const int MaxNameLength = 120;

    private ExtractedEntity()
    {
        Name = string.Empty;
    }

    public ExtractedEntity(string name, EntityType type)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("An extracted entity requires a name.");
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainException($"An extracted entity name may not exceed {MaxNameLength} characters.");
        }

        Name = trimmed;
        Type = type;
    }

    public string Name { get; private set; }

    public EntityType Type { get; private set; }

    /// <summary>Case-insensitive comparison key, so "Houthi" and "houthi" count as one actor.</summary>
    public string MatchKey => Name.ToLowerInvariant();
}
