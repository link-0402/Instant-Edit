using System.Collections;
using System.Reflection;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using InstantEdit.Models;
using InstantEdit.Services;
using Lumina.Data;

namespace InstantEdit.Ui;

/// <summary>Compact resource browser for the authoritative Penumbra resource snapshot.</summary>
public sealed class MainWindow
{
    private readonly Configuration _config; private readonly PenumbraService _penumbra; private readonly OnScreenService _onScreen;
    private readonly BlenderClient _blender; private readonly IDataManager _data; private readonly IChatGui _chat; private readonly IPluginLog _log;
    private readonly object _stateLock = new();
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private bool _open, _blenderOk, _blenderChecking; private int _editing;
    private DateTime _lastBlenderCheck = DateTime.MinValue;
    private string _filter = string.Empty, _sourceFilter = string.Empty, _status = string.Empty; private bool _statusOk = true;

    public MainWindow(Configuration config, PenumbraService penumbra, OnScreenService onScreen, BlenderClient blender,
        IDataManager data, IChatGui chat, IPluginLog log, Action saveConfig, Action restartExportListener, object? unusedUiBuilder = null)
    { _config = config; _penumbra = penumbra; _onScreen = onScreen; _blender = blender; _data = data; _chat = chat; _log = log; }

    public bool IsOpen { get => _open; set => _open = value; }
    public void Open() => _open = true; public void Close() => _open = false; public void Toggle() => _open = !_open;

    public void Draw()
    {
        if (!_open) return;
        if (!ImGui.Begin("Instant Edit##Main", ref _open)) { ImGui.End(); return; }
        DrawHeader();
        ImGui.TextColored(new Vector4(.76f, .78f, .84f, 1), "Browse the visible object hierarchy. Each resource keeps its resolved source and available actions in the same place.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##resource-filter", "Search resource names, paths, or sources…", ref _filter, 256);
        DrawSourceFilters();
        ImGui.Spacing();
        DrawResources();
        DrawFeedback(); ImGui.End();
    }

    private void DrawHeader()
    {
        ImGui.TextColored(new Vector4(.95f, .78f, .35f, 1), "INSTANT EDIT"); ImGui.SameLine(); ImGui.TextColored(new Vector4(.56f, .58f, .65f, 1), "On Screen");
        ImGui.SameLine(0, 12); if (ImGui.SmallButton("Refresh character list")) RequestRefresh();
        var penumbra = false;
        try { penumbra = _penumbra.Available; } catch (Exception e) { _log.Debug(e.Message); }
        StartBlenderCheckIfNeeded(); bool blender, checking; lock (_stateLock) { blender = _blenderOk; checking = _blenderChecking; }
        Status("Penumbra", penumbra, penumbra ? "OK" : "Unavailable"); ImGui.SameLine(0, 10); Status("Blender", blender, checking ? "Checking" : blender ? "OK" : "Offline");
        ImGui.Separator();
    }

    private static void Status(string name, bool good, string value)
    { ImGui.TextColored(good ? new Vector4(.3f, .78f, .5f, 1) : new Vector4(.9f, .45f, .32f, 1), "●"); ImGui.SameLine(0, 3); ImGui.TextColored(new Vector4(.7f, .72f, .78f, 1), $"{name}: {value}"); }

