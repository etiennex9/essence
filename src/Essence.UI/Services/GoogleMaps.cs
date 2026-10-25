using System.Globalization;

namespace Essence.UI.Services;

/// <summary>Links that hand a station over to Google Maps.</summary>
public static class GoogleMaps
{
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
}
