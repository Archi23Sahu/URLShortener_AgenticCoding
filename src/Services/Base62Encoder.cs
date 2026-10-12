namespace UrlShortener.Services;

/// <summary>
/// Converts non-negative numbers to and from Base62 (0-9, A-Z, a-z).
/// Chosen over GUID/UUID because a 6-character code is far shorter and friendlier than a 22-36 character one.
/// </summary>
public static class Base62Encoder
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int Base = 62;

    // long.MaxValue needs 11 Base62 characters.
    private const int MaxEncodedLength = 11;

    public static string Encode(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be non-negative.");
        }

        if (value == 0)
        {
            return Alphabet[0].ToString();
        }

        Span<char> buffer = stackalloc char[MaxEncodedLength];
        var index = buffer.Length;

        while (value > 0)
        {
            buffer[--index] = Alphabet[(int)(value % Base)];
            value /= Base;
        }

        return new string(buffer.Slice(index));
    }

    public static long Decode(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        long result = 0;
        foreach (var c in code)
        {
            var digit = Alphabet.IndexOf(c);
            if (digit < 0)
            {
                throw new FormatException($"'{c}' is not a valid Base62 character.");
            }

            result = checked(result * Base + digit);
        }

        return result;
    }
}
