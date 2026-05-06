using System.Text.Json;
using DotNetEnv;
using MediatR;
using Microsoft.Extensions.Options;
using ThumbnailGenerator.Api.Configuration;
using ThumbnailGenerator.Api.Contracts;
using ThumbnailGenerator.Api.Features.Images;
using ThumbnailGenerator.Api.Services;

static string? FindEnvFile()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, ".env");
        if (File.Exists(candidate))
            return candidate;
    }

    return null;
}

static void BridgeEnv(string dotnetKey, string flatKey)
{
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(dotnetKey)))
        return;
    var v = Environment.GetEnvironmentVariable(flatKey);
    if (!string.IsNullOrEmpty(v))
        Environment.SetEnvironmentVariable(dotnetKey, v);
}

static string NormalizeEnv(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return string.Empty;

    var s = value.Trim().TrimStart('\uFEFF');
    if (s.Length >= 2
        && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        s = s[1..^1].Trim();

    return s;
}

/// <summary>
/// Doğrudan .env satırı okuma. Aynı anahtar birden fazlaysa <b>son boş olmayan</b> değer geçerlidir
/// (ilk satır boş kalsa bile sonraki dolu satır kullanılır).
/// </summary>
static string? ReadDotenvValue(string filePath, string key)
{
    if (!File.Exists(filePath))
        return null;

    string? lastNonEmpty = null;
    foreach (var raw in File.ReadLines(filePath))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#'))
            continue;

        if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            line = line[7..].TrimStart();
        else if (line.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
            line = line[4..].TrimStart();

        var hash = line.IndexOf('#');
        if (hash >= 0)
            line = line[..hash].TrimEnd();

        var eq = line.IndexOf('=');
        if (eq <= 0)
            continue;

        var k = line[..eq].Trim();
        if (!string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            continue;

        var v = NormalizeEnv(line[(eq + 1)..].Trim());
        if (!string.IsNullOrWhiteSpace(v))
            lastNonEmpty = v;
    }

    return lastNonEmpty;
}

static string CoalesceToken(params string?[] candidates)
{
    foreach (var c in candidates)
        if (!string.IsNullOrWhiteSpace(c))
            return c!;
    return string.Empty;
}

/// <summary>
/// .env'de yanlışlıkla "export ..." veya satır kalıntısı yapıştırıldıysa yalnızca r8_... anahtarını ayıklar.
/// </summary>
static string SanitizeReplicateApiToken(string raw)
{
    var s = NormalizeEnv(raw);
    if (string.IsNullOrEmpty(s))
        return string.Empty;

    while (s.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
        s = NormalizeEnv(s[7..]);

    var idx = s.IndexOf("r8_", StringComparison.Ordinal);
    if (idx < 0)
        return s;

    s = s[idx..];
    var end = 0;
    for (; end < s.Length; end++)
    {
        var c = s[end];
        if (char.IsAsciiLetterOrDigit(c) || c == '_')
            continue;
        break;
    }

    return end > 0 ? s[..end] : s;
}

static bool LooksLikeReplicateToken(string t) =>
    t.StartsWith("r8_", StringComparison.Ordinal) && t.Length is >= 35 and <= 128;

static bool LooksLikeOpenRouterKey(string k) =>
    k.StartsWith("sk-or-", StringComparison.Ordinal) && k.Length is >= 32 and <= 512;

static bool? TryParseLooseBool(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return null;
    var t = value.Trim();
    if (bool.TryParse(t, out var b))
        return b;
    if (string.Equals(t, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(t, "yes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(t, "on", StringComparison.OrdinalIgnoreCase))
        return true;
    if (string.Equals(t, "0", StringComparison.OrdinalIgnoreCase)
        || string.Equals(t, "no", StringComparison.OrdinalIgnoreCase)
        || string.Equals(t, "off", StringComparison.OrdinalIgnoreCase))
        return false;
    return null;
}

var envFile = FindEnvFile();
if (envFile is not null)
{
    Env.Load(
        envFile,
        new LoadOptions(
            setEnvVars: true,
            clobberExistingVars: true,
            onlyExactPath: true));
}
else
{
    Env.TraversePath().Load();
    envFile = FindEnvFile();
}

BridgeEnv("Replicate__ApiToken", "REPLICATE_API_TOKEN");
BridgeEnv("Replicate__FluxModelVersion", "REPLICATE_FLUX_MODEL_VERSION");
BridgeEnv("OpenRouter__ApiKey", "OPENROUTER_API_KEY");
BridgeEnv("OpenRouter__Model", "OPENROUTER_MODEL");

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ReplicateOptions>(builder.Configuration.GetSection(ReplicateOptions.SectionName));
builder.Services.PostConfigure<ReplicateOptions>(o =>
{
    // Kök .env önce: kabukta kalmış eski / hatalı REPLICATE_API_TOKEN export'u genelde 401 üretir.
    var merged = NormalizeEnv(
        CoalesceToken(
            envFile != null ? ReadDotenvValue(envFile, "REPLICATE_API_TOKEN") : null,
            Environment.GetEnvironmentVariable("REPLICATE_API_TOKEN"),
            Environment.GetEnvironmentVariable("Replicate__ApiToken"),
            o.ApiToken));

    o.ApiToken = SanitizeReplicateApiToken(merged);

    o.FluxModelVersion = NormalizeEnv(
        CoalesceToken(
            envFile != null ? ReadDotenvValue(envFile, "REPLICATE_FLUX_MODEL_VERSION") : null,
            Environment.GetEnvironmentVariable("REPLICATE_FLUX_MODEL_VERSION"),
            Environment.GetEnvironmentVariable("Replicate__FluxModelVersion"),
            o.FluxModelVersion));

    var sendAr = ReadDotenvValue(envFile, "REPLICATE_SEND_ASPECT_RATIO")
        ?? Environment.GetEnvironmentVariable("REPLICATE_SEND_ASPECT_RATIO");
    if (TryParseLooseBool(sendAr) is { } sar)
        o.SendAspectRatio = sar;
});
builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection(OpenRouterOptions.SectionName));
builder.Services.PostConfigure<OpenRouterOptions>(o =>
{
    o.ApiKey = NormalizeEnv(
        CoalesceToken(
            envFile != null ? ReadDotenvValue(envFile, "OPENROUTER_API_KEY") : null,
            Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
            Environment.GetEnvironmentVariable("OpenRouter__ApiKey"),
            o.ApiKey));

    var model = NormalizeEnv(
        CoalesceToken(
            envFile != null ? ReadDotenvValue(envFile, "OPENROUTER_MODEL") : null,
            Environment.GetEnvironmentVariable("OPENROUTER_MODEL"),
            Environment.GetEnvironmentVariable("OpenRouter__Model"),
            o.Model));
    if (!string.IsNullOrWhiteSpace(model))
        o.Model = model;
});
builder.Services.Configure<CorsOptions>(builder.Configuration.GetSection(CorsOptions.SectionName));

var corsOriginsEnv = Environment.GetEnvironmentVariable("CORS_ORIGINS");
var corsOptions = builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new CorsOptions();
if (!string.IsNullOrWhiteSpace(corsOriginsEnv))
{
    corsOptions.Origins = corsOriginsEnv
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));
builder.Services.AddHttpClient<IReplicatePredictionService, ReplicatePredictionService>();
builder.Services.AddHttpClient<IOpenRouterPromptService, OpenRouterPromptService>(client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ThumbnailGenerator.Api/1.0");
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOptions.Origins)
            .AllowAnyHeader()
            .AllowAnyMethod());
});

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ThumbnailGenerator.Api");

        var rep = app.Services.GetRequiredService<IOptions<ReplicateOptions>>().Value;
        if (string.IsNullOrWhiteSpace(rep.ApiToken))
        {
            logger.LogWarning(
                "REPLICATE_API_TOKEN boş. Kök .env içinde REPLICATE_API_TOKEN=r8_... olduğundan ve API'yi yeniden başlattığınızdan emin olun. .env yolu: {EnvPath}",
                envFile ?? "(bulunamadı)");
        }
        else
        {
            var ok = LooksLikeReplicateToken(rep.ApiToken);
            var prefix = ok && rep.ApiToken.Length >= 7 ? rep.ApiToken[..7] : rep.ApiToken[..Math.Min(12, rep.ApiToken.Length)];
            if (ok)
            {
                logger.LogInformation(
                    "Replicate token yüklendi (önek: {Prefix}…, uzunluk: {Length}).",
                    prefix,
                    rep.ApiToken.Length);
            }
            else
            {
                logger.LogWarning(
                    "REPLICATE_API_TOKEN beklenen biçimde değil (r8_ ile başlamalı, ~40 karakter). Uzunluk: {Length}, önek: {Prefix}. .env satırı yalnızca şu olmalı: REPLICATE_API_TOKEN=r8_... (satıra 'export' veya açıklama yapıştırmayın).",
                    rep.ApiToken.Length,
                    prefix);
            }
        }

        var or = app.Services.GetRequiredService<IOptions<OpenRouterOptions>>().Value;
        if (string.IsNullOrWhiteSpace(or.ApiKey))
        {
            logger.LogWarning(
                "OPENROUTER_API_KEY boş. Görsel prompt için kök .env içinde OPENROUTER_API_KEY=sk-or-... tanımlayın. .env yolu: {EnvPath}",
                envFile ?? "(bulunamadı)");
        }
        else
        {
            var orOk = LooksLikeOpenRouterKey(or.ApiKey);
            var orPrefix = or.ApiKey.Length >= 12 ? or.ApiKey[..12] : or.ApiKey;
            if (orOk)
            {
                logger.LogInformation(
                    "OpenRouter anahtarı yüklendi (önek: {Prefix}…, uzunluk: {Length}), model: {Model}.",
                    orPrefix,
                    or.ApiKey.Length,
                    string.IsNullOrWhiteSpace(or.Model) ? "(boş)" : or.Model);
            }
            else
            {
                logger.LogWarning(
                    "OPENROUTER_API_KEY beklenen biçimde değil (sk-or- ile başlamalı). Uzunluk: {Length}, önek: {Prefix}.",
                    or.ApiKey.Length,
                    orPrefix);
            }
        }
    });
}

