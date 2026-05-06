namespace ThumbnailGenerator.Api.Services;

public sealed class ReplicatePredictionException : Exception
{
    public ReplicatePredictionException(string message)
        : base(message)
    {
    }

    public ReplicatePredictionException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
