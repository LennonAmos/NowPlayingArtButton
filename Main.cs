using SuchByte.MacroDeck.Plugins;

namespace NowPlayingArtButton;

public sealed class Main : MacroDeckPlugin
{
    public Main()
    {
        if (!PluginManager.UpdatedPlugins.Contains(this))
        {
            PluginManager.UpdatedPlugins.Add(this);
        }
    }

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
