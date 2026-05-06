namespace ThumbnailGenerator.Api.Services;

/// <summary>
/// REST <c>POST /v1/predictions</c> gövdesindeki <c>version</c> alanı yalnızca sürüm hash'idir.
/// Node örneğindeki <c>owner/model:hash</c> biçiminden hash'i ayıklar.
/// </summary>
public static class ReplicateVersionNormalizer
{
    /// <summary>Replicate sürüm kimlikleri genelde 64 hex karakterdir; kısa önekler için en az 32 karakter şartı kullanılır.</summary>
    public static string Normalize(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0)
            return s;

        var lastColon = s.LastIndexOf(':');
        if (lastColon >= 0 && lastColon < s.Length - 1)
        {
            var tail = s[(lastColon + 1)..].Trim();
            if (tail.Length >= 32)
                return tail;
        }

        return s;
    }
}
