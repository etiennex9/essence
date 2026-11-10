using System.Globalization;
using Android.Content;

namespace Essence.Mobile;

/// <summary>
/// .NET keeps the language the process started with, while Android's can change under it: the phone's, or Essence's own
/// (Settings → Apps → Essence → Language). The app and the widgets take Android's whenever they start something.
/// </summary>
internal static class AppCulture
{
    public static void Apply(Context context)
    {
        if (context.Resources?.Configuration?.Locales is not { IsEmpty: false } locales || locales.Get(0)?.ToLanguageTag() is not { } tag)
        {
            return;
        }

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(tag);
        }
        catch (CultureNotFoundException)
        {
            return;
        }

        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
