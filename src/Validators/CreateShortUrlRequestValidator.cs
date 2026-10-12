using FluentValidation;
using Microsoft.Extensions.Options;
using UrlShortener.Configurations;
using UrlShortener.DTOs;

namespace UrlShortener.Validators;

public class CreateShortUrlRequestValidator : AbstractValidator<CreateShortUrlRequest>
{
    public CreateShortUrlRequestValidator(IOptions<ShortenerOptions> options)
    {
        var maxDays = options.Value.MaxExpiryDays;

        UrlRules.ApplyUrlRules(RuleFor(x => x.Url));

        UrlRules.ApplyExpiryDaysRule(RuleFor(x => x.ExpiryDays), maxDays);
    }
}
