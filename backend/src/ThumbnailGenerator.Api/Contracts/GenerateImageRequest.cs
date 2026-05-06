namespace ThumbnailGenerator.Api.Contracts;

public sealed class GenerateImageRequest
{
    public string BlogPost { get; set; } = "";

    public string? Title { get; set; }
}
