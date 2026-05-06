namespace ThumbnailGenerator.Api.Services;

public sealed class OpenRouterPromptException : Exception
{
    public OpenRouterPromptException(string message)
        : base(message)
    {
    }

    public OpenRouterPromptException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