    private void DrawResources()
    {
        var actors = ReadActors().Where(ActorMatches).ToList();
        // Keep room for the status line below the viewport. The old -5px calculation
        // consumed the whole remaining window and clipped the refresh message.
        var viewportHeight = Math.Max(80, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() - 8);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.075f, .085f, .105f, 1));
        if (!ImGui.BeginChild("##resource-browser", new Vector2(0, viewportHeight), true)) { ImGui.EndChild(); ImGui.PopStyleColor(); return; }
        if (_onScreen.IsRefreshing && actors.Count == 0) ImGui.TextColored(new Vector4(.65f, .68f, .75f, 1), "Refreshing resources…");
        else if (actors.Count == 0) ImGui.TextColored(new Vector4(.65f, .68f, .75f, 1), "No snapshot loaded. Use Refresh character list to collect on-screen resources.");
        else foreach (var actor in actors) DrawActor(actor);
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private void DrawActor(ActorView actor)
    {
        var actorId = SafeId($"actor:{actor.Entity.Address:X}:{actor.Entity.ObjectIndex}");
        ImGui.PushID(actorId); DrawOpaqueRow();
        var expanded = _expanded.Contains(actorId);
        if (ImGui.Button(expanded ? "▼##actor-toggle" : "▶##actor-toggle", new Vector2(22, ImGui.GetFrameHeight()))) { if (expanded) _expanded.Remove(actorId); else _expanded.Add(actorId); }
        ImGui.SameLine(0, 4); var header = Safe($"{actor.Category}{(string.IsNullOrWhiteSpace(actor.Name) ? string.Empty : $"  ·  {actor.Name}")}", "Player");
        var actorLabelWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
        if (ImGui.Selectable($"{header}##actor-label", false, ImGuiSelectableFlags.None, new Vector2(actorLabelWidth, ImGui.GetFrameHeight()))) { if (expanded) _expanded.Remove(actorId); else _expanded.Add(actorId); }
        if (expanded)
        {
            DrawSection(actor, ResourceSection.CharacterFeatures, "Character features", actorId + ":features");
            DrawSection(actor, ResourceSection.Gear, "Gear", actorId + ":gear");
            DrawSection(actor, ResourceSection.Other, "Other", actorId + ":other");
        }
        ImGui.PopID();
    }

    private void DrawSection(ActorView actor, ResourceSection section, string label, string key)
    {
        var sectionValue = section.ToString();
        var nodes = actor.Roots.Where(x => string.Equals(Safe(x.Section), sectionValue, StringComparison.OrdinalIgnoreCase) && HasModdedContent(x) && MatchesSource(x));
        nodes = section == ResourceSection.Gear
            ? nodes.OrderBy(x => GearRank(Safe(x.Slot))).ThenBy(x => x.Order)
            : nodes.OrderBy(x => x.Order);
        var ordered = nodes.ToList();
        if (ordered.Count == 0) return;
        // All three sections are siblings beneath an actor. Scope their otherwise
        // identical button/selectable labels so ImGui does not merge their input.
        ImGui.PushID(SafeId(key));
        ImGui.Indent(16); DrawOpaqueRow(); var expanded = _expanded.Contains(key);
        if (ImGui.Button(expanded ? "▼##section-toggle" : "▶##section-toggle", new Vector2(22, ImGui.GetFrameHeight()))) { if (expanded) _expanded.Remove(key); else _expanded.Add(key); }
        ImGui.SameLine(0, 4); var sectionLabelWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
        if (ImGui.Selectable($"{label}##section-label", false, ImGuiSelectableFlags.None, new Vector2(sectionLabelWidth, ImGui.GetFrameHeight()))) { if (expanded) _expanded.Remove(key); else _expanded.Add(key); }
        if (expanded) for (var i = 0; i < ordered.Count; i++) DrawNode(ordered[i], $"{key}:{i}", 2);
        ImGui.Unindent(16);
        ImGui.PopID();
    }

    private void DrawNode(ResourceView node, string scope, int depth)
    {
        // Resource JSON is intentionally treated as untrusted display data.  Do not
        // pass any reflected value directly to an ImGui UTF-8 overload.
        var type = Safe(node.Type, "Resource");
        var name = Safe(node.Name, "Unnamed resource");
        var gamePath = Safe(node.GamePath);
        var actualPath = Safe(node.ActualPath);
        var source = Safe(node.SourceLabel, node.Modded == true ? "Mod" : "Source unavailable");
        var key = SafeId($"{scope}:{type}:{name}:{gamePath}:{actualPath}");
        ImGui.PushID(key);
        ImGui.Indent(depth * 16);
        DrawOpaqueRow();
        var presentation = Safe(node.Slot, KindLabel(type));
        var label = Safe($"{presentation}  ·  {DisplayName(name, actualPath)}  [{source}]", "Unnamed resource");
        var children = node.Children ?? new List<ResourceView>();
        var hasChildren = children.Count > 0;
        var model = IsModel(node);
        var expanded = _expanded.Contains(key);
        var arrow = hasChildren ? (expanded ? "▼" : "▶") : "  ";
        if (ImGui.Button(Safe($"{arrow}##expand:{key}", "  ##expand"), new Vector2(22, ImGui.GetFrameHeight())))
            if (hasChildren) { if (expanded) _expanded.Remove(key); else _expanded.Add(key); }
        if (ImGui.IsItemHovered() && hasChildren) ImGui.SetTooltip(expanded ? "Collapse" : "Expand");
        ImGui.SameLine(0, 4);
        var actionWidth = model && IsSafeModel(node) ? 48 : 0;
        // Calculate after the disclosure control, and never pass a negative width
        // to ImGui. Negative/near-zero selectable widths made text rows disappear
        // at narrower Dalamud scaling factors.
        var labelWidth = Math.Max(80, ImGui.GetContentRegionAvail().X - actionWidth);
        var hovered = ImGui.Selectable(Safe($"{label}##label:{key}", "Unnamed resource##label"), false, ImGuiSelectableFlags.None, new Vector2(labelWidth, ImGui.GetFrameHeight()));
        if (hovered && hasChildren) { if (expanded) _expanded.Remove(key); else _expanded.Add(key); }
        if (model && IsSafeModel(node))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Edit##node-action")) TryEditNode(node);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Edit this model in Blender");
        }
        ShowPathTooltip(ImGui.IsItemHovered() || hovered, actualPath);
        if (expanded && hasChildren)
        {
            for (var i = 0; i < children.Count; i++)
                if (children[i] is not null && HasModdedContent(children[i]) && MatchesSource(children[i])) DrawNode(children[i], $"{scope}:{i}", depth + 1);
        }
        ImGui.Unindent(depth * 16);
        ImGui.PopID();
    }

    private void DrawSourceFilters()
    {
        var actors = ReadActors();
        var groups = new[] { "All sources", "Mod", "Game data", "External resolved file", "Source unavailable" };
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(.58f, .61f, .69f, 1), "SOURCE"); ImGui.SameLine(0, 8);
        foreach (var group in groups)
        {
            var count = group == "All sources"
                ? actors.SelectMany(x => x.Roots).SelectMany(Flatten).Count(HasModdedContent)
                : actors.SelectMany(x => x.Roots).SelectMany(Flatten).Count(x => HasModdedContent(x) && SourceGroup(x.SourceLabel) == group);
            ImGui.PushID($"source-filter:{group}");
            var selected = _sourceFilter == (group == "All sources" ? string.Empty : group);
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.45f, .34f, .16f, 1));
            if (ImGui.SmallButton($"{group}  {count}")) _sourceFilter = group == "All sources" ? string.Empty : group;
            if (selected) ImGui.PopStyleColor();
            if (group != groups[^1]) ImGui.SameLine(0, 6);
            ImGui.PopID();
        }
        ImGui.NewLine();
    }

    private static IEnumerable<ResourceView> Flatten(ResourceView root)
    { yield return root; foreach (var child in root.Children) foreach (var item in Flatten(child)) yield return item; }

    private List<ActorView> ReadActors()
    {
        var result = new List<ActorView>();
        foreach (var entity in _onScreen.Items)
        {
            var prop = entity.GetType().GetProperty("ResourceRoots", BindingFlags.Public | BindingFlags.Instance);
            if (prop?.GetValue(entity) is not IEnumerable roots) continue;
            var parsed = new List<ResourceView>(); var index = 0;
            foreach (var raw in roots) if (raw is not null) parsed.Add(ReadNode(raw, $"entity:{entity.Address:X}:{entity.ObjectIndex}:{index++}"));
            result.Add(new ActorView(entity, ActorCategory(entity), Safe(entity.Name), parsed));
        }
        return result;
    }

    private static string ActorCategory(OnScreenObject entity)
    {
        foreach (var name in new[] { "Category", "ActorCategory", "Type" })
        {
            var value = entity.GetType().GetProperty(name)?.GetValue(entity)?.ToString();
            if (value is not null && new[] { "Player", "Minion", "Mount", "Summon" }.Contains(value, StringComparer.OrdinalIgnoreCase)) return value;
        }
        return "Player";
    }

    private bool ActorMatches(ActorView actor) => (string.IsNullOrWhiteSpace(_filter) || actor.Category.Contains(_filter, StringComparison.OrdinalIgnoreCase) || actor.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) || actor.Roots.Any(Matches)) && actor.Roots.Any(MatchesSource);

    private bool MatchesSource(ResourceView node)
        => string.IsNullOrWhiteSpace(_sourceFilter) || SourceGroup(node.SourceLabel) == _sourceFilter || node.Children.Any(MatchesSource);

    private static ResourceView ReadNode(object raw, string scope)
    {
        var type = raw.GetType(); var children = new List<ResourceView>();
        if (type.GetProperty("Children")?.GetValue(raw) is IEnumerable list) { var i = 0; foreach (var child in list) if (child is not null) children.Add(ReadNode(child, $"{scope}:{i++}")); }
        return new ResourceView(String(type, raw, "Type"), String(type, raw, "Icon"), String(type, raw, "Name"), String(type, raw, "GamePath"), String(type, raw, "ActualPath"), Source(type, raw), EnumString(type, raw, nameof(ResourceNode.ResourceSection)), String(type, raw, nameof(ResourceNode.SlotLabel)), Int(type, raw, nameof(ResourceNode.SortOrder)), Bool(type, raw, nameof(ResourceNode.IsModdedSubtree)), children);
    }

    private static string String(Type type, object value, string name) => type.GetProperty(name)?.GetValue(value) as string ?? string.Empty;
    private static string EnumString(Type type, object value, string name) => type.GetProperty(name)?.GetValue(value)?.ToString() ?? string.Empty;
    private static string Source(Type type, object value)
    { foreach (var name in new[] { "SourceLabel", "Source", "SourceState", "State" }) { var valueText = String(type, value, name); if (!string.IsNullOrWhiteSpace(valueText)) return valueText; } return "Source unavailable"; }
    private static int Int(Type type, object value, string name) => int.TryParse(type.GetProperty(name)?.GetValue(value)?.ToString(), out var result) ? result : int.MaxValue;
    private static bool? Bool(Type type, object value, string name) { var raw = type.GetProperty(name)?.GetValue(value); return raw is bool valueBool ? valueBool : null; }
    private static string KindLabel(string type) => string.IsNullOrWhiteSpace(type) ? "Resource" : type;
    private static string DisplayName(string name, string actualPath)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return Safe(Path.GetFileName(actualPath), "Unnamed resource");
    }
    private static bool IsModel(ResourceView node) => node.Type.Contains("model", StringComparison.OrdinalIgnoreCase) || node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);
    private static bool IsSafeModel(ResourceView node) => IsModel(node) && (node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase));
    private static bool HasModdedContent(ResourceView node) => node.Modded != false || node.Children.Any(HasModdedContent);
    private bool Matches(ResourceView node)
    {
        var filter = Safe(_filter);
        var children = node.Children ?? new List<ResourceView>();
        return string.IsNullOrWhiteSpace(filter) || Safe(node.Name).Contains(filter, StringComparison.OrdinalIgnoreCase) || Safe(node.Type).Contains(filter, StringComparison.OrdinalIgnoreCase) || Safe(node.SourceLabel).Contains(filter, StringComparison.OrdinalIgnoreCase) || Safe(node.ActualPath).Contains(filter, StringComparison.OrdinalIgnoreCase) || children.Any(Matches);
    }
    private static string SourceGroup(string source)
    { var s = source.ToLowerInvariant(); if (s.Contains("mod")) return "Mod"; if (s.Contains("game") || s.Contains("base")) return "Game data"; if (s.Contains("external") || s.Contains("resolved")) return "External resolved file"; return "Source unavailable"; }
    private static int GearRank(string slot)
    {
        return slot.ToLowerInvariant() switch
        {
            "head" => 0, "body" => 1, "hands" => 2, "legs" => 3, "feet" => 4,
            "earrings" or "ears" => 5, "necklace" or "neck" => 6, "bracelet" or "wrists" => 7,
            "right ring" or "right finger" => 8, "left ring" or "left finger" => 9, _ => 100,
        };
    }

    private void DrawOpaqueRow()
    { var p = ImGui.GetCursorScreenPos(); ImGui.GetWindowDrawList().AddRectFilled(p, p + new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight()), ImGui.GetColorU32(new Vector4(.11f, .12f, .15f, 1)), 2); }
    private static void ShowPathTooltip(bool hovered, string path)
    {
        if (!hovered) return;
        var safePath = Safe(path);
        ImGui.SetTooltip(string.IsNullOrWhiteSpace(safePath) ? "No resolved path" : $"{safePath}\nRight-click to copy");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) ImGui.SetClipboardText(safePath);
    }

    private void TryEditNode(ResourceView node)
    {
        foreach (var entity in _onScreen.Items)
            foreach (var model in entity.Models)
                if (string.Equals(model.GamePath, node.GamePath, StringComparison.OrdinalIgnoreCase) || string.Equals(model.LocalPath, node.ActualPath, StringComparison.OrdinalIgnoreCase)) { EditModel(entity, model); return; }
        SetStatus("This model is no longer available.", false);
    }

    private void RequestRefresh() { try { _onScreen.RequestRefresh(); SetStatus("Refreshing resources…", true); } catch (Exception e) { _log.Debug(e.Message); SetStatus("Refresh unavailable. Is Penumbra running?", false); } }
    private void EditModel(OnScreenObject item, MdlFile model) { if (Interlocked.CompareExchange(ref _editing, 1, 0) != 0) { SetStatus("Another model is already being sent.", false); return; } _ = Task.Run(() => EditModelAsync(item, model, _config.BlenderPort, _config.LaunchBlender, _config.BlenderPath, _config.ListenPort, _config.ModName)); }
    private async Task EditModelAsync(OnScreenObject item, MdlFile model, int blenderPort, bool launch, string? blenderPath, int listenPort, string modName)
    {
        try { byte[] bytes = model.IsFilePath ? await File.ReadAllBytesAsync(model.LocalPath) : (await _data.GetFileAsync<FileResource>(model.LocalPath, CancellationToken.None))?.Data ?? throw new InvalidOperationException($"Game file not found: {model.LocalPath}"); var dir = Path.Combine(Path.GetTempPath(), "InstantEdit"); Directory.CreateDirectory(dir); var file = Path.Combine(dir, $"{Sanitize(item.Name)}-{item.ObjectIndex}-{model.FileName}"); await File.WriteAllBytesAsync(file, bytes); await EnsureBlenderAsync(blenderPort, launch, blenderPath); await _blender.SendImportAsync(blenderPort, file, model.GamePath, item.ObjectIndex, $"{item.Name} {model.FileName}", listenPort, modName); SetStatus($"Sent {model.FileName} to Blender.", true); _chat.Print($"Instant Edit: {model.FileName} sent to Blender."); }
        catch (Exception e) { _log.Error(e, "Failed to send model to Blender."); SetStatus($"Failed: {e.Message}", false); _chat.PrintError($"Instant Edit: could not send model to Blender: {e.Message}"); } finally { Volatile.Write(ref _editing, 0); }
    }
    private async Task EnsureBlenderAsync(int port, bool launch, string? path) { if (await _blender.IsReachableAsync(port)) return; if (!launch) throw new InvalidOperationException("Blender is not running and auto-launch is disabled."); SetStatus("Launching Blender…", true); var exe = await Task.Run(() => _blender.FindBlenderExecutable(path)); if (exe is null) throw new InvalidOperationException("Could not find Blender. Set the executable path in settings."); await Task.Run(() => _blender.Launch(exe)); var deadline = DateTime.UtcNow.AddSeconds(60); while (DateTime.UtcNow < deadline) { await Task.Delay(1000); if (await _blender.IsReachableAsync(port)) return; } throw new InvalidOperationException("Blender did not come online in time. Check that the Yet Another Addon is enabled."); }
    private void StartBlenderCheckIfNeeded() { lock (_stateLock) { if (_blenderChecking || (DateTime.UtcNow - _lastBlenderCheck).TotalSeconds <= 2) return; _blenderChecking = true; _lastBlenderCheck = DateTime.UtcNow; } _ = Task.Run(async () => { var ok = await _blender.IsReachableAsync(_config.BlenderPort); lock (_stateLock) { _blenderOk = ok; _blenderChecking = false; } }); }
    private void DrawFeedback() { string text; bool ok; lock (_stateLock) { text = _status; ok = _statusOk; } if (text.Length > 0) ImGui.TextColored(ok ? new Vector4(.4f, .85f, .6f, 1) : new Vector4(1, .55f, .3f, 1), text); }
    private void SetStatus(string text, bool ok) { lock (_stateLock) { _status = text; _statusOk = ok; } }
    private static string Sanitize(string name) { var invalid = Path.GetInvalidFileNameChars(); var value = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(); return value.Length == 0 ? "Object" : value; }

    private static string Safe(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string SafeId(string? value)
    {
        var id = Safe(value, "resource");
        return id.Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    private sealed record ActorView(OnScreenObject Entity, string Category, string Name, List<ResourceView> Roots);
    private sealed record ResourceView(string Type, string Icon, string Name, string GamePath, string ActualPath, string SourceLabel, string Section, string Slot, int Order, bool? Modded, List<ResourceView> Children);
}
