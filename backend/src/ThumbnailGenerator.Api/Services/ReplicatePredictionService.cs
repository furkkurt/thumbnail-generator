using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ThumbnailGenerator.Api.Configuration;

namespace ThumbnailGenerator.Api.Services;

public sealed class ReplicatePredictionService : IReplicatePredictionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly IOptionsSnapshot<ReplicateOptions> _options;
    private readonly ILogger<ReplicatePredictionService> _logger;

    public ReplicatePredictionService(
        HttpClient httpClient,
        IOptionsSnapshot<ReplicateOptions> options,
        ILogger<ReplicatePredictionService> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<string> RunPredictionAsync(string fullPrompt, CancellationToken cancellationToken)
    {
        var opts = _options.Value;

        if (string.IsNullOrWhiteSpace(opts.ApiToken))
            throw new ReplicatePredictionException("Replicate API anahtarı yapılandırılmamış (Replicate:ApiToken / REPLICATE_API_TOKEN).");

        if (string.IsNullOrWhiteSpace(opts.FluxModelVersion)
            || string.Equals(opts.FluxModelVersion, "YOUR_REPLICATE_FLUX_VERSION", StringComparison.Ordinal))
            throw new ReplicatePredictionException("Replicate model sürümü yapılandırılmamış (Replicate:FluxModelVersion).");

        var versionId = ReplicateVersionNormalizer.Normalize(opts.FluxModelVersion);
        if (string.IsNullOrWhiteSpace(versionId))
            throw new ReplicatePredictionException("Geçersiz model sürümü. .env içinde REPLICATE_FLUX_MODEL_VERSION olarak tam hash veya owner/model:hash kullanın.");

        var baseUri = new Uri(opts.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "predictions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiToken.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Model şeması Replicate sayfasından doğrulanmalı; çoğu görsel modeli "aspect_ratio" (ör. "16:9") kabul eder.
        var input = new Dictionary<string, object> { ["prompt"] = fullPrompt };
        if (opts.SendAspectRatio && !string.IsNullOrWhiteSpace(opts.AspectRatio))
            input["aspect_ratio"] = opts.AspectRatio.Trim();

        var body = new CreatePredictionBody
        {
            Version = versionId,
            Input = input,
        };

        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var createResponse = await _httpClient.SendAsync(request, cancellationToken);
        var createPayload = await createResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
        {
            if (createResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning(
                    "Replicate 401: gönderilen Bearer token uzunluğu {Length}. Kabukta eski REPLICATE_API_TOKEN varsa: unset REPLICATE_API_TOKEN; .env önceliklidir.",
                    opts.ApiToken.Length);
            }

            _logger.LogWarning("Replicate tahmin oluşturma başarısız: {Status} {Body}", createResponse.StatusCode, createPayload);
            var detail = TryExtractReplicateErrorDetail(createPayload);
            var hint = createResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? " Token geçersiz, süresi dolmuş veya kabuktaki eski REPLICATE_API_TOKEN .env'i ezliyor olabilir; `unset REPLICATE_API_TOKEN` deneyin."
                : string.Empty;
            var msg = string.IsNullOrEmpty(detail)
                ? "Görsel üretimi başlatılamadı. Model sürümünün yalnızca hash kısmı olduğundan ve input şemasının (yalnızca prompt) uyduğundan emin olun."
                : $"Replicate: {TruncateForUser(detail)}";
            throw new ReplicatePredictionException(msg + hint);
        }

        var created = JsonSerializer.Deserialize<PredictionResponse>(createPayload, JsonOptions);
        if (created?.Urls?.Get is not { Length: > 0 } pollUrl)
            throw new ReplicatePredictionException("Replicate yanıtında polling URL'si yok.");

        var deadline = DateTime.UtcNow.AddMinutes(3);
        const int delayMs = 2000;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var pollRequest = new HttpRequestMessage(HttpMethod.Get, pollUrl);
            pollRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiToken.Trim());
            pollRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var poll = await _httpClient.SendAsync(pollRequest, cancellationToken);
            var pollPayload = await poll.Content.ReadAsStringAsync(cancellationToken);

            if (!poll.IsSuccessStatusCode)
            {
                _logger.LogWarning("Replicate polling hatası: {Status} {Body}", poll.StatusCode, pollPayload);
                throw new ReplicatePredictionException("Görsel üretimi sırasında Replicate ile iletişim kesildi.");
            }

            var status = JsonSerializer.Deserialize<PredictionResponse>(pollPayload, JsonOptions);
            var state = status?.Status?.ToLowerInvariant();

            if (state is "succeeded" or "successful")
            {
                var url = ExtractOutputUrl(status?.Output);
                if (!string.IsNullOrWhiteSpace(url))
                    return url!;

                throw new ReplicatePredictionException("Tahmin tamamlandı ancak çıktı URL'si okunamadı.");
            }

            if (state is "failed" or "canceled")
            {
                var detail = status?.Error is { } err ? err.ToString() : pollPayload;
                _logger.LogWarning("Replicate tahmin başarısız: {Detail}", detail);
                var userDetail = TryExtractReplicateErrorDetail(pollPayload);
                throw new ReplicatePredictionException(
                    string.IsNullOrEmpty(userDetail)
                        ? "Görsel üretimi başarısız oldu. Prompt veya model girişlerini kontrol edin."
                        : $"Replicate: {TruncateForUser(userDetail)}");
            }

            await Task.Delay(delayMs, cancellationToken);
        }

        throw new ReplicatePredictionException("Görsel üretimi zaman aşımına uğradı. Daha sonra tekrar deneyin.");
    }

    private static string TruncateForUser(string s, int max = 400) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string? TryExtractReplicateErrorDetail(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                    return detail.GetString();

                if (detail.ValueKind == JsonValueKind.Array && detail.GetArrayLength() > 0)
                {
                    var first = detail[0];
                    if (first.ValueKind == JsonValueKind.String)
                        return first.GetString();
                    if (first.ValueKind == JsonValueKind.Object
                        && first.TryGetProperty("msg", out var msg)
                        && msg.ValueKind == JsonValueKind.String)
                        return msg.GetString();
                }
            }

            if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                return title.GetString();
        }
        catch (JsonException)
        {
            // ignore
        }

        return null;
    }

    private static string? ExtractOutputUrl(JsonElement? output)
    {
        if (output is not { } el)
            return null;

        if (el.ValueKind == JsonValueKind.String)
            return el.GetString();

        if (el.ValueKind == JsonValueKind.Object)
            return ReadUrlFromObject(el);

        if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0)
        {
            var first = el[0];
            if (first.ValueKind == JsonValueKind.String)
                return first.GetString();

            if (first.ValueKind == JsonValueKind.Object)
                return ReadUrlFromObject(first);
        }

        return null;
    }

    private static string? ReadUrlFromObject(JsonElement obj)
    {
        if (obj.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            return url.GetString();
        if (obj.TryGetProperty("href", out var href) && href.ValueKind == JsonValueKind.String)
            return href.GetString();
        return null;
    }

    private sealed class CreatePredictionBody
    {
        public required string Version { get; set; }
        public required Dictionary<string, object> Input { get; set; }
    }

    private sealed class PredictionResponse
    {
        public string? Status { get; set; }
        public JsonElement? Output { get; set; }
        public JsonElement? Error { get; set; }
        public PredictionUrls? Urls { get; set; }
    }

    private sealed class PredictionUrls
    {
        [JsonPropertyName("get")]
        public string? Get { get; set; }
    }
}
