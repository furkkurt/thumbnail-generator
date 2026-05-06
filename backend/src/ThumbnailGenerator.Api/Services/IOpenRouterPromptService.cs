namespace ThumbnailGenerator.Api.Services;

/// <summary>Türkçe blog metninden vektör görseli için İngilizce kısa prompt üretir (OpenRouter / Gemini).</summary>
public interface IOpenRouterPromptService
{
    Task<string> BuildEnglishVectorPromptAsync(string? titleTurkish, string blogPostTurkish, CancellationToken cancellationToken);
}
