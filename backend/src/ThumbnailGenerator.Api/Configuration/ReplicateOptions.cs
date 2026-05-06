namespace ThumbnailGenerator.Api.Configuration;

public sealed class ReplicateOptions
{
    public const string SectionName = "Replicate";

    /// <summary>Örn. https://api.replicate.com/v1</summary>
    public string BaseUrl { get; set; } = "https://api.replicate.com/v1";

    public string ApiToken { get; set; } = "";

    /// <summary>Replicate predictions "version" alanı (tam sürüm hash'i).</summary>
    public string FluxModelVersion { get; set; } = "";

    /// <summary>Model input şemasına göre; Flux için genelde "16:9".</summary>
    public string AspectRatio { get; set; } = "16:9";

    /// <summary>
    /// true ise input'a aspect_ratio eklenir. Çoğu özel model (ör. vector-blog-thumbnails) yalnızca prompt kabul eder; false bırakın.
    /// </summary>
    public bool SendAspectRatio { get; set; }
}
