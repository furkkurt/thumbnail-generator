namespace ThumbnailGenerator.Api.Composition;

/// <summary>LLM'den gelen İngilizce betimlemeye sabit vektör stil sonekini ekler.</summary>
public static class PromptComposer
{
    private const string StyleSuffix =
        "VCTRTN, flat vector illustration, simple shapes, solid colors, minimalist, clean lines, 2D, no gradients, no shading, no text.";

    public static string Compose(string userPrompt)
    {
        var trimmed = userPrompt.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Prompt boş olamaz.", nameof(userPrompt));

        return $"{trimmed}. {StyleSuffix}";
    }
}
