using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FriendListFrenzy.Services;
using FriendListFrenzy.UI;

namespace FriendListFrenzy;

public sealed class Plugin : IDalamudPlugin
{
    public const string CommandPrimary = "/friendlistfrenzy";
    public const string CommandAlias = "/flf";

    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;

    public static Configuration Config { get; private set; } = null!;
    public static FriendListService Friends { get; private set; } = null!;
    public static BulkRemoverService BulkRemover { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("FriendListFrenzy");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Friends = new FriendListService();
        BulkRemover = new BulkRemoverService();

        mainWindow = new MainWindow();
        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandPrimary, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open FriendList Frenzy window."
        });

        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open FriendList Frenzy window."
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
    }

    private void OnCommand(string command, string args)
    {
        ToggleMainWindow();
    }

    private void ToggleMainWindow()
    {
        mainWindow.IsOpen = !mainWindow.IsOpen;
        if (mainWindow.IsOpen)
        {
            Friends.RequestRefresh();
            Friends.RefreshFromMemory();
        }
    }

    private void DrawUI()
    {
        windowSystem.Draw();
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;

        CommandManager.RemoveHandler(CommandPrimary);
        CommandManager.RemoveHandler(CommandAlias);

        windowSystem.RemoveAllWindows();
        BulkRemover.Dispose();
        Friends.Dispose();
        Config.Save();
    }
}