app.UseCors();

app.MapGet("/", () => Results.Ok(new { service = "ThumbnailGenerator.Api", status = "ok" }));

if (app.Environment.IsDevelopment())
{
    app.MapGet("/api/diagnostics/replicate", (IOptions<ReplicateOptions> opt) =>
    {
        var o = opt.Value;
        var has = !string.IsNullOrWhiteSpace(o.ApiToken);
        var looksOk = has && LooksLikeReplicateToken(o.ApiToken);
        var prefix = has && o.ApiToken.Length >= 4 ? o.ApiToken[..Math.Min(7, o.ApiToken.Length)] : null;
        return Results.Ok(new
        {
            envFileFound = envFile,
            hasToken = has,
            tokenPrefix = prefix,
            tokenLength = has ? o.ApiToken.Length : 0,
            tokenLooksLikeReplicate = looksOk,
            hasModelVersion = !string.IsNullOrWhiteSpace(o.FluxModelVersion)
                && !string.Equals(o.FluxModelVersion, "YOUR_REPLICATE_FLUX_VERSION", StringComparison.Ordinal),
        });
    });

    app.MapGet("/api/diagnostics/openrouter", (IOptions<OpenRouterOptions> opt) =>
    {
        var o = opt.Value;
        var has = !string.IsNullOrWhiteSpace(o.ApiKey);
        var looksOk = has && LooksLikeOpenRouterKey(o.ApiKey);
        var prefix = has && o.ApiKey.Length >= 12 ? o.ApiKey[..12] : has ? o.ApiKey[..Math.Min(8, o.ApiKey.Length)] : null;
        return Results.Ok(new
        {
            envFileFound = envFile,
            hasApiKey = has,
            keyPrefix = prefix,
            keyLength = has ? o.ApiKey.Length : 0,
            keyLooksLikeOpenRouter = looksOk,
            model = string.IsNullOrWhiteSpace(o.Model) ? null : o.Model,
            baseUrl = string.IsNullOrWhiteSpace(o.BaseUrl) ? null : o.BaseUrl.TrimEnd('/'),
        });
    });
}

app.MapPost("/api/generate", async Task<IResult> (
    GenerateImageRequest body,
    ISender sender,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(body.BlogPost))
        return TypedResults.BadRequest(new { message = "Blog metni gerekli." });

    try
    {
        var title = string.IsNullOrWhiteSpace(body.Title) ? null : body.Title.Trim();
        var result = await sender.Send(new GenerateImageCommand(body.BlogPost.Trim(), title), cancellationToken);
        return TypedResults.Ok(new
        {
            imageUrl = result.ImageUrl,
            englishPrompt = result.EnglishPrompt,
            replicatePrompt = result.ReplicatePrompt,
        });
    }
    catch (ArgumentException ex)
    {
        return TypedResults.BadRequest(new { message = ex.Message });
    }
    catch (OpenRouterPromptException ex)
    {
        return TypedResults.Json(new { message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (ReplicatePredictionException ex)
    {
        return TypedResults.Json(new { message = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();
