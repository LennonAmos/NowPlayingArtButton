using SuchByte.MacroDeck.Plugins;

namespace NowPlayingArtButton;

public sealed class Main : MacroDeckPlugin
{
    public override bool CanConfigure => false;

    public override void Enable()
    {
        Actions = new List<PluginAction>
        {
            new UseNowPlayingArtButtonAction(),
            new OpenLyricsProfileAction()
        };
        _ = ActionCleanup.RemoveSleepModeActionsAfterProfilesLoadAsync(this);
        NowPlayingTileService.SetPlugin(this);
        LyricsProfileController.SetPlugin(this);
    }
}
