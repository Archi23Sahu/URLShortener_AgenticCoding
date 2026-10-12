using FluentValidation;
using Microsoft.Extensions.Options;
using UrlShortener.Configurations;
using UrlShortener.DTOs;

namespace UrlShortener.Validators;

public class UpdateShortUrlRequestValidator : AbstractValidator<UpdateShortUrlRequest>
{
    public UpdateShortUrlRequestValidator(IOptions<ShortenerOptions> options)
    {
        var maxDays = options.Value.MaxExpiryDays;

        UrlRules.ApplyUrlRules(RuleFor(x => x.Url));

        UrlRules.ApplyExpiryDaysRule(RuleFor(x => x.ExpiryDays), maxDays);
    }
}
