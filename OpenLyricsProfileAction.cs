using SuchByte.MacroDeck.ActionButton;
using SuchByte.MacroDeck.Plugins;

namespace NowPlayingArtButton;

public sealed class OpenLyricsProfileAction : PluginAction
{
    public override string Name => "Open Synced Lyrics Profile";

    public override string Description => "Switches to a full-screen Macro Deck profile with synced now-playing lyrics.";

    public override void Trigger(string clientId, ActionButton actionButton)
    {
        LyricsProfileController.GoToLyricsProfile(clientId);
    }
}
