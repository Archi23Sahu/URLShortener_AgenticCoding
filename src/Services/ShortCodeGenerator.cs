namespace UrlShortener.Services;

/// <summary>
/// Turns a database identity value into a short code.
/// Adding 62^5 before encoding guarantees every code has at least 6 characters,
/// so the first link is "100001" instead of "1".
/// </summary>
/// <remarks>
/// Trade-off: codes are sequential, so they are guessable and enumerable.
/// That is acceptable for this prototype (links are not secret) and is listed under Limitations.
/// </remarks>
public static class ShortCodeGenerator
{
    /// <summary>62^5 = 916,132,832. Smallest value that encodes to 6 Base62 characters.</summary>
    public const long MinOffset = 916_132_832L;

    public static string FromId(long id) => Base62Encoder.Encode(id + MinOffset);
}
