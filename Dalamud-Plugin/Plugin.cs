using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using InstantEdit.Services;
using InstantEdit.Ui;

namespace InstantEdit;

public sealed class Plugin : IDalamudPlugin
{
    private readonly object                    _configLock = new();
    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager         _commands;
    private readonly IPluginLog              _log;
    private readonly Configuration           _config;
    private readonly PenumbraService         _penumbra;
    private readonly OnScreenService         _onScreen;
    private readonly ExportContextRegistry   _contexts;
    private readonly BlenderClient           _blender;
    private readonly ExportServer            _exportServer;
    private readonly MainWindow              _window;
    private readonly SettingsWindow          _settingsWindow;

    public string Name => "Instant Edit";

    public Plugin(
        IDalamudPluginInterface pi,
        ICommandManager commands,
        IChatGui chat,
        IClientState clientState,
        IPluginLog log,
        IDataManager data,
        ITextureProvider textureProvider,
        IObjectTable objects,
        IFramework framework)
    {
        _pi       = pi;
        _commands = commands;
        _log      = log;

        _config    = pi.GetPluginConfig() as Configuration ?? new Configuration();
        if (_config.Version < 7)
        {
            _config.Version = 7;
            pi.SavePluginConfig(_config);
        }
        _penumbra  = new PenumbraService(pi, framework, log, objects);
        _onScreen  = new OnScreenService(objects, clientState, framework, _penumbra, log);
        _contexts  = new ExportContextRegistry(
            Guid.NewGuid().ToString("N"),
            _config.ExportContexts,
            contexts =>
            {
                lock (_configLock)
                {
                    _config.ExportContexts = contexts.ToList();
                    _pi.SavePluginConfig(_config);
                }
            },
            _onScreen.GetActorIdentity);
        _blender   = new BlenderClient(
            log,
            _config.ListenPort,
            _config.ModName,
            _contexts,
            _onScreen.GetActorIdentity);
        _exportServer = new ExportServer(_config, _penumbra, _contexts, log);
        _window    = new MainWindow(
            _config,
            _penumbra,
            _onScreen,
            _blender,
            data,
            chat,
            log,
            SaveConfiguration,
            () => _exportServer.Restart(),
            _pi.UiBuilder,
            textureProvider);
        _settingsWindow = new SettingsWindow(
            _config,
            SaveConfiguration,
            () => _exportServer.Restart(),
            _log);

        _pi.UiBuilder.Draw += _window.Draw;
        _pi.UiBuilder.Draw += _settingsWindow.Draw;
        _pi.UiBuilder.OpenMainUi += _window.Open;
        _pi.UiBuilder.OpenConfigUi += _settingsWindow.Open;

        _commands.AddHandler("/ie", new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Instant Edit window. Use /ie refresh to refresh the on-screen list.",
        });

        try
        {
            _exportServer.Start();
        }
        catch (Exception e)
        {
            // A listener conflict must not prevent the rest of the plugin from loading.
            _log.Error(e, "Instant Edit export receiver could not start.");
        }

        _log.Information("Instant Edit loaded.");
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            _onScreen.RequestRefresh();
            return;
        }

        _window.Toggle();
    }

    private void SaveConfiguration()
    {
        lock (_configLock)
            _pi.SavePluginConfig(_config);
    }

    public void Dispose()
    {
        _exportServer.Dispose();
        _contexts.Dispose();
        _blender.Dispose();
        _commands.RemoveHandler("/ie");
        _pi.UiBuilder.Draw -= _window.Draw;
        _pi.UiBuilder.Draw -= _settingsWindow.Draw;
        _pi.UiBuilder.OpenMainUi -= _window.Open;
        _pi.UiBuilder.OpenConfigUi -= _settingsWindow.Open;
        _window.Dispose();
        SaveConfiguration();
    }
}
