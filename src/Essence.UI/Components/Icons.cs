namespace Essence.UI.Components;

/// <summary>Path data (24×24 view box) of the icons drawn in more than one component, and of the long ones.</summary>
internal static class Icons
{
    public const string Star = "M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z";

    /// <summary>Two lines on a chart's axes.</summary>
    public const string Compare = "M4 4v16h16M8 13l3-3 3 2 4-4M8 17l3-1 3 1 4-2";

    public const string Check = "M5 12.5l4.5 4.5L19 7.5";

    public const string Close = "M6 6l12 12M18 6L6 18";

    /// <summary>A paper plane: directions in Google Maps.</summary>
    public const string Directions = "M3 11l19-9-9 19-2-8-8-2z";

    /// <summary>A fuel pump: Best near you.</summary>
    public const string Pump = "M4 20V5a2 2 0 0 1 2-2h6a2 2 0 0 1 2 2v15M3 20h12M7 7h4M14 9h2a2 2 0 0 1 2 2v5a1.5 1.5 0 0 0 3 0V8l-3-3";

    /// <summary>A house: the saved place Home.</summary>
    public const string Home = "M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z";

    /// <summary>A briefcase: the saved place Work.</summary>
    public const string Work = "M5 7h14a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2zM8 7V5a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2";

    /// <summary>A map pin: another saved place, or a dropped pin.</summary>
    public const string Place = "M12 21s-7-6.2-7-11.5a7 7 0 0 1 14 0C19 14.8 12 21 12 21zM12 7a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z";

    /// <summary>A magnifier: where to go.</summary>
    public const string Search = "M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM20 20l-4-4";

    public const string Plus = "M12 5v14M5 12h14";

    /// <summary>A building: an address, in the address search.</summary>
    public const string Building = "M4 21V5l8-3 8 3v16M9 21v-4h6v4M8 8h.01M12 8h.01M16 8h.01M8 12h.01M12 12h.01M16 12h.01";

    /// <summary>A road: a street, in the address search.</summary>
    public const string Street = "M5 21L9 3M19 21L15 3M12 4v2M12 10v3M12 17v3";

    /// <summary>An i in a circle: About, and how Best near you is worked out.</summary>
    public const string Info = "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 16.5v-5M12 7.75v.01";

    /// <summary>Two arrows going round: plan the trip again.</summary>
    public const string Refresh = "M20 12a8 8 0 0 0-15.52-2.74M4 4v5h5M4 12a8 8 0 0 0 15.52 2.74M20 20v-5h-5";

    /// <summary>A pencil (Feather's, MIT): manage the saved places.</summary>
    public const string Pencil = "M17 3a2.83 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z";

    /// <summary>A bin (Feather's, MIT): forget a saved place.</summary>
    public const string Trash = "M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2M10 11v6M14 11v6";

    /// <summary>A gear (Feather's, MIT): manage the cars.</summary>
    public const string Settings =
        "M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z"
        + "M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 "
        + "1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06"
        + "a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 "
        + "2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 "
        + "0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 "
        + "1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21"
        + "a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z";
}
