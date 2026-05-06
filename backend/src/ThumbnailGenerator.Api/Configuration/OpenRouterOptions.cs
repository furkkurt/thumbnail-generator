namespace ThumbnailGenerator.Api.Configuration;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    public string ApiKey { get; set; } = "";

    /// <summary>OpenRouter model slug; UI’dan kopyalanan <c>~vendor/model</c> biçimini kullanın (ör. ~google/gemini-pro-latest).</summary>
    public string Model { get; set; } = "~google/gemini-pro-latest";

    public string SiteUrl { get; set; } = "http://localhost:5080";

    public string SiteName { get; set; } = "ThumbnailGenerator.Api";
}
