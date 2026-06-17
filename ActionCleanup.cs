using SuchByte.MacroDeck.ActionButton;
using SuchByte.MacroDeck.Logging;
using SuchByte.MacroDeck.Plugins;
using SuchByte.MacroDeck.Profiles;

namespace NowPlayingArtButton;

internal static class ActionCleanup
{
    public static async Task RemoveSleepModeActionsAfterProfilesLoadAsync(MacroDeckPlugin plugin)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var changed = RemoveSleepModeActionsFromNowPlayingButtons();
            if (changed.RemovedActions > 0 || ProfileManager.Profiles.Count > 0)
            {
                MacroDeckLogger.Info(plugin, $"Now Playing action cleanup scanned {changed.ScannedButtons} buttons and removed {changed.RemovedActions} Sleep Mode action(s).");
                return;
            }
        }

        MacroDeckLogger.Warning(plugin, "Now Playing action cleanup could not see loaded profiles after waiting.");
    }

    public static CleanupResult RemoveSleepModeActionsFromNowPlayingButtons()
    {
        List<ActionButton> buttons;
        try
        {
            buttons = ProfileManager.Profiles
                .ToArray()
                .SelectMany(profile => profile.Folders.ToArray())
                .SelectMany(folder => folder.ActionButtons.ToArray())
                .Where(IsNowPlayingButton)
                .ToList();
        }
        catch (InvalidOperationException)
        {
            return new CleanupResult(0, 0);
        }

        var scannedButtons = 0;
        var removedActions = 0;
        foreach (var button in buttons)
        {
            scannedButtons++;
            removedActions += RemoveSleepModeActions(button);
        }

        if (removedActions > 0)
        {
            ProfileManager.Save();
        }

        return new CleanupResult(scannedButtons, removedActions);
    }

    public static int RemoveSleepModeActions(ActionButton button)
    {
        var removed = 0;
        removed += RemoveSleepModeActions(button.Actions);
        removed += RemoveSleepModeActions(button.ActionsRelease);
        removed += RemoveSleepModeActions(button.ActionsLongPress);
        removed += RemoveSleepModeActions(button.ActionsLongPressRelease);
        return removed;
    }

    private static int RemoveSleepModeActions(List<PluginAction?> actions)
    {
        var removed = actions.RemoveAll(IsSleepModeAction);
        return removed;
    }

    private static bool IsNowPlayingButton(ActionButton button)
    {
        return button.Actions.Any(action =>
                   action is UseNowPlayingArtButtonAction ||
                   action is OpenLyricsProfileAction) ||
               IsNowPlayingIcon(button.IconOff) ||
               IsNowPlayingIcon(button.IconOn) ||
               IsNowPlayingLabel(button.LabelOff?.LabelText) ||
               IsNowPlayingLabel(button.LabelOn?.LabelText);
    }

    private static bool IsNowPlayingIcon(string? icon)
    {
        return !string.IsNullOrWhiteSpace(icon) &&
               icon.StartsWith("Now Playing Art Button", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNowPlayingLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("current_playing_title", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("current_playing_artist", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("youtube_title", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("youtube_artist", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSleepModeAction(PluginAction? action)
    {
        if (action is null)
        {
            return false;
        }

        var type = action.GetType();
        var fullName = type.FullName ?? string.Empty;
        var name = type.Name;
        return fullName.Contains("MacroDeckSleepMode", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("GoToSleepProfileAction", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ToggleSleepModeAction", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("SleepModeOnAction", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("SleepModeOffAction", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("WakeFromSleepProfileAction", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record CleanupResult(int ScannedButtons, int RemovedActions);
}
