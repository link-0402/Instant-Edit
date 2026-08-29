using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using InstantEdit.Models;
using InstantEdit.Services;
using Lumina.Data;

namespace InstantEdit.Ui;

/// <summary>Compact resource browser for the authoritative Penumbra resource snapshot.</summary>
public sealed class MainWindow : Window, IDisposable
{
    private const string WindowOptionsPopupName = "WindowSystemContextActions";
    private const string KofiUrl = "https://ko-fi.com/luci_xiv";
    private readonly Configuration _config; private readonly PenumbraService _penumbra; private readonly OnScreenService _onScreen;
    private readonly BlenderClient _blender; private readonly IDataManager _data; private readonly IChatGui _chat; private readonly IPluginLog _log;
    private readonly MaterialPreviewBundleBuilder _materialPreviews;
    private readonly ResourceSourceAttributor _resourceSources;
    private readonly Action _saveConfig;
    private readonly IUiBuilder _uiBuilder;
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedFiltered = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, IDalamudTextureWrap> _slotIcons = new Dictionary<string, IDalamudTextureWrap>();
    private bool _blenderOk, _blenderChecking; private int _editing;
    private DateTime _lastBlenderCheck = DateTime.MinValue;
    private DateTime _lastModListRefresh = DateTime.MinValue;
    private string _filter = string.Empty, _modFilter = string.Empty, _resourceTypeFilter = string.Empty, _status = string.Empty;
    private string? _selectedModDirectory, _loadedModDirectory;
    private IReadOnlyList<PenumbraMod> _mods = Array.Empty<PenumbraMod>();
    private ActorView? _loadedModView;
    private CancellationTokenSource? _modLoadCts;
    private bool _modLoading, _modLoadFailed;
    private bool _statusOk = true;

