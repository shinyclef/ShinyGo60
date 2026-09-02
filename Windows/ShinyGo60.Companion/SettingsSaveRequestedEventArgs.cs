using ShinyGo60.Companion.Core.Configuration;

namespace ShinyGo60.Companion;

public sealed class SettingsSaveRequestedEventArgs : EventArgs
{
    public SettingsSaveRequestedEventArgs(CompanionConfiguration configuration, bool startWithWindows)
    {
        this.Configuration = configuration;
        this.StartWithWindows = startWithWindows;
    }

    public CompanionConfiguration Configuration { get; }

    public bool StartWithWindows { get; }
}
