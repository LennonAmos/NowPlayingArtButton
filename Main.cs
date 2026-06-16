using SuchByte.MacroDeck.Plugins;

namespace NowPlayingArtButton;

public sealed class Main : MacroDeckPlugin
{
    public override bool CanConfigure => false;

    public override void Enable()
    {
        NowPlayingTileService.SetPlugin(this);
        Actions = new List<PluginAction>
        {
            new UseNowPlayingArtButtonAction()
        };
    }
}