    public MainWindow(Configuration config, PenumbraService penumbra, OnScreenService onScreen, BlenderClient blender,
        IDataManager data, IChatGui chat, IPluginLog log, Action saveConfig, Action restartExportListener, IUiBuilder uiBuilder,
        ITextureProvider textureProvider)
        : base("XIV Instant Edit##Main")
    {
        _config = config; _penumbra = penumbra; _onScreen = onScreen; _blender = blender; _data = data; _chat = chat; _log = log;
        _resourceSources = new ResourceSourceAttributor(penumbra, log);
        _materialPreviews = new MaterialPreviewBundleBuilder(data, log, _resourceSources);
        _saveConfig = saveConfig;
        _uiBuilder = uiBuilder;
        AllowPinning = true;
        AllowClickthrough = true;
        AllowBackgroundBlur = true;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Heart,
            Click = _ => OpenKofiPage(),
            ShowTooltip = () => ImGui.SetTooltip("♥ Support me on Ko-fi"),
        });
        _ = uiBuilder.RunWhenUiPrepared(() => LoadSlotIcons(uiBuilder, textureProvider), true);
    }

    public void Dispose()
    {
        _lifetimeCts.Cancel();
        _modLoadCts?.Cancel();
        _modLoadCts?.Dispose();
        _modLoadCts = null;
        IReadOnlyDictionary<string, IDalamudTextureWrap> icons;
        lock (_stateLock)
        {
            icons = _slotIcons;
            _slotIcons = new Dictionary<string, IDalamudTextureWrap>();
        }

        foreach (var icon in icons.Values.Distinct())
            icon.Dispose();
        _lifetimeCts.Dispose();
    }

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;

    private void OpenKofiPage()
    {
        try
        {
            Util.OpenLink(KofiUrl);
        }
        catch (Exception e)
        {
            _log.Error(e, "Could not open the XIV Instant Edit Ko-fi page.");
        }
    }

    public override void Draw()
    {
        DrawHeader();
        if (ImGui.BeginTabBar("##instant-edit-tabs"))
        {
            if (ImGui.BeginTabItem("On Screen"))
            {
                DrawOnScreenTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Mod Browser"))
            {
                DrawModsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
        DrawFeedback();
        DrawWindowOptionsExtension();
    }

    /// <summary>
    /// Adds the plugin-specific visibility option to Dalamud's standard window options popup.
    /// The built-in popup is drawn by <see cref="WindowSystem"/> after the window content, so
    /// this callback is registered immediately after the window system draw callback.
    /// </summary>
    public void DrawWindowOptionsExtension()
    {
        if (!ImGui.IsPopupOpen(WindowOptionsPopupName))
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 1f);
        if (ImGui.BeginPopup(WindowOptionsPopupName, ImGuiWindowFlags.NoMove))
        {
            var keepVisible = _config.KeepVisibleWhenUiHidden;
            if (ImGui.Checkbox("Don't hide the plugin window when hiding UI", ref keepVisible))
            {
                _config.KeepVisibleWhenUiHidden = keepVisible;
                _uiBuilder.DisableUserUiHide = keepVisible;
                _saveConfig();
            }
            ImGuiComponents.HelpMarker("Keep XIV Instant Edit visible when the game UI is hidden with Scroll Lock.");
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }

    private void DrawOnScreenTab()
    {
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##resource-filter", "Search", ref _filter, 256);
        var actors = ReadActors();
        DrawResourceTypeFilters(actors);
        ImGui.Spacing();
        DrawResources(actors);
    }

    private void DrawModsTab()
    {
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##mod-filter", "Search", ref _modFilter, 256);

        var mods = ReadMods();
        var filteredMods = mods.Where(ModMatches).ToArray();
        var listHeight = Math.Min(180, Math.Max(72, filteredMods.Length * (ImGui.GetFrameHeightWithSpacing()) + 8));
        if (ImGui.BeginChild("##mod-list", new Vector2(0, listHeight), true))
        {
            if (filteredMods.Length == 0)
                ImGui.TextColored(new Vector4(.65f, .68f, .75f, 1), "No matching Penumbra mods.");
            else
                foreach (var mod in filteredMods)
                {
                    var selected = string.Equals(_selectedModDirectory, mod.Directory, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable($"{mod.Name}##mod:{SafeId(mod.Directory)}", selected))
                    {
                        CancelModLoad();
                        _selectedModDirectory = mod.Directory;
                        _loadedModDirectory = null;
                        _loadedModView = null;
                    }
                }
            ImGui.EndChild();
        }

        var selectedMod = mods.FirstOrDefault(mod =>
            string.Equals(mod.Directory, _selectedModDirectory, StringComparison.OrdinalIgnoreCase));
        if (selectedMod is null)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(.65f, .68f, .75f, 1), "Select a Penumbra mod to browse its resources.");
            return;
        }

        var modView = GetModView(selectedMod);
        if (modView is null)
        {
            ImGui.Spacing();
            bool loading;
            bool failed;
            lock (_stateLock)
            {
                loading = _modLoading;
                failed = _modLoadFailed;
            }
            var message = loading ? "Scanning the selected Penumbra mod..." :
                failed ? "Could not read the selected Penumbra mod." : "No supported resources found.";
            var color = failed ? new Vector4(.9f, .55f, .35f, 1) : new Vector4(.65f, .68f, .75f, 1);
            ImGui.TextColored(color, message);
            return;
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(.76f, .78f, .84f, 1), selectedMod.Name);
        DrawResourceTypeFilters([modView]);
        ImGui.Spacing();
        DrawResources([modView], "No supported models, textures, or materials found in this mod.");
    }

    private void DrawHeader()
    {
        ImGui.TextColored(new Vector4(.95f, .78f, .35f, 1), "XIV INSTANT EDIT"); ImGui.SameLine(); ImGui.TextColored(new Vector4(.56f, .58f, .65f, 1), "On Screen");
        ImGui.SameLine(0, 12); if (ImGui.SmallButton("Refresh character list")) RequestRefresh();
        var penumbra = false;
        try { penumbra = _penumbra.Available; } catch (Exception e) { _log.Debug(e.Message); }
        StartBlenderCheckIfNeeded(); bool blender; lock (_stateLock) blender = _blenderOk;
        Status("Penumbra", penumbra, penumbra ? "OK" : "Unavailable"); ImGui.SameLine(0, 10); Status("Blender", blender, blender ? "Online" : "Offline");
        ImGui.Separator();
        DrawImportOptions();
    }

    private void DrawImportOptions()
    {
        ImGui.TextColored(new Vector4(.76f, .78f, .84f, 1), "IMPORT OPTIONS");
        var useExistingSkeleton = _config.UseExistingSkeleton;
        if (ImGui.Checkbox("Remove imported armature and use existing skeleton", ref useExistingSkeleton))
        {
            _config.UseExistingSkeleton = useExistingSkeleton;
            SaveImportOptions();
        }

        if (_config.UseExistingSkeleton)
        {
            ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "Imported meshes receive an Armature modifier targeting this Blender object:");
            var skeletonName = _config.SkeletonObjectName;
            ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X * 0.45f));
            if (ImGui.InputText("Skeleton object", ref skeletonName, 128))
            {
                _config.SkeletonObjectName = skeletonName;
                SaveImportOptions();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "must be an existing Blender Armature");
        }
        else
        {
            ImGui.TextColored(new Vector4(.55f, .57f, .64f, 1), "Each import creates its own InstantEditArmature.");
        }

        var applyTexturesAndMaterials = _config.ApplyTexturesAndMaterials;
        if (ImGui.Checkbox("Apply textures and materials", ref applyTexturesAndMaterials))
        {
            _config.ApplyTexturesAndMaterials = applyTexturesAndMaterials;
            SaveImportOptions();
        }
        ImGui.TextColored(
            new Vector4(.55f, .57f, .64f, 1),
            "Creates display-only Blender materials. Quick Export still writes model data only.");

        ImGui.Indent();
        ImGui.BeginDisabled(!applyTexturesAndMaterials);
        var excludeBodyAndGeneralMaterials = _config.ExcludeBodyAndGeneralMaterials;
        if (ImGui.Checkbox("Exclude body and general materials", ref excludeBodyAndGeneralMaterials))
        {
            _config.ExcludeBodyAndGeneralMaterials = excludeBodyAndGeneralMaterials;
            SaveImportOptions();
        }
        ImGui.EndDisabled();
        ImGui.TextColored(
            new Vector4(.55f, .57f, .64f, 1),
            "Leaves body skin, body-piercing, and pube materials as colored placeholders.");
        ImGui.Unindent();
        ImGui.Separator();
    }

    private void SaveImportOptions()
    {
        _config.SkeletonObjectName = string.IsNullOrWhiteSpace(_config.SkeletonObjectName)
            ? "Skeleton"
            : _config.SkeletonObjectName.Trim();
        if (_config.SkeletonObjectName.Length > 128)
            _config.SkeletonObjectName = _config.SkeletonObjectName[..128];
        _saveConfig();
    }

    private static void Status(string name, bool good, string value)
    { ImGui.TextColored(good ? new Vector4(.3f, .78f, .5f, 1) : new Vector4(.9f, .45f, .32f, 1), "●"); ImGui.SameLine(0, 3); ImGui.TextColored(new Vector4(.7f, .72f, .78f, 1), $"{name}: {value}"); }

    private void DrawResources(IReadOnlyList<ActorView> actors, string? emptyMessage = null)
    {
        actors = actors.Where(ActorMatches).ToList();
        // Keep room for the status line below the viewport. The old -5px calculation
        // consumed the whole remaining window and clipped the refresh message.
        var viewportHeight = Math.Max(80, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() - 8);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.075f, .085f, .105f, 1));
        if (!ImGui.BeginChild("##resource-browser", new Vector2(0, viewportHeight), true)) { ImGui.EndChild(); ImGui.PopStyleColor(); return; }
        if (emptyMessage is null && _onScreen.IsRefreshing && actors.Count == 0) ImGui.TextColored(new Vector4(.65f, .68f, .75f, 1), "Refreshing resources…");
        else if (actors.Count == 0) ImGui.TextColored(new Vector4(.65f, .68f, .75f, 1), emptyMessage ?? "No snapshot loaded. Use Refresh character list to collect on-screen resources.");
        else foreach (var actor in actors) DrawActor(actor);
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private void DrawActor(ActorView actor)
    {
        if (actor.Entity is null)
        {
            DrawModResourceRows(actor);
            return;
        }

        var actorId = SafeId($"actor:{actor.Entity.Address:X}:{actor.Entity.ObjectIndex}");
        ImGui.PushID(actorId); DrawOpaqueRow();
        var filteredView = IsFilteredResourceView;
        var expanded = filteredView
            ? !_collapsedFiltered.Contains(actorId)
            : SearchActive || _expanded.Contains(actorId);
        if (ImGui.Button(expanded ? "▼##actor-toggle" : "▶##actor-toggle", new Vector2(22, ImGui.GetFrameHeight()))) ToggleExpanded(actorId, expanded, filteredView);
        ImGui.SameLine(0, 4); var header = Safe($"{actor.Category}{(string.IsNullOrWhiteSpace(actor.Name) ? string.Empty : $"  ·  {actor.Name}")}", "Player");
        var actorLabelWidth = Math.Max(1, ImGui.GetContentRegionAvail().X);
        if (ImGui.Selectable($"{header}##actor-label", false, ImGuiSelectableFlags.None, new Vector2(actorLabelWidth, ImGui.GetFrameHeight()))) ToggleExpanded(actorId, expanded, filteredView);
        if (expanded)
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter |
                        ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
            if (ImGui.BeginTable("##resource-table", 3, flags))
            {
                ImGui.TableSetupColumn("Slot / Item", ImGuiTableColumnFlags.WidthStretch, .36f);
                ImGui.TableSetupColumn("Mod / Resource Path", ImGuiTableColumnFlags.WidthStretch, .64f);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 58);
                ImGui.TableHeadersRow();
                DrawSection(actor, ResourceSection.CharacterFeatures, "Character features", actorId + ":features");
                DrawSection(actor, ResourceSection.Gear, "Gear", actorId + ":gear");
                DrawSection(actor, ResourceSection.Other, "Other", actorId + ":other");
                ImGui.EndTable();
            }
        }
        ImGui.PopID();
    }

    private void DrawModResourceRows(ActorView actor)
    {
        var resources = actor.Roots
            .Where(HasResourceTypeMatch)
            .OrderBy(resource => resource.GamePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuter |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##mod-resource-table", 4, flags))
            return;

        ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.WidthStretch, .36f);
        ImGui.TableSetupColumn("Mod / Resource Path", ImGuiTableColumnFlags.WidthStretch, .42f);
        ImGui.TableSetupColumn("Mod Options", ImGuiTableColumnFlags.WidthStretch, .22f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableHeadersRow();

        if (IsFilteredResourceView)
        {
            var row = 0;
            foreach (var root in resources)
                foreach (var resource in Flatten(root).Where(MatchesSelectedResourceType))
                    DrawFlatNode(actor, root, resource, $"mod:flat:{row++}", true);
        }
        else
        {
            for (var i = 0; i < resources.Count; i++)
                DrawNode(actor, resources[i], $"mod:{i}", 0, false, false, true);
        }

        ImGui.EndTable();
    }

    private void DrawSection(ActorView actor, ResourceSection section, string label, string key)
    {
        var sectionValue = section.ToString();
        var searchActive = actor.Entity is not null && SearchActive;
        var filterBySearch = searchActive && !ActorIdentityMatches(actor);
        var nodes = actor.Roots.Where(x => string.Equals(Safe(x.Section), sectionValue, StringComparison.OrdinalIgnoreCase) && HasResourceTypeMatch(x) && (!filterBySearch || Matches(x)));
        nodes = nodes.OrderBy(x => x.Order);
        var ordered = nodes.ToList();
        if (ordered.Count == 0) return;
        ImGui.PushID(SafeId(key));
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        var filteredView = IsFilteredResourceView;
        var expanded = filteredView
            ? !_collapsedFiltered.Contains(key)
            : SearchActive || _expanded.Contains(key);
        if (ImGui.Button(expanded ? "▼##section-toggle" : "▶##section-toggle", new Vector2(22, ImGui.GetFrameHeight()))) ToggleExpanded(key, expanded, filteredView);
        ImGui.SameLine(0, 4);
        if (ImGui.Selectable($"{label}##section-label", false, ImGuiSelectableFlags.SpanAllColumns, new Vector2(0, ImGui.GetFrameHeight()))) ToggleExpanded(key, expanded, filteredView);
        if (expanded)
        {
            if (filteredView)
                DrawFlatSection(actor, ordered, key, filterBySearch);
            else
                for (var i = 0; i < ordered.Count; i++) DrawNode(actor, ordered[i], $"{key}:{i}", 3, filterBySearch, searchActive);
        }
        ImGui.PopID();
    }

    private void DrawFlatSection(ActorView actor, IReadOnlyList<ResourceView> roots, string key, bool filterBySearch)
    {
        var row = 0;
        foreach (var root in roots)
        {
            foreach (var resource in Flatten(root).Where(x => MatchesSelectedResourceType(x) && (!filterBySearch || Matches(x))))
                DrawFlatNode(actor, root, resource, $"{key}:flat:{row++}");
        }
    }

    private void DrawFlatNode(ActorView actor, ResourceView item, ResourceView resource, string scope, bool showOptionMapping = false)
    {
        ImGui.PushID(SafeId(scope));
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 18);
        DrawSlotIcon(Safe(item.Slot, KindLabel(item.Type)), item.Icon, item.Section);
        ImGui.SameLine(0, 6);
        var itemName = Safe(DisplayName(item.Name, item.ActualPath), "Unnamed resource");
        ImGui.TextUnformatted(itemName);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Safe(item.Slot, item.Type));

        ImGui.TableSetColumnIndex(1);
        DrawResolvedPath(resource, Safe(resource.SourceLabel), Safe(resource.ActualPath), Safe(resource.GamePath));

        if (showOptionMapping)
        {
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(Safe(resource.OptionMapping, "Unmapped"));
        }

        ImGui.TableSetColumnIndex(showOptionMapping ? 3 : 2);
        if (IsModel(resource) && IsSafeModel(resource))
        {
            if (ImGui.SmallButton("Edit##flat-node-action")) TryEditNode(resource, actor);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Edit this model in Blender");
        }
        ImGui.PopID();
    }

    private void DrawNode(ActorView actor, ResourceView node, string scope, int depth, bool filterBySearch, bool autoExpandSearch, bool showOptionMapping = false)
    {
        var type = Safe(node.Type, "Resource");
        var name = Safe(node.Name, "Unnamed resource");
        var gamePath = Safe(node.GamePath);
        var actualPath = Safe(node.ActualPath);
        var source = Safe(node.SourceLabel, "Source unavailable");
        var key = SafeId($"{scope}:{type}:{name}:{gamePath}:{actualPath}");
        ImGui.PushID(key);
        var presentation = Safe(node.Slot, KindLabel(type));
        var children = node.Children;
        var hasChildren = children.Count > 0;
        var model = IsModel(node);
        var expanded = autoExpandSearch || _expanded.Contains(key);
        var arrow = hasChildren ? (expanded ? "▼" : "▶") : "  ";

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        // ImGui's persistent Indent state is reset while changing table rows/cells.
        // Offset this cell's cursor directly so every tree level moves right.
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, depth - 2) * 18);
        if (ImGui.Button(Safe($"{arrow}##expand:{key}", "  ##expand"), new Vector2(22, ImGui.GetFrameHeight())))
            if (hasChildren) { if (expanded) _expanded.Remove(key); else _expanded.Add(key); }
        if (ImGui.IsItemHovered() && hasChildren) ImGui.SetTooltip(expanded ? "Collapse" : "Expand");
        ImGui.SameLine(0, 4);
        DrawSlotIcon(presentation, node.Icon, node.Section);
        ImGui.SameLine(0, 6);
        var itemName = Safe(DisplayName(name, actualPath), "Unnamed resource");
        var hovered = ImGui.Selectable($"{itemName}##label:{key}", false, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight()));
        if (hovered && hasChildren) { if (expanded) _expanded.Remove(key); else _expanded.Add(key); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{presentation}\nGame path: {(gamePath.Length == 0 ? "(none)" : gamePath)}");
        ImGui.TableSetColumnIndex(1);
        DrawResolvedPath(node, source, actualPath, gamePath);

        if (showOptionMapping)
        {
            ImGui.TableSetColumnIndex(2);
            ImGui.TextUnformatted(Safe(node.OptionMapping, "Unmapped"));
        }

        ImGui.TableSetColumnIndex(showOptionMapping ? 3 : 2);
        if (model && IsSafeModel(node))
        {
            if (ImGui.SmallButton("Edit##node-action")) TryEditNode(node, actor);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Edit this model in Blender");
        }
        if (expanded && hasChildren)
        {
            for (var i = 0; i < children.Count; i++)
                if (!filterBySearch || Matches(children[i]))
                    DrawNode(actor, children[i], $"{scope}:{i}", depth + 1, filterBySearch, autoExpandSearch, showOptionMapping);
        }
        ImGui.PopID();
    }

    private void DrawResolvedPath(ResourceView node, string source, string actualPath, string gamePath)
    {
        if (!string.IsNullOrWhiteSpace(node.SourceModName))
        {
            ImGui.TextColored(new Vector4(.3f, .9f, .35f, 1), $"[{node.SourceModName}]");
            ImGui.SameLine(0, 5);
        }
        else if (!string.IsNullOrWhiteSpace(source))
        {
            ImGui.TextColored(new Vector4(.55f, .57f, .63f, 1), $"[{source}]");
            ImGui.SameLine(0, 5);
        }

        var displayPath = Safe(node.SourceRelativePath, Safe(gamePath, actualPath));
        ImGui.TextUnformatted(displayPath);
        ShowPathTooltip(ImGui.IsItemHovered(), actualPath);
    }

    private void DrawSlotIcon(string slot, string resourceIcon, string section)
    {
        IDalamudTextureWrap? icon;
        var key = string.Equals(section, ResourceSection.CharacterFeatures.ToString(), StringComparison.OrdinalIgnoreCase)
            ? "Unknown"
            : NormalizeSlotIcon(slot, resourceIcon);
        lock (_stateLock)
            _slotIcons.TryGetValue(key, out icon);

        if (icon is null)
        {
            ImGui.Dummy(new Vector2(ImGui.GetFrameHeight()));
            return;
        }

        var size = new Vector2(ImGui.GetFrameHeight());
        ImGui.Image(icon.Handle, size);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(slot);
    }

    private void DrawResourceTypeFilters(IReadOnlyList<ActorView> actors)
    {
        var groups = new[] { "Everything", "Models" };
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(.58f, .61f, .69f, 1), "Filter"); ImGui.SameLine(0, 8);
        foreach (var group in groups)
        {
            var filter = group == "Everything" ? string.Empty : group;
            var count = actors.SelectMany(x => x.Roots).SelectMany(Flatten)
                .Count(node => MatchesResourceType(node, filter));
            ImGui.PushID($"resource-type-filter:{group}");
            var selected = _resourceTypeFilter == filter;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.45f, .34f, .16f, 1));
            if (ImGui.SmallButton($"{group}  {count}")) _resourceTypeFilter = filter;
            if (selected) ImGui.PopStyleColor();
            if (group != groups[^1]) ImGui.SameLine(0, 6);
            ImGui.PopID();
        }
        ImGui.NewLine();
    }

    private IReadOnlyList<PenumbraMod> ReadMods()
    {
        if (DateTime.UtcNow - _lastModListRefresh > TimeSpan.FromSeconds(2))
        {
            _mods = _penumbra.GetMods();
            _lastModListRefresh = DateTime.UtcNow;
            if (_selectedModDirectory is not null && !_mods.Any(mod =>
                    string.Equals(mod.Directory, _selectedModDirectory, StringComparison.OrdinalIgnoreCase)))
            {
                CancelModLoad();
                _selectedModDirectory = null;
                _loadedModDirectory = null;
                _loadedModView = null;
            }
        }

        return _mods;
    }

    private bool ModMatches(PenumbraMod mod)
        => string.IsNullOrWhiteSpace(_modFilter)
            || mod.Name.Contains(_modFilter, StringComparison.OrdinalIgnoreCase)
            || mod.Directory.Contains(_modFilter, StringComparison.OrdinalIgnoreCase);

    private ActorView? GetModView(PenumbraMod mod)
    {
        lock (_stateLock)
        {
            if (string.Equals(_loadedModDirectory, mod.Directory, StringComparison.OrdinalIgnoreCase))
                return _loadedModView;
        }

        StartModLoad(mod);
        return null;
    }

    private void StartModLoad(PenumbraMod mod)
    {
        CancelModLoad();
        var cts = new CancellationTokenSource();
        var importObjectIndex = _onScreen.Items.FirstOrDefault()?.ObjectIndex ?? 0;
        lock (_stateLock)
        {
            _modLoadCts = cts;
            _loadedModDirectory = mod.Directory;
            _loadedModView = null;
            _modLoading = true;
            _modLoadFailed = false;
        }

        _ = LoadModViewAsync(mod, importObjectIndex, cts);
    }

    private async Task LoadModViewAsync(PenumbraMod mod, int importObjectIndex, CancellationTokenSource owner)
    {
        PenumbraModSnapshot? snapshot = null;
        try
        {
            snapshot = await _penumbra.GetModResourcesAsync(mod.Directory, owner.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Debug($"Could not load Penumbra mod resources: {e.Message}");
        }

        ActorView? view = null;
        if (snapshot is not null)
            view = BuildModView(snapshot, importObjectIndex);

        lock (_stateLock)
        {
            if (!ReferenceEquals(_modLoadCts, owner))
                return;
            _loadedModView = view;
            _modLoading = false;
            _modLoadFailed = snapshot is null && !owner.IsCancellationRequested;
        }
    }

    private static ActorView BuildModView(PenumbraModSnapshot snapshot, int importObjectIndex)
    {
        var roots = snapshot.Resources
            .Select(resource => new ResourceView(
                ResourceType(resource.GamePath),
                string.Empty,
                Path.GetFileName(resource.GamePath),
                resource.GamePath,
                resource.ActualPath,
                $"Loaded from: {snapshot.Name}",
                snapshot.Name,
                snapshot.Directory,
                snapshot.RootPath,
                resource.RelativePath,
                ResourceSection.Other.ToString(),
                ResourceType(resource.GamePath),
                int.MaxValue,
                resource.OptionMapping,
                new List<ResourceView>()))
            .ToList();

        return new ActorView(
            null,
            "Mod",
            snapshot.Name,
            roots,
            importObjectIndex);
    }

    private void CancelModLoad()
    {
        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            cts = _modLoadCts;
            _modLoadCts = null;
            _modLoading = false;
        }
        cts?.Cancel();
        cts?.Dispose();
    }

    private static string ResourceType(string gamePath)
        => Path.GetExtension(gamePath).ToLowerInvariant() switch
        {
            ".mdl" => "Model",
            ".tex" or ".atex" => "Texture",
            ".mtrl" => "Material",
            _ => "Resource",
        };

    private static IEnumerable<ResourceView> Flatten(ResourceView root)
    { yield return root; foreach (var child in root.Children) foreach (var item in Flatten(child)) yield return item; }

    private List<ActorView> ReadActors()
    {
        var result = new List<ActorView>();
        foreach (var entity in _onScreen.Items)
        {
            var parsed = entity.ResourceRoots.Select(ReadNode).ToList();
            result.Add(new ActorView(entity, entity.PresentationCategory.ToString(), Safe(entity.Name), parsed, entity.ObjectIndex));
        }
        return result;
    }

    private bool SearchActive => !string.IsNullOrWhiteSpace(_filter);

    private bool ActorIdentityMatches(ActorView actor)
        => actor.Category.Contains(_filter, StringComparison.OrdinalIgnoreCase) || actor.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private bool ActorMatches(ActorView actor)
        => actor.Roots.Any(HasResourceTypeMatch)
        && (actor.Entity is null || !SearchActive || ActorIdentityMatches(actor) || actor.Roots.Any(Matches));

    private bool IsFilteredResourceView => !string.IsNullOrWhiteSpace(_resourceTypeFilter);

    private bool HasResourceTypeMatch(ResourceView node)
        => MatchesSelectedResourceType(node) || node.Children.Any(HasResourceTypeMatch);

    private bool MatchesSelectedResourceType(ResourceView node)
        => MatchesResourceType(node, _resourceTypeFilter);

    private static bool MatchesResourceType(ResourceView node, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        var type = Safe(node.Type).ToLowerInvariant();
        var gamePath = Safe(node.GamePath).ToLowerInvariant();
        var actualPath = Safe(node.ActualPath).ToLowerInvariant();
        return filter switch
        {
            "Models" => type.Contains("model") || gamePath.EndsWith(".mdl") || actualPath.EndsWith(".mdl"),
            "Textures" => type.Contains("texture") || gamePath.EndsWith(".tex") || gamePath.EndsWith(".atex") || actualPath.EndsWith(".tex") || actualPath.EndsWith(".atex"),
            "Materials" => type.Contains("material") || gamePath.EndsWith(".mtrl") || actualPath.EndsWith(".mtrl"),
            _ => true,
        };
    }

    private void ToggleExpanded(string key, bool expanded, bool defaultExpanded)
    {
        var set = defaultExpanded ? _collapsedFiltered : _expanded;
        if (expanded)
        {
            if (defaultExpanded) set.Add(key); else set.Remove(key);
        }
        else
        {
            if (defaultExpanded) set.Remove(key); else set.Add(key);
        }
    }

    private static ResourceView ReadNode(ResourceNode node)
        => new(
            node.Type,
            node.Icon,
            node.Name,
            node.GamePath,
            node.ActualPath,
            node.SourceLabel,
            node.SourceModName ?? string.Empty,
            node.SourceModDirectory ?? string.Empty,
            node.SourceModRootPath ?? string.Empty,
            node.SourceRelativePath ?? string.Empty,
            node.ResourceSection.ToString(),
            node.SlotLabel,
            node.SortOrder,
            string.Empty,
            node.Children.Select(ReadNode).ToList());

    private static string KindLabel(string type) => string.IsNullOrWhiteSpace(type) ? "Resource" : type;
    private static string DisplayName(string name, string actualPath)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return Safe(Path.GetFileName(actualPath), "Unnamed resource");
    }
    private static bool IsModel(ResourceView node) => node.Type.Contains("model", StringComparison.OrdinalIgnoreCase) || node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);
    private static bool IsSafeModel(ResourceView node)
        => IsModel(node) && node.GamePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) &&
           node.ActualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) &&
           Path.IsPathRooted(node.ActualPath) && !string.IsNullOrWhiteSpace(node.SourceModDirectory);
    private bool Matches(ResourceView node)
    {
        var filter = Safe(_filter);
        var children = node.Children;
        return string.IsNullOrWhiteSpace(filter)
            || Safe(node.Name).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.Type).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.SourceLabel).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.SourceModName).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.SourceRelativePath).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.GamePath).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Safe(node.ActualPath).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || children.Any(Matches);
    }
    private bool LoadSlotIcons(IUiBuilder uiBuilder, ITextureProvider textureProvider)
    {
        using var armoury = uiBuilder.LoadUld("ui/uld/ArmouryBoard.uld");
        if (!armoury.Valid)
            return false;

        var icons = new Dictionary<string, IDalamudTextureWrap>(StringComparer.OrdinalIgnoreCase);
        try
        {
            void Add(string slot, int part)
            {
                var texture = armoury.LoadTexturePart("ui/uld/ArmouryBoard_hr1.tex", part);
                if (texture is not null)
                    icons.Add(slot, texture);
            }

            Add("Mainhand", 0);
            Add("Head", 1);
            Add("Body", 2);
            Add("Hands", 3);
            Add("Legs", 5);
            Add("Feet", 6);
            Add("Offhand", 7);
            Add("Ears", 8);
            Add("Neck", 9);
            Add("Wrists", 10);
            Add("Finger", 11);

            var unknown = LoadUnknownSlotIcon(textureProvider);
            if (unknown is not null)
                icons.Add("Unknown", unknown);

            lock (_stateLock)
                _slotIcons = icons;
            return true;
        }
        catch (Exception e)
        {
            foreach (var icon in icons.Values)
                icon.Dispose();
            _log.Debug($"Could not load armoury slot icons: {e.Message}");
            return false;
        }
    }

    private IDalamudTextureWrap? LoadUnknownSlotIcon(ITextureProvider textureProvider)
    {
        var texture = _data.GetFile<Lumina.Data.Files.TexFile>("ui/uld/levelup2_hr1.tex");
        if (texture is null)
            return null;

        // This is the same square crop Penumbra uses for its unknown-slot '?' icon.
        var source = texture.GetRgbaImageData();
        var size = texture.Header.Height;
        var bytes = new byte[size * size * 4];
        var horizontalOffset = 2 * (texture.Header.Height - texture.Header.Width);
        for (var y = 0; y < size; ++y)
            source.AsSpan(4 * y * texture.Header.Width, 4 * texture.Header.Width)
                .CopyTo(bytes.AsSpan(4 * y * size + horizontalOffset));

        return textureProvider.CreateFromRaw(RawImageSpecification.Rgba32(size, size), bytes, "InstantEdit.UnknownSlotIcon");
    }

    private static string NormalizeSlotIcon(string slot, string resourceIcon)
    {
        var value = $"{slot} {resourceIcon}".ToLowerInvariant();
        if (value.Contains("mainhand") || value.Contains("weapon")) return "Mainhand";
        if (value.Contains("offhand")) return "Offhand";
        if (value.Contains("head")) return "Head";
        if (value.Contains("body")) return "Body";
        if (value.Contains("hand")) return "Hands";
        if (value.Contains("leg")) return "Legs";
        if (value.Contains("feet") || value.Contains("foot")) return "Feet";
        if (value.Contains("earring") || value.Contains("ears")) return "Ears";
        if (value.Contains("neck")) return "Neck";
        if (value.Contains("bracelet") || value.Contains("wrist")) return "Wrists";
        if (value.Contains("ring") || value.Contains("finger")) return "Finger";
        return string.Empty;
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

    private void TryEditNode(ResourceView node, ActorView actor)
    {
        if (!IsModel(node) || !IsSafeModel(node))
        {
            SetStatus("Only safe .mdl resources can be imported.", false);
            return;
        }

        var model = new MdlFile { GamePath = node.GamePath, LocalPath = node.ActualPath };
        EditModel(actor, model, node);
    }

    private void RequestRefresh() { try { SetStatus(string.Empty, true); _onScreen.RequestRefresh(); } catch (Exception e) { _log.Debug(e.Message); SetStatus("Refresh unavailable. Is Penumbra running?", false); } }
    private void EditModel(ActorView actor, MdlFile model, ResourceView source)
    {
        if (Interlocked.CompareExchange(ref _editing, 1, 0) != 0)
        {
            SetStatus("Another model is already being sent.", false);
            return;
        }

        var importOptions = CurrentImportOptions();
        var cancellationToken = _lifetimeCts.Token;
        _ = Task.Run(() => EditModelAsync(
            actor,
            model,
            source,
            _config.BlenderPort,
            _config.ListenPort,
            importOptions,
            cancellationToken));
    }

    private BlenderImportOptions CurrentImportOptions()
        => _config.UseExistingSkeleton
            ? BlenderImportOptions.Existing(
                _config.SkeletonObjectName,
                _config.ApplyTexturesAndMaterials,
                _config.ExcludeBodyAndGeneralMaterials)
            : BlenderImportOptions.GeneratedWithPreview(
                _config.ApplyTexturesAndMaterials,
                _config.ExcludeBodyAndGeneralMaterials);

    private async Task EditModelAsync(
        ActorView actor,
        MdlFile model,
        ResourceView source,
        int blenderPort,
        int listenPort,
        BlenderImportOptions importOptions,
        CancellationToken cancellationToken)
    {
        string? handoffDirectory = null;
        var handoffCached = false;
        try
        {
            if (!await CheckBlenderAsync(blenderPort, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Blender is offline. Start Blender and enable the XIV Instant Edit add-on before editing.");
            if (importOptions.ArmatureMode == BlenderImportOptions.ExistingMode &&
                !await _blender.SupportsImportOptionsAsync(blenderPort, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The XIV Instant Edit add-on is too old for custom import options. Update the add-on and restart Blender.");
            if (importOptions.ApplyTexturesAndMaterials &&
                !await _blender.SupportsMaterialPreviewAsync(blenderPort, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The XIV Instant Edit add-on is too old for texture and material previews. Update the add-on and restart Blender.");

            var bytes = model.IsFilePath
                ? await File.ReadAllBytesAsync(model.LocalPath, cancellationToken).ConfigureAwait(false)
                : (await _data.GetFileAsync<FileResource>(model.LocalPath, cancellationToken).ConfigureAwait(false))?.Data
                    ?? throw new InvalidOperationException($"Game file not found: {model.LocalPath}");
            CleanupStaleHandoffs();
            var handoffRoot = Path.Combine(Path.GetTempPath(), "InstantEdit", "handoff");
            Directory.CreateDirectory(handoffRoot);
            var dir = Path.Combine(handoffRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            handoffDirectory = dir;
            var file = Path.Combine(dir, $"{Sanitize(actor.Name)}-{actor.ImportObjectIndex}-{model.FileName}");
            await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
            var resources = await ResolvePreviewResourcesAsync(actor).ConfigureAwait(false);
            var materialBundle = await _materialPreviews.BuildImportBundleAsync(
                bytes,
                model.GamePath,
                resources,
                Path.Combine(dir, "preview"),
                importOptions.ApplyTexturesAndMaterials,
                importOptions.ExcludeBodyAndGeneralMaterials,
                cancellationToken).ConfigureAwait(false);
            var preview = materialBundle.Preview;
            var resourceManifest = materialBundle.ResourceManifest;
            handoffCached = await _blender.SendSourceImportAsync(
                blenderPort,
                file,
                model.GamePath,
                actor.ImportObjectIndex,
                $"{actor.Name} {model.FileName}",
                listenPort,
                source.ActualPath,
                source.SourceModDirectory,
                source.SourceModName,
                importOptions: importOptions,
                previewManifestPath: preview?.ManifestPath,
                sourceModRootPath: source.SourceModRootPath,
                targetRelativePath: source.SourceRelativePath,
                resourceManifest: resourceManifest,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var warning = preview is { Warnings.Count: > 0 } ? $" Preview warning: {preview.WarningSummary}" : string.Empty;
            var mashupWarning = resourceManifest is null
                ? " Mashup warning: exact material/texture sources could not be captured; re-import after resolving the missing resources."
                : string.Empty;
            SetStatus($"Sent {model.FileName} to Blender.{warning}{mashupWarning}", true);
            _chat.Print($"XIV Instant Edit: {model.FileName} sent to Blender.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Plugin/window shutdown cancels outstanding handoffs without touching UI services.
        }
        catch (Exception e)
        {
            _log.Error(e, "Failed to send model to Blender.");
            SetStatus($"Failed: {e.Message}", false);
            _chat.PrintError($"XIV Instant Edit: could not send model to Blender: {e.Message}");
        }
        finally
        {
            if (handoffCached && handoffDirectory is not null)
                TryDeleteOwnedHandoff(handoffDirectory);
            Volatile.Write(ref _editing, 0);
        }
    }

    private static void CleanupStaleHandoffs()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InstantEdit", "handoff"));
        if (!Directory.Exists(root))
            return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (name.Length != 32 || !Guid.TryParseExact(name, "N", out _))
                continue;
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    TryDeleteOwnedHandoff(directory);
            }
            catch
            {
                // A locked or concurrently consumed handoff is retried later.
            }
        }
    }

    private static void TryDeleteOwnedHandoff(string directory)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "InstantEdit", "handoff"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(directory);
            var name = Path.GetFileName(full);
            if (name.Length != 32 || !Guid.TryParseExact(name, "N", out _) ||
                !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return;
            Directory.Delete(full, true);
        }
        catch
        {
            // Cleanup is best-effort and never changes the import result.
        }
    }

    private async Task<IReadOnlyCollection<MaterialResourceCandidate>> ResolvePreviewResourcesAsync(ActorView actor)
    {
        if (actor.Entity is not null && actor.ImportObjectIndex is >= 0 and <= ushort.MaxValue)
        {
            var resolved = await _penumbra.GetResourcePathsAsync((ushort)actor.ImportObjectIndex).ConfigureAwait(false);
            if (resolved is not null)
            {
                return resolved.SelectMany(pair => pair.Value.Select(gamePath =>
                    new MaterialResourceCandidate(gamePath, pair.Key))).ToArray();
            }
        }

        return actor.Roots
            .SelectMany(Flatten)
            .Where(resource => resource.GamePath.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) ||
                               resource.GamePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            .Select(resource => new MaterialResourceCandidate(resource.GamePath, resource.ActualPath))
            .ToArray();
    }

    private async Task<bool> CheckBlenderAsync(int port, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        return await _blender.IsReachableAsync(port, timeout.Token).ConfigureAwait(false);
    }

    private void StartBlenderCheckIfNeeded()
    {
        lock (_stateLock)
        {
            if (_blenderChecking || (DateTime.UtcNow - _lastBlenderCheck).TotalSeconds <= 5)
                return;
            _blenderChecking = true;
        }

        _ = Task.Run(async () =>
        {
            var ok = false;
            try
            {
                ok = await CheckBlenderAsync(_config.BlenderPort).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.Debug($"Blender status check failed: {e.Message}");
            }
            finally
            {
                lock (_stateLock)
                {
                    _blenderOk = ok;
                    _blenderChecking = false;
                    _lastBlenderCheck = DateTime.UtcNow;
                }
            }
        });
    }
    private void DrawFeedback()
    {
        string text;
        bool ok;
        lock (_stateLock)
        {
            text = _status;
            ok = _statusOk;
        }

        if (text.Length == 0)
            return;

        var accent = ok
            ? new Vector4(.35f, .85f, .55f, 1)
            : new Vector4(1f, .35f, .22f, 1);
        var background = ok
            ? new Vector4(.08f, .18f, .12f, 1)
            : new Vector4(.24f, .09f, .065f, 1);

        ImGui.PushStyleColor(ImGuiCol.ChildBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, accent);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1);
        if (ImGui.BeginChild("##instant-edit-feedback", new Vector2(0, ImGui.GetFrameHeightWithSpacing() * 2), true))
        {
            ImGui.TextColored(accent, ok ? "✓" : "⚠");
            ImGui.SameLine(0, 6);
            ImGui.PushTextWrapPos();
            ImGui.TextColored(new Vector4(.92f, .93f, .96f, 1), text);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }
    private void SetStatus(string text, bool ok) { lock (_stateLock) { _status = text; _statusOk = ok; } }
    private static string Sanitize(string name) { var invalid = Path.GetInvalidFileNameChars(); var value = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(); return value.Length == 0 ? "Object" : value; }

    private static string Safe(string? value, string fallback = "")
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string SafeId(string? value)
    {
        var id = Safe(value, "resource");
        return id.Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    private sealed record ActorView(
        OnScreenObject? Entity,
        string Category,
        string Name,
        List<ResourceView> Roots,
        int ImportObjectIndex);
    private sealed record ResourceView(
        string Type,
        string Icon,
        string Name,
        string GamePath,
        string ActualPath,
        string SourceLabel,
        string SourceModName,
        string SourceModDirectory,
        string SourceModRootPath,
        string SourceRelativePath,
        string Section,
        string Slot,
        int Order,
        string OptionMapping,
        List<ResourceView> Children);
}
