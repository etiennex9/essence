using AndroidX.Core.View;

namespace Essence.Mobile;

/// <summary>The status and navigation bars' icons, dark over the light theme's background and light over the dark one's.</summary>
internal static class SystemBars
{
    public static void Update()
    {
        if (Platform.CurrentActivity?.Window is not { } window)
        {
            return;
        }

        var light = Application.Current?.RequestedTheme != AppTheme.Dark;
        if (WindowCompat.GetInsetsController(window, window.DecorView) is not { } controller)
        {
            return;
        }

        controller.AppearanceLightStatusBars = light;
        controller.AppearanceLightNavigationBars = light;
    }
}
