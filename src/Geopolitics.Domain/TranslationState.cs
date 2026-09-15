namespace Geopolitics.Domain;

/// <summary>
/// Whether the English text shown for an observation is a translation, the source's own words, or
/// absent because nothing translated it.
/// <para>
/// This exists because the dashboard used to infer the answer and inferred it wrongly. It stamped a
/// "translated from Arabic" marker on any observation whose language was not English, when the
/// language had often been read straight off the feed at intake with no model involved at all, and
/// when the default provider states plainly that it cannot translate. A reader was told a
/// translation had happened while looking at untranslated Arabic.
/// </para>
/// <para>
/// So the state is recorded rather than derived. It is set from what the enrichment step actually
/// reports having done, and a reader who is shown source text is told that is what they are looking
/// at.
/// </para>
/// </summary>
public enum TranslationState
{
    /// <summary>
    /// Nothing rendered this into English. The title and summary are the source's own words, in the
    /// source's own language.
    /// <para>
    /// Zero because it is the correct reading of every record written before translation was
    /// recorded, and because it is the safer of the two wrong answers: showing untranslated text
    /// while claiming nothing costs a reader a little confusion, where claiming a translation that
    /// did not happen costs them the ability to trust any of it.
    /// </para>
    /// </summary>
    NotTranslated = 0,

    /// <summary>
    /// The source published in English, so there is nothing to translate and the English view and
    /// the original view are the same text. Distinct from <see cref="NotTranslated"/>, which is a
    /// gap; this is a complete answer.
    /// </summary>
    AlreadyEnglish,

    /// <summary>
    /// A language model rendered the source text into English. The model that did it is recorded
    /// alongside, because a machine translation of a contested claim is evidence about the model as
    /// much as about the claim.
    /// </summary>
    MachineTranslated,
}
