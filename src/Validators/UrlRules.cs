using FluentValidation;
using UrlShortener.Services;

namespace UrlShortener.Validators;

/// <summary>Rule shared by the create and update request validators.</summary>
public static class UrlRules
{
    public const int MaxUrlLength = 2048;

    /// <summary>Optional lifetime: when present it must be between 1 and the configured maximum.</summary>
    public static void ApplyExpiryDaysRule<T>(IRuleBuilder<T, int?> rule, int maxDays) =>
        rule.Must(days => !days.HasValue || (days.Value >= 1 && days.Value <= maxDays))
            .WithMessage($"ExpiryDays must be between 1 and {maxDays}.");

    public static void ApplyUrlRules<T>(IRuleBuilderInitial<T, string> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Url is required.")
            .Must(url => UrlNormalizer.TryNormalize(url, out var normalized) && normalized.Length <= MaxUrlLength)
            .WithMessage($"Url must be an absolute http or https URL of at most {MaxUrlLength} characters and must not contain credentials.");
}
