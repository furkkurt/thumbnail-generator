namespace ThumbnailGenerator.Api.Services;

public interface IReplicatePredictionService
{
    /// <summary>Tam prompt ile tahmin çalıştırır; başarılı olunca çıktı görsel URL'sini döner.</summary>
    Task<string> RunPredictionAsync(string fullPrompt, CancellationToken cancellationToken);
}
