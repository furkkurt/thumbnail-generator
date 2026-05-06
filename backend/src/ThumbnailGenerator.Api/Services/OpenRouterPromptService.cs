using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ThumbnailGenerator.Api.Configuration;

namespace ThumbnailGenerator.Api.Services;

public sealed class OpenRouterPromptService : IOpenRouterPromptService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private const string SystemPrompt =
        """
        You write a single English image-generation prompt for a flat vector illustration model (simple shapes, solid colors, no photorealism).
        The user's message is a Turkish blog post (and optional Turkish title). Read it and output ONE concise English prompt describing the main visual scene or metaphor — no explanation, no quotes, no markdown fences, no bullet lists.
        The output must be entirely in English. Do not include any text, letters, logos, or typography in the described image.
        Keep it under roughly 400 characters.
        """;

    private readonly HttpClient _httpClient;
    private readonly IOptionsSnapshot<OpenRouterOptions> _options;
    private readonly ILogger<OpenRouterPromptService> _logger;

    public OpenRouterPromptService(
        HttpClient httpClient,
        IOptionsSnapshot<OpenRouterOptions> options,
        ILogger<OpenRouterPromptService> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<string> BuildEnglishVectorPromptAsync(
        string? titleTurkish,
        string blogPostTurkish,
        CancellationToken cancellationToken)
    {
        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.ApiKey))
            throw new OpenRouterPromptException(
                "OpenRouter API anahtarı yapılandırılmamış (OpenRouter:ApiKey / OPENROUTER_API_KEY).");

        if (string.IsNullOrWhiteSpace(opts.Model))
            throw new OpenRouterPromptException("OpenRouter model adı boş (OpenRouter:Model).");

        var modelId = ResolveOpenRouterModelId(opts.Model.Trim());

        var userParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(titleTurkish))
            userParts.Add($"Turkish title: {titleTurkish.Trim()}");
        userParts.Add($"Turkish blog post:\n{blogPostTurkish.Trim()}");

        var userContent = string.Join("\n\n", userParts);

        var baseUri = new Uri(opts.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey.Trim());

        // OpenRouter dokümantasyonundaki "HTTP-Referer" ifadesi, HTTP’de standart Referer başlığıdır.
        var siteUrl = opts.SiteUrl?.Trim();
        if (!string.IsNullOrWhiteSpace(siteUrl))
            request.Headers.TryAddWithoutValidation("Referer", siteUrl);

        var siteName = opts.SiteName?.Trim();
        if (!string.IsNullOrWhiteSpace(siteName))
            request.Headers.TryAddWithoutValidation("X-Title", siteName);

        var body = new ChatCompletionRequest
        {
            Model = modelId,
            Messages =
            [
                new ChatMessage { Role = "system", Content = SystemPrompt },
                new ChatMessage { Role = "user", Content = userContent },
            ],
        };

        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter hata: {Status} {Body}", response.StatusCode, payload);
            var detail = TryGetOpenRouterErrorDetail(payload);
            throw new OpenRouterPromptException(FormatOpenRouterFailureMessage(response.StatusCode, detail));
        }

        ChatCompletionResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new OpenRouterPromptException("OpenRouter yanıtı çözümlenemedi.", ex);
        }

        var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        var cleaned = CleanLlmOutput(text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned))
            throw new OpenRouterPromptException("OpenRouter boş prompt döndü.");

        _logger.LogInformation("OpenRouter İngilizce görsel prompt: {EnglishPrompt}", cleaned);
        return cleaned;
    }

    /// <summary>
    /// OpenRouter arayüzü bazı modelleri <c>~vendor/model</c> olarak kopyalar; API de bu slug’ı bekler.
    /// <c>google/gemini-pro-latest</c> tek başına geçersiz model hatası (400) verebilir.
    /// </summary>
    private static string ResolveOpenRouterModelId(string model)
    {
        if (string.Equals(model, "google/gemini-pro-latest", StringComparison.OrdinalIgnoreCase))
            return "~google/gemini-pro-latest";
        return model;
    }

    private static string? TryGetOpenRouterErrorDetail(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("error", out var err))
                return null;
            if (err.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString();
            return err.ValueKind == JsonValueKind.String ? err.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatOpenRouterFailureMessage(HttpStatusCode status, string? apiDetail)
    {
        const string intro = "Görsel promptu oluşturulamadı (OpenRouter).";
        if (string.IsNullOrWhiteSpace(apiDetail))
        {
            return $"{intro} HTTP {(int)status}. Anahtar, model adı veya kota kontrol edin; geliştirmede API konsolundaki tam gövde loguna bakın.";
        }

        var oneLine = string.Join(
            " ",
            apiDetail.Trim().ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (oneLine.Length > 320)
            oneLine = string.Concat(oneLine.AsSpan(0, 317), "…");

        return $"{intro} ({(int)status}) {oneLine}";
    }

    private static string CleanLlmOutput(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = s.IndexOf('\n');
            if (firstNl >= 0)
                s = s[(firstNl + 1)..].Trim();
            var fence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
                s = s[..fence].Trim();
        }

        s = s.Trim().Trim('"', '\'');
        return s.ReplaceLineEndings(" ").Trim();
    }

    private sealed class ChatCompletionRequest
    {
        public required string Model { get; set; }
        public required List<ChatMessage> Messages { get; set; }
    }

    private sealed class ChatMessage
    {
        public required string Role { get; set; }
        public string? Content { get; set; }
    }

    private sealed class ChatCompletionResponse
    {
        public List<ChoiceDto>? Choices { get; set; }
    }

    private sealed class ChoiceDto
    {
        public ChatMessage? Message { get; set; }
    }
}
