namespace UrlShortener.Exceptions;

/// <summary>The short code does not exist. Maps to HTTP 404.</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string code)
        : base($"No short URL exists for code '{code}'.")
    {
    }
}

/// <summary>The short code exists but its expiry time has passed. Maps to HTTP 410.</summary>
public class LinkExpiredException : Exception
{
    public LinkExpiredException(string code)
        : base($"The short URL '{code}' has expired.")
    {
    }
}

/// <summary>
/// The request is well-formed JSON but not acceptable (for example a bad date range). Maps to HTTP 400 with
/// the message as detail. Deliberately narrower than ArgumentException: an ArgumentException raised by framework or
/// library code is a server fault (HTTP 500), and its message must not reach the client.
/// </summary>
public class InvalidRequestException : ArgumentException
{
    public InvalidRequestException(string message)
        : base(message)
    {
    }
}

/// <summary>Optimistic locking failed: another request changed the row first. Maps to HTTP 409.</summary>
public class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(Exception inner)
        : base("The short URL was modified by another request. Reload it and try again.", inner)
    {
    }
}
