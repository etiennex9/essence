using Android.App;
using Android.Runtime;

namespace Essence.Mobile;

#if DEBUG
// Debug builds may talk http to the local API (adb reverse, or the emulator's 10.0.2.2); release builds only https.
[Application(NetworkSecurityConfig = "@xml/network_security_config")]
#else
[Application]
#endif
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override void OnCreate()
    {
        base.OnCreate();
        // Before any activity starts: an update found in the background installs only while none shows.
        RegisterActivityLifecycleCallbacks(Updates.AppForeground.Instance);
    }
}
