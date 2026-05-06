using MediatR;
using ThumbnailGenerator.Api.Composition;
using ThumbnailGenerator.Api.Services;

namespace ThumbnailGenerator.Api.Features.Images;

public sealed record GenerateImageCommand(string BlogPost, string? Title) : IRequest<GenerateImageResponse>;

public sealed record GenerateImageResponse(string ImageUrl, string EnglishPrompt, string ReplicatePrompt);

public sealed class GenerateImageCommandHandler : IRequestHandler<GenerateImageCommand, GenerateImageResponse>
{
    private readonly IOpenRouterPromptService _openRouter;
    private readonly IReplicatePredictionService _replicate;

    public GenerateImageCommandHandler(
        IOpenRouterPromptService openRouter,
        IReplicatePredictionService replicate)
    {
        _openRouter = openRouter;
        _replicate = replicate;
    }

    public async Task<GenerateImageResponse> Handle(GenerateImageCommand request, CancellationToken cancellationToken)
    {
        var english = await _openRouter.BuildEnglishVectorPromptAsync(request.Title, request.BlogPost, cancellationToken);
        Console.WriteLine($"[English visual prompt] {english}");
        var fullPrompt = PromptComposer.Compose(english);
        Console.WriteLine($"[Replicate prompt] {fullPrompt}");
        var imageUrl = await _replicate.RunPredictionAsync(fullPrompt, cancellationToken);
        return new GenerateImageResponse(imageUrl, english, fullPrompt);
    }
}
