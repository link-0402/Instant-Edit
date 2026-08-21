using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace InstantEdit.Ui;

/// <summary>Dedicated Dalamud configuration surface; kept separate from model selection.</summary>
public sealed class SettingsWindow
{
    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly Action _restartExportListener;
    private readonly IPluginLog _log;
    private bool _open;

    public SettingsWindow(Configuration config, Action saveConfig, Action restartExportListener, IPluginLog log)
    { _config = config; _saveConfig = saveConfig; _restartExportListener = restartExportListener; _log = log; }

    public bool IsOpen { get => _open; set => _open = value; }
    public void Open() => _open = true;
    public void Close() => _open = false;
    public void Toggle() => _open = !_open;

    public void Draw()
    {
        if (!_open) return;
        if (!ImGui.Begin("Instant Edit Settings##Settings", ref _open)) { ImGui.End(); return; }
        ImGui.TextColored(new Vector4(.95f, .78f, .35f, 1), "INSTANT EDIT SETTINGS");
        ImGui.TextColored(new Vector4(.58f, .6f, .67f, 1), "Connection and export preferences");
        ImGui.Spacing();
        ImGui.Separator(); ImGui.Text("Blender connection");
        var blenderPort = _config.BlenderPort; if (ImGui.InputInt("Blender port", ref blenderPort)) { _config.BlenderPort = blenderPort; Save(); }
        var launch = _config.LaunchBlender; if (ImGui.Checkbox("Launch Blender when closed", ref launch)) { _config.LaunchBlender = launch; Save(); }
        var path = _config.BlenderPath; if (ImGui.InputText("Blender path (optional)", ref path, 512)) { _config.BlenderPath = path; Save(); }
        if (!string.IsNullOrWhiteSpace(_config.BlenderPath) && !File.Exists(_config.BlenderPath)) ImGui.TextColored(new Vector4(1, .6f, .3f, 1), "Path not found; auto-detection will be used.");
        ImGui.Spacing(); ImGui.Separator(); ImGui.Text("Export target");
        var listenPort = _config.ListenPort; if (ImGui.InputInt("Listener port", ref listenPort)) { var changed = listenPort != _config.ListenPort; _config.ListenPort = listenPort; Save(); if (changed) RestartListener(); }
        ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "Exports are written to this persistent Penumbra mod.");
        var modName = _config.ModName; if (ImGui.InputText("Persistent mod name", ref modName, 64)) { _config.ModName = modName; Save(); }
        ImGui.End();
    }

    private void Save()
    {
        _config.BlenderPort = Math.Clamp(_config.BlenderPort, 1, 65535);
        _config.ListenPort = Math.Clamp(_config.ListenPort, 1, 65535);
        _saveConfig();
    }
    private void RestartListener() { try { _restartExportListener(); } catch (Exception e) { _log.Error(e, "Failed to restart the export listener."); } }
}
