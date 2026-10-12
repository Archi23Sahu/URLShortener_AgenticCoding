namespace UrlShortener.Controllers;

public static class ApiRoutes
{
    /// <summary>
    /// Route segment for a short code: 1-12 Base62 characters.
    /// Inside a route template, "{" and "}" must be doubled, and "[" and "]" must be doubled too
    /// (MVC treats [name] as a token such as [controller]). Anything that does not match
    /// (for example "../x" or "a;DROP") never reaches a controller and gets a plain 404.
    /// </summary>
    public const string CodeSegment = "{code:regex(^[[0-9A-Za-z]]{{1,12}}$)}";
}
