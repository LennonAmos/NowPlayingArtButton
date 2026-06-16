using SuchByte.MacroDeck.ActionButton;
using SuchByte.MacroDeck.Plugins;

namespace NowPlayingArtButton;

public sealed class UseNowPlayingArtButtonAction : PluginAction
{
    public override string Name => "Use as now-playing art";

    public override string Description => "Turns this button into a live cover-art tile with song and artist text.";

    public override bool CanConfigure => false;

    public override void Trigger(string clientId, ActionButton actionButton)
    {
        NowPlayingTileService.RegisterButton(actionButton);
    }

    public override void OnActionButtonDelete()
    {
        NowPlayingTileService.PruneMissingButtons();
    }
}
