using SuchByte.MacroDeck.ActionButton;
using SuchByte.MacroDeck.Plugins;

namespace NowPlayingArtButton;

public sealed class ExitLyricsProfileAction : PluginAction
{
    public override string Name => "Exit Synced Lyrics Profile";

    public override string Description => "Returns to the profile that opened the synced lyrics screen.";

    public override void Trigger(string clientId, ActionButton actionButton)
    {
        LyricsProfileController.ExitLyricsProfile(clientId);
    }
}
