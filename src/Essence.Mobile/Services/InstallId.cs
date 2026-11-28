namespace Essence.Mobile.Services;

/// <summary>
/// A random id made on this installation's first run and kept with its preferences, which every call to the Essence server
/// carries (<see cref="Header"/>, from <see cref="ServerSettings.CreateApiClient"/>), so that the server can count how many
/// installations use each version (README → Usage). It says nothing about the phone or its user, goes to no other server
/// (GitHub, OpenStreetMap), and a reinstall, or clearing the app's storage, makes a new one.
/// </summary>
public sealed class InstallId
{
    /// <summary>The request header the id goes in.</summary>
    public const string Header = "Essence-Install";

    private const string StorageKey = "essence.install-id";

    public InstallId()
    {
        if (Preferences.Default.Get<string?>(StorageKey, null) is { } kept && Guid.TryParseExact(kept, "N", out _))
        {
            Value = kept;
            return;
        }

        Value = Guid.NewGuid().ToString("N");
        Preferences.Default.Set(StorageKey, Value);
    }

    /// <summary>32 lowercase hex digits.</summary>
    public string Value { get; }
}
