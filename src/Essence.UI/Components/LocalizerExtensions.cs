using Essence.UI.Resources;
using Essence.UI.Services;
using Microsoft.Extensions.Localization;

namespace Essence.UI.Components;

internal static class LocalizerExtensions
{
    /// <summary>"2 h ago" / "il y a 2 h".</summary>
    public static string Ago(this IStringLocalizer<Strings> localizer, DateTimeOffset when, DateTimeOffset now)
    {
        var (unit, value) = Formatting.Ago(now - when);
        return unit switch
        {
            AgoUnit.JustNow => localizer["AgoJustNow"],
            AgoUnit.Minutes => localizer["AgoMinutes", value],
            AgoUnit.Hours => localizer["AgoHours", value],
            _ => localizer["AgoDays", value],
        };
    }
}
