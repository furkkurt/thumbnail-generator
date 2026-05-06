namespace ThumbnailGenerator.Api.Configuration;

public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    public string[] Origins { get; set; } = ["http://localhost:4321"];
}
