using System.Globalization;
using System.Text.RegularExpressions;
using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>Links that hand a station over to Google Maps.</summary>
public static partial class GoogleMaps
{
    /// <summary>
    /// The station looked up on Google Maps, to read the hours Google has for it, when Essence has none or the user doubts
    /// them. It's looked up by its brand (else its name, unless that's only a numbered company's, "9214-4609 Québec inc.") and its
    /// address. Only a link the user follows: nothing of Google's comes back to Essence, whose map isn't Google's.
    /// </summary>
    public static string Search(string? brand, string? name, string address, string? postalCode)
    {
        var what = !Brands.IsNone(brand) ? brand.Trim()
            : string.IsNullOrWhiteSpace(name) || NumberedCompany().IsMatch(name) ? null
            : name.Trim();
        var where = $"{Spaces().Replace(address, " ").Replace(" ,", ",").Trim()} {postalCode?.Trim()}".Trim();
        var query = what is null ? where : $"{what}, {where}";
        return "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(query);
    }

    /// <summary>
    /// Driving directions from where the user is to a point. With <paramref name="navigate"/>, the Google Maps app starts
    /// the navigation at once (a browser shows the directions either way).
    /// </summary>
    public static string Directions(double latitude, double longitude, bool navigate = false)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://www.google.com/maps/dir/?api=1&destination={latitude:0.######},{longitude:0.######}&travelmode=driving");
        return navigate ? url + "&dir_action=navigate" : url;
    }

    /// <summary>
    /// Driving directions from where the user is to a destination, by way of a stop (a station on the way). With
    /// <paramref name="navigate"/>, the Google Maps app starts the navigation at once.
    /// </summary>
    public static string DirectionsVia(double latitude, double longitude, double viaLatitude, double viaLongitude, bool navigate = false)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"https://www.google.com/maps/dir/?api=1&destination={latitude:0.######},{longitude:0.######}"
            + $"&waypoints={viaLatitude:0.######},{viaLongitude:0.######}&travelmode=driving");
        return navigate ? url + "&dir_action=navigate" : url;
    }

    [GeneratedRegex(@"^\d{4}-\d{4} Qu[eé]bec", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedCompany();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
