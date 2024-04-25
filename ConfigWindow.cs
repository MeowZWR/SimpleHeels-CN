using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;
using Newtonsoft.Json;
using SimpleHeels.Files;
using World = Lumina.Excel.GeneratedSheets.World;

namespace SimpleHeels;

public class ConfigWindow : Window {
    private static FileDialogManager? _fileDialogManager;
    private readonly PluginConfig config;
    private readonly Stopwatch hiddenStopwatch = Stopwatch.StartNew();
    
    private readonly Plugin plugin;

    private readonly Lazy<Dictionary<(ushort, ModelSlot), ShoeModel>> shoeModelList = new(() => {
        var dict = new Dictionary<(ushort, ModelSlot), ShoeModel> { [(0, ModelSlot.Feet)] = new() { Id = 0, Name = "Smallclothes (Barefoot)" } };

        foreach (var item in PluginService.Data.GetExcelSheet<Item>()!.Where(i => i.EquipSlotCategory?.Value?.Feet != 0)) {
            if (item.ItemUICategory.Row is not (35 or 36 or 38)) continue;

            var modelBytes = BitConverter.GetBytes(item.ModelMain);
            var modelId = BitConverter.ToUInt16(modelBytes, 0);

            var slot = item.ItemUICategory.Row switch {
                35 => ModelSlot.Top,
                36 => ModelSlot.Legs,
                _ => ModelSlot.Feet
            };

            if (!dict.ContainsKey((modelId, slot))) dict.Add((modelId, slot), new ShoeModel { Id = modelId, Slot = slot });

            dict[(modelId, slot)].Items.Add(item);
            dict[(modelId, slot)].Name = null;
        }

        return dict;
    });

    private int beginDrag = -1;
    private float checkboxSize = 36;
    private DalamudLinkPayload clickAllowInCutscenePayload;

    private DalamudLinkPayload clickAllowInGposePayload;
    private int endDrag = -1;
    private Vector2 endDragPosition = new();

    private Vector2 firstCheckboxScreenPosition = new(0);

    private string footwearSearch = string.Empty;

    private string groupNameMatchingNewInput = string.Empty;
    private string groupNameMatchingWorldSearch = string.Empty;

    private Vector2 iconButtonSize = new(16);

    private float kofiButtonOffset = 0f;
    private MdlFile? loadedFile;
    private string loadedFilePath = string.Empty;
    private Exception? mdlEditorException;
    private float mdlEditorOffset = 0f;

    private string newName = string.Empty;
    private uint newWorld = 0;

    private CancellationTokenSource? notVisibleWarningCancellationTokenSource;
    private string? PenumbraModFolder;

    private string searchInput = string.Empty;

    private CharacterConfig? selectedCharacter;

    private GroupConfig? selectedGroup;
    private string selectedName = string.Empty;
    private uint selectedWorld;
    private bool useTextoolSafeAttribute = true;

    public ConfigWindow(string name, Plugin plugin, PluginConfig config) : base(name, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse) {
        this.config = config;
        this.plugin = plugin;

        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(800, 400), MaximumSize = new Vector2(float.MaxValue) };

        Size = new Vector2(1000, 500);
        SizeCondition = ImGuiCond.FirstUseEver;

        clickAllowInGposePayload = PluginService.PluginInterface.AddChatLinkHandler(1000, (_, _) => {
            config.ConfigInGpose = true;
            PluginService.PluginInterface.UiBuilder.DisableGposeUiHide = true;
            IsOpen = true;
        });

        clickAllowInCutscenePayload = PluginService.PluginInterface.AddChatLinkHandler(1001, (_, _) => {
            config.ConfigInCutscene = true;
            PluginService.PluginInterface.UiBuilder.DisableCutsceneUiHide = true;
            IsOpen = true;
        });
    }

    public override void OnOpen() {
        UpdatePenumbraModFolder();
    }

    private void UpdatePenumbraModFolder() {
        try {
            var getModDir = PluginService.PluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
            PenumbraModFolder = getModDir.InvokeFunc();
            PenumbraModFolder = PenumbraModFolder.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            PluginService.Log.Debug($"Penumbra文件：{PenumbraModFolder}");
        } catch {
            PenumbraModFolder = null;
        }
    }

    public unsafe void DrawCharacterList() {
        foreach (var (worldId, characters) in config.WorldCharacterDictionary.ToArray()) {
            var world = PluginService.Data.GetExcelSheet<World>()?.GetRow(worldId);
            if (world == null) continue;

            ImGui.TextDisabled($"{world.Name.RawString}");
            ImGuiExt.Separator();

            foreach (var (name, characterConfig) in characters.ToArray()) {
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGrey, characterConfig.Enabled == false)) {
                    if (ImGui.Selectable($"{name}##{world.Name.RawString}", selectedCharacter == characterConfig)) {
                        selectedCharacter = characterConfig;
                        selectedName = name;
                        selectedWorld = world.RowId;
                        newName = name;
                        newWorld = world.RowId;
                        selectedGroup = null;
                    }
                }

                if (ImGui.BeginPopupContextItem()) {
                    if (ImGui.Selectable($"从设置中移除“{name} @ {world.Name.RawString}”")) {
                        characters.Remove(name);
                        if (selectedCharacter == characterConfig) selectedCharacter = null;
                        if (characters.Count == 0) {
                            config.WorldCharacterDictionary.Remove(worldId);
                        }
                    }

                    ImGui.EndPopup();
                }
            }

            ImGuiHelpers.ScaledDummy(10);
        }

        if (Plugin.IsDebug && Plugin.IpcAssignedData.Count > 0) {
            ImGui.TextDisabled("[DEBUG] IPC Assignments");
            ImGuiExt.Separator();

            foreach (var (objectId, ipcCharacterConfig) in Plugin.IpcAssignedData) {
                var ipcAssignedObject = Utils.GetGameObjectById(objectId);
                if (ipcAssignedObject == null) continue;
                if (!ipcAssignedObject->IsCharacter()) continue;
                var ipcAssignedCharacter = (Character*)ipcAssignedObject;
                if (ipcAssignedCharacter->HomeWorld == ushort.MaxValue) continue;

                var name = MemoryHelper.ReadSeString((nint)ipcAssignedObject->Name, 64).TextValue;
                var worldId = ipcAssignedCharacter->HomeWorld;

                var world = PluginService.Data.GetExcelSheet<World>()?.GetRow(worldId);
                if (world == null) continue;
                if (ImGui.Selectable($"{name}##{world.Name.RawString}##ipc", selectedName == name && selectedWorld == worldId)) {
                    selectedCharacter = ipcCharacterConfig;
                    selectedName = name;
                    selectedWorld = world.RowId;
                    newName = string.Empty;
                    newWorld = 0;
                    selectedGroup = null;
                }

                ImGui.SameLine();
                ImGui.TextDisabled(world.Name.ToDalamudString().TextValue);
            }

            ImGuiHelpers.ScaledDummy(10);
        }

        if (config.Groups.Count > 0) {
            ImGui.TextDisabled($"组分配");
            ImGuiExt.Separator();
            var arr = config.Groups.ToArray();

            for (var i = 0; i < arr.Length; i++) {
                var filterConfig = arr[i];
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGrey, filterConfig.Enabled == false)) {
                    if (ImGui.Selectable($"{filterConfig.Label}##filterConfig_{i}", selectedGroup == filterConfig)) {
                        selectedCharacter = null;
                        selectedName = string.Empty;
                        selectedWorld = 0;
                        newName = string.Empty;
                        newWorld = 0;
                        selectedGroup = filterConfig;
                        selectedGroup.Characters.RemoveAll(c => string.IsNullOrWhiteSpace(c.Name));
                    }
                }

                if (ImGui.BeginPopupContextItem()) {
                    if (config.Groups.Count > 1) {
                        if (i > 0) {
                            if (ImGui.Selectable($"上移")) {
                                config.Groups.Remove(filterConfig);
                                config.Groups.Insert(i - 1, filterConfig);
                            }
                        }

                        if (i < config.Groups.Count - 1) {
                            if (ImGui.Selectable($"下移")) {
                                config.Groups.Remove(filterConfig);
                                config.Groups.Insert(i + 1, filterConfig);
                            }
                        }

                        ImGuiExt.Separator();
                    }

                    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGui.GetIO().KeyShift ? ImGuiCol.Text : ImGuiCol.TextDisabled));
                    if (ImGui.Selectable($"删除组 '{filterConfig.Label}'") && ImGui.GetIO().KeyShift) {
                        if (selectedGroup == filterConfig) selectedGroup = null;
                        config.Groups.Remove(filterConfig);
                    }

                    ImGui.PopStyleColor();
                    if (!ImGui.GetIO().KeyShift && ImGui.IsItemHovered()) {
                        ImGui.SetTooltip("按住SHIFT键点击删除。");
                    }

                    ImGui.EndPopup();
                }
            }

            ImGuiHelpers.ScaledDummy(10);
        }
    }

    private void ShowDebugInfo() {
        if (Plugin.IsDebug && ImGui.TreeNode("DEBUG INFO")) {
            try {
                var activePlayer = PluginService.Objects.FirstOrDefault(t => t is PlayerCharacter playerCharacter && playerCharacter.Name.TextValue == selectedName && playerCharacter.HomeWorld.Id == selectedWorld);

                if (activePlayer is not PlayerCharacter pc) {
                    ImGui.TextDisabled("角色当前不存在于世界中。");
                    return;
                }

                ImGui.TextDisabled($"角色：{pc:X8}");
                if (ImGui.IsItemClicked()) ImGui.SetClipboardText($"{pc.Address:X}");

                unsafe {
                    var obj = (GameObject*)activePlayer.Address;
                    var character = (Character*)obj;
                    Util.ShowStruct(character);
                    var realPosition = obj->Position;
                    if (obj->DrawObject == null) {
                        ImGui.TextDisabled("角色当前没有被绘制。");
                        return;
                    }

                    var drawPosition = obj->DrawObject->Object.Position;

                    ImGui.Text($"Actual Y Position: {realPosition.Y}");
                    ImGui.Text($"Drawn Y Position: {drawPosition.Y}");
                    if (ImGui.IsItemClicked()) {
                        ImGui.SetClipboardText($"{(ulong)(&obj->DrawObject->Object.Position.Y):X}");
                    }

                    ImGui.Text($"Active Offset: {drawPosition.Y - realPosition.Y}");
                    // ImGui.Text($"Expected Offset: {plugin.GetOffset(obj)}");

                    ImGui.Text($"Height: {obj->GetHeight()}");
                    ImGui.Text($"Mode: {character->Mode}, {character->ModeParam}");

                    ImGui.Text($"Object Type: {obj->DrawObject->Object.GetObjectType()}");
                    if (obj->DrawObject->Object.GetObjectType() == ObjectType.CharacterBase) {
                        var characterBase = (CharacterBase*)obj->DrawObject;
                        ImGui.Text($"Model Type: {characterBase->GetModelType()}");
                        if (characterBase->GetModelType() == CharacterBase.ModelType.Human) {
                            var human = (Human*)obj->DrawObject;
                            ImGui.Text("Active Models:");
                            ImGui.Indent();
                            ImGui.Text("Top:");
                            ImGui.Indent();
                            ImGui.Text($"ID: {human->Top.Id}, {human->Top.Variant}");
                            ImGui.Text($"Name: {GetModelName(human->Top.Id, ModelSlot.Top, true) ?? "Does not replace feet"}");
                            ImGui.Text($"Path: {Plugin.GetModelPath(human, ModelSlot.Top)}");
                            ImGui.Unindent();
                            ImGui.Text("Legs:");
                            ImGui.Indent();
                            ImGui.Text($"ID: {human->Legs.Id}, {human->Legs.Variant}");
                            ImGui.Text($"Name: {GetModelName(human->Legs.Id, ModelSlot.Legs, true) ?? "Does not replace feet"}");
                            ImGui.Text($"Path: {Plugin.GetModelPath(human, ModelSlot.Legs)}");
                            ImGui.Unindent();
                            ImGui.Text("Feet:");
                            ImGui.Indent();
                            ImGui.Text($"ID: {human->Feet.Id}, {human->Feet.Variant}");
                            ImGui.Text($"Name: {GetModelName(human->Feet.Id, ModelSlot.Feet)}");
                            ImGui.Text($"Path: {Plugin.GetModelPath(human, ModelSlot.Feet)}");
                            ImGui.Unindent();

                            ImGui.Unindent();
                        } else {
                            ImGui.TextDisabled("玩家不是一个“人类”");
                        }
                    } else {
                        ImGui.TextDisabled("玩家不是一个“角色”");
                    }
                }
            } finally {
                ImGui.TreePop();
            }
        }
    }

    public override void Draw() {
        checkboxSize = ImGui.GetTextLineHeight() + ImGui.GetStyle().FramePadding.Y * 2;

        hiddenStopwatch.Restart();
        if (notVisibleWarningCancellationTokenSource != null) {
            notVisibleWarningCancellationTokenSource.Cancel();
            notVisibleWarningCancellationTokenSource = null;
        }

        if (!config.Enabled) {
            ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DalamudRed * new Vector4(1, 1, 1, 0.3f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGuiColors.DalamudRed * new Vector4(1, 1, 1, 0.3f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGuiColors.DalamudRed * new Vector4(1, 1, 1, 0.3f));
            ImGui.Button($"{plugin.Name} 当前已禁用。不会应用任何偏移值。", new Vector2(ImGui.GetContentRegionAvail().X, 32 * ImGuiHelpers.GlobalScale));
            ImGui.PopStyleColor(3);
        }

        ImGui.BeginGroup();
        {
            if (ImGui.BeginChild("character_select", ImGuiHelpers.ScaledVector2(120, 0) - iconButtonSize with { X = 0 }, true)) {
                DrawCharacterList();
            }

            ImGui.EndChild();

            var charaListPos = ImGui.GetItemRectSize().X;

            if (PluginService.ClientState.LocalPlayer != null) {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.User)) {
                    if (PluginService.ClientState.LocalPlayer != null) {
                        config.TryAddCharacter(PluginService.ClientState.LocalPlayer.Name.TextValue, PluginService.ClientState.LocalPlayer.HomeWorld.Id);
                    }
                }

                if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加你的当前角色");

                ImGui.SameLine();
                if (ImGuiComponents.IconButton(FontAwesomeIcon.DotCircle)) {
                    if (PluginService.Targets.Target is PlayerCharacter pc) {
                        config.TryAddCharacter(pc.Name.TextValue, pc.HomeWorld.Id);
                    }
                }

                if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加当前选中角色");
                ImGui.SameLine();
            }

            if (ImGuiComponents.IconButton(FontAwesomeIcon.PeopleGroup)) {
                if (new GroupConfig().Initialize() is GroupConfig newGroup) {
                    selectedCharacter = null;
                    selectedName = string.Empty;
                    selectedWorld = 0;
                    newName = string.Empty;
                    newWorld = 0;
                    selectedGroup = newGroup;
                    config.Groups.Add(newGroup);
                }
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("新建组分配");
            ImGui.SameLine();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog)) {
                selectedCharacter = null;
                selectedName = string.Empty;
                selectedWorld = 0;
                newName = string.Empty;
                newWorld = 0;
                selectedGroup = null;
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip("插件选项");
            iconButtonSize = ImGui.GetItemRectSize() + ImGui.GetStyle().ItemSpacing;

            if (!config.HideKofi) {
                ImGui.SameLine();
                if (kofiButtonOffset > 0) ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), charaListPos - kofiButtonOffset + ImGui.GetStyle().WindowPadding.X));
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Coffee, "支持", new Vector4(1, 0.35f, 0.35f, 1f), new Vector4(1, 0.35f, 0.35f, 0.9f), new Vector4(1, 0.35f, 0.35f, 75f))) {
                    Util.OpenLink("https://ko-fi.com/Caraxi");
                }

                if (ImGui.IsItemHovered()) {
                    ImGui.SetTooltip("到Ko-fi支持");
                }

                kofiButtonOffset = ImGui.GetItemRectSize().X;
            }
        }
        ImGui.EndGroup();

        ImGui.SameLine();
        if (ImGui.BeginChild("character_view", ImGuiHelpers.ScaledVector2(0), true)) {
            if (selectedCharacter != null) {
                ShowDebugInfo();
                DrawCharacterView(selectedCharacter);
            } else if (selectedGroup != null) {
                if (ImGui.Checkbox($"为组启用偏移", ref selectedGroup.Enabled)) {
                    Plugin.RequestUpdateAll();
                }

                if (selectedGroup is { Enabled: false }) {
                    ImGui.SameLine();
                    ImGui.TextColored(ImGuiColors.DalamudRed, "此配置已禁用。");
                }

                ImGui.InputText("组标签", ref selectedGroup.Label, 50);
                ImGuiExt.Separator();
                ImGui.Text("按性别将此组应用到角色：");
                ImGui.Indent();

                if (ImGui.Checkbox("男性模型", ref selectedGroup.MatchMasculine)) {
                    if (selectedGroup is { MatchFeminine: false, MatchMasculine: false }) {
                        selectedGroup.MatchFeminine = true;
                    }
                }

                if (ImGui.Checkbox("女性模型", ref selectedGroup.MatchFeminine)) {
                    if (selectedGroup is { MatchFeminine: false, MatchMasculine: false }) {
                        selectedGroup.MatchMasculine = true;
                    }
                }

                ImGui.Unindent();

                ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(selectedGroup.Clans.Count == 0 ? ImGui.GetColorU32(ImGuiCol.TextDisabled) : ImGui.GetColorU32(ImGuiCol.Text)), "按种族将此组应用到角色：");

                if (selectedGroup.Clans.Count == 0) {
                    ImGui.SameLine();
                    ImGuiComponents.HelpMarker($"此组将应用于所有角色{(selectedGroup.MatchFeminine && selectedGroup.MatchMasculine ? "" : selectedGroup.MatchFeminine ? "（女性模型）" : "（男性模型）")} 因为还没有选择种族。");
                }

                ImGui.Indent();
                if (ImGui.BeginTable("clanTable", 4)) {
                    foreach (var clan in PluginService.Data.GetExcelSheet<Tribe>()!) {
                        if (clan.RowId == 0) continue;

                        var isEnabled = selectedGroup.Clans.Count == 0 || selectedGroup.Clans.Contains(clan.RowId);

                        ImGui.TableNextColumn();

                        ImGui.PushStyleColor(ImGuiCol.CheckMark, ImGui.GetColorU32(selectedGroup.Clans.Count == 0 ? ImGuiCol.TextDisabled : ImGuiCol.Text));
                        if (ImGui.Checkbox($"{clan.Masculine.ToDalamudString().TextValue}", ref isEnabled)) {
                            if (selectedGroup.Clans.Contains(clan.RowId)) {
                                selectedGroup.Clans.Remove(clan.RowId);
                            } else {
                                selectedGroup.Clans.Add(clan.RowId);
                            }
                        }

                        ImGui.PopStyleColor();
                    }

                    ImGui.EndTable();
                }

                ImGui.Unindent();

                if (ImGui.CollapsingHeader("名称匹配")) {
                    var nameMatchCharacter = -1;
                    foreach (var c in selectedGroup.Characters.ToArray()) {
                        ImGui.PushID($"group_character_{++nameMatchCharacter}");

                        ImGui.SetNextItemWidth(ImGuiHelpers.GlobalScale * 140);
                        ImGui.InputText("##name", ref c.Name, 32);

                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(ImGuiHelpers.GlobalScale * 140);
                        if (ImGui.BeginCombo("##world", c.World == ushort.MaxValue ? "非玩家" : PluginService.Data.GetExcelSheet<World>()?.GetRow(c.World)?.Name.RawString ?? $"World#{c.World}", ImGuiComboFlags.HeightLargest)) {
                            var appearing = ImGui.IsWindowAppearing();

                            if (appearing) {
                                groupNameMatchingWorldSearch = string.Empty;
                                ImGui.SetKeyboardFocusHere();
                            }

                            ImGui.SetNextItemWidth(ImGuiHelpers.GlobalScale * 140);
                            ImGui.InputTextWithHint("##search", "搜索...", ref groupNameMatchingWorldSearch, 25);
                            var s = ImGui.GetItemRectSize();
                            ImGuiExt.Separator();

                            if (ImGui.BeginChild("worldScroll", new Vector2(s.X, ImGuiHelpers.GlobalScale * 250))) {
                                var lastDc = uint.MaxValue;

                                void World(string name, uint worldId, WorldDCGroupType? dc = null) {
                                    if (!string.IsNullOrWhiteSpace(groupNameMatchingWorldSearch)) {
                                        if (!name.Contains(groupNameMatchingWorldSearch, StringComparison.InvariantCultureIgnoreCase)) return;
                                    }

                                    if (dc != null) {
                                        if (lastDc != dc.RowId) {
                                            lastDc = dc.RowId;
                                            ImGui.TextDisabled($"{dc.Name.RawString}");
                                        }
                                    }

                                    if (ImGui.Selectable($"    {name}", c.World == worldId)) {
                                        c.World = worldId;
                                        ImGui.CloseCurrentPopup();
                                    }

                                    if (appearing && c.World == worldId) {
                                        ImGui.SetScrollHereY();
                                    }
                                }

                                World("非玩家", ushort.MaxValue);
                                foreach (var w in PluginService.Data.GetExcelSheet<World>()!.Where(w => w.IsPublic).OrderBy(w => w.DataCenter.Value?.Name.RawString).ThenBy(w => w.Name.RawString)) {
                                    World(w.Name.RawString, w.RowId, w.DataCenter.Value);
                                }
                            }

                            ImGui.EndChild();
                            ImGui.EndCombo();
                        }

                        ImGui.SameLine();
                        if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash)) {
                            selectedGroup.Characters.RemoveAt(nameMatchCharacter);
                        }

                        ImGui.PopID();
                    }

                    ImGui.PushID($"group_character_{++nameMatchCharacter}");
                    ImGui.SetNextItemWidth(ImGuiHelpers.GlobalScale * 140);
                    if (ImGui.InputText("##name", ref groupNameMatchingNewInput, 32)) {
                        var c = new GroupCharacter() { Name = groupNameMatchingNewInput };
                        selectedGroup.Characters.Add(c);
                        groupNameMatchingNewInput = string.Empty;
                    }
                    
                    if (PluginService.ClientState.LocalPlayer != null) {
                        ImGui.SameLine();
                        if (ImGuiComponents.IconButton(FontAwesomeIcon.User)) {
                            if (PluginService.ClientState.LocalPlayer != null) {
                                var c = new GroupCharacter { Name = PluginService.ClientState.LocalPlayer.Name.TextValue, World = PluginService.ClientState.LocalPlayer.HomeWorld.Id };
                                if (!selectedGroup.Characters.Any(ec => ec.Name == c.Name && ec.World == c.World)) {
                                    selectedGroup.Characters.Add(c);
                                }
                            }
                        }

                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加当前玩家");

                        ImGui.SameLine();
                        if (ImGuiComponents.IconButton(FontAwesomeIcon.DotCircle)) {
                            var target = PluginService.Targets.SoftTarget ?? PluginService.Targets.Target;
                            if (target != null) {
                                var c = new GroupCharacter { Name = target.Name.TextValue, World = target is PlayerCharacter pc ? pc.HomeWorld.Id : ushort.MaxValue };
                                if (!selectedGroup.Characters.Any(ec => ec.Name == c.Name && ec.World == c.World)) {
                                    selectedGroup.Characters.Add(c);
                                }
                            }
                        }

                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("添加目标玩家");
                        ImGui.SameLine();
                    }
                    
                    ImGui.PopID();
                }

                ImGuiExt.Separator();
                DrawCharacterView(selectedGroup);
            } else {
                var changelogVisible = Changelog.Show(config);

                ImGui.Text("SimpleHeels 选项");

                if (!changelogVisible) {
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize("changelogs").X - ImGui.GetStyle().FramePadding.X * 2);
                    if (ImGui.SmallButton("查看更新日志")) {
                        config.DismissedChangelog = 0f;
                    }
                }

                ImGuiExt.Separator();

                if (ImGui.Checkbox("启用", ref config.Enabled)) {
                    Plugin.RequestUpdateAll();
                }

                ImGui.SameLine();
                ImGuiComponents.HelpMarker("可以使用命令来切换：\n\t/heels toggle\n\t/heels enable\n\t/heels disable");
                ImGui.Checkbox("隐藏Ko-fi支持按钮", ref config.HideKofi);
                if (ImGui.Checkbox("使用模型指定的偏移值", ref config.UseModelOffsets)) {
                    Plugin.RequestUpdateAll();
                }

                ImGui.SameLine();
                ImGuiComponents.HelpMarker("允许模组作者为修改后的物品指定偏移值。\n单击获取更多信息。");
                if (ImGui.IsItemClicked()) {
                    Util.OpenLink("https://github.com/Caraxi/SimpleHeels/blob/master/modguide.md");
                }

                ImGui.Checkbox("将偏移应用于宠物", ref config.ApplyToMinions);
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("允许将组分配的偏移应用于宠物。\n这只有在使用另一个插件（如Glamourer）将宠物转换为人类后才生效。\n情感动作偏移和月海同步不会对宠物起作用。");

                ImGui.Checkbox("同步静态宠物位置", ref config.ApplyStaticMinionPositions);
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("当你正在通过月海同步器同步你的偏移值时，允许发送和接收静态宠物的位置。\n他人也须启用了此选项才能看到效果。\n仅适用于不会移动的宠物，比如软软垫子和浪人的篝火。");

                
                ImGui.Checkbox("情感动作使用精确定位", ref config.UsePrecisePositioning);
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("调整其他玩家的偏移，以更好地匹配他们在游戏中实际看到的位置。\n\n默认情况下，游戏在显示其他玩家所在位置时缺乏精确度，此选项可帮助对齐未绑定在椅子或床上的其他情感动作。");
                
                ImGui.Checkbox("创建新条目时优先显示模型路径", ref config.PreferModelPath);

                ImGui.Checkbox("显示加号/减号按钮来调整偏移值", ref config.ShowPlusMinusButtons);
                using (ImRaii.Disabled(!config.ShowPlusMinusButtons))
                using (ImRaii.PushIndent()) {
                    ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
                    ImGui.SliderFloat("加号/减号按钮调整值", ref config.PlusMinusDelta, 0.0001f, 0.01f, "%.4f", ImGuiSliderFlags.AlwaysClamp);
                }

                ImGui.Checkbox("显示角色重命名和复制UI", ref config.ShowCopyUi);

                ImGuiExt.Separator();
                ImGui.Text("绕过卫月框架的插件UI隐藏机制：");
                ImGui.Indent();
                if (ImGui.Checkbox("进入集体动作时", ref config.ConfigInGpose)) {
                    PluginService.PluginInterface.UiBuilder.DisableGposeUiHide = config.ConfigInGpose;
                }

                if (ImGui.Checkbox("进入过场动画时", ref config.ConfigInCutscene)) {
                    PluginService.PluginInterface.UiBuilder.DisableCutsceneUiHide = config.ConfigInCutscene;
                }

                ImGui.Unindent();
                ImGuiExt.Separator();
                ImGui.Text("临时偏移");
                ImGui.SameLine();
                using (ImRaii.PushFont(UiBuilder.IconFont)) {
                    ImGui.TextColored(ImGuiColors.DalamudWhite, FontAwesomeIcon.InfoCircle.ToIconString());
                }

                if (ImGui.IsItemHovered()) {
                    
                    ImGui.BeginTooltip();
                    
                    ImGui.TextWrapped("临时偏移允许您在不更改配置的情况下调整当前偏移。当您开始、结束一个循环的情感动作时，或者使用“重置偏移”按钮手动重置时，偏移将自动重置。");
                    ImGuiHelpers.ScaledDummy(350, 1);
                    ImGui.EndTooltip();
                }
                
                using (ImRaii.PushIndent())
                using (ImRaii.PushId("TempOffsets")) {
                    ImGui.Checkbox("显示编辑窗口", ref config.TempOffsetWindowOpen);
                    ImGui.SameLine();
                    ImGui.TextDisabled("使用命令切换");
                    ImGui.SameLine();
                    ImGui.TextColored(ImGuiColors.DalamudViolet, "/heels temp");
                    ImGui.Checkbox("显示提示", ref config.TempOffsetWindowTooltips);
                    ImGui.Checkbox("窗口锁定", ref config.TempOffsetWindowLock);
                    using (ImRaii.Disabled(config.TempOffsetWindowLock == false)) {
                        ImGui.Checkbox("窗口透明", ref config.TempOffsetWindowTransparent);
                    }

                    ImGui.Checkbox("显示加号/减号按钮", ref config.TempOffsetWindowPlusMinus);
                }
                
                
                ImGuiExt.Separator();

#if DEBUG
                ImGui.Checkbox("[调试] 在启动时打开设置窗口", ref config.DebugOpenOnStartup);
#endif

                if (Plugin.IsDebug) {
                    if (ImGui.TreeNode("调试")) {
                        ImGui.Text("上次报告的数据：");
                        ImGui.Indent();
                        ImGui.Text(ApiProvider.LastReportedData);
                        ImGui.Unindent();

                        ImGui.TreePop();
                    }

                    if (ImGui.TreeNode("PERFORMANCE")) {
                        PerformanceMonitors.DrawTable();
                        ImGui.TreePop();
                    }
                }

                if (config.UseModelOffsets && ImGui.CollapsingHeader("模型偏移编辑器")) {
                    ShowModelEditor();
                }
            }
        }

        ImGui.EndChild();
    }

    private void ShowModelEditor() {
        if (mdlEditorException != null) {
            ImGui.TextColored(ImGuiColors.DalamudRed, $"{mdlEditorException}");
        } else {
            ImGui.TextWrapped("这是一个非常简单的修改器，可以通过修改mdl文件来设置偏移值。");
            ImGui.Spacing();
            ImGui.Text("- 选择文件");
            ImGui.Text("- 设置偏移");
            ImGui.Text("- 保存修改后的文件");
            ImGui.Spacing();

            if (loadedFile != null) {
                var attributes = loadedFile.Attributes.ToList();

                ImGui.InputText("##loadedFile", ref loadedFilePath, 2048, ImGuiInputTextFlags.ReadOnly);
                var s = ImGui.GetItemRectSize();
                ImGui.SameLine();
                ImGui.Text("加载的文件");
                ImGui.Checkbox("使用TexTools安全属性", ref useTextoolSafeAttribute);

                ImGuiExt.FloatEditor("高跟鞋偏移", ref mdlEditorOffset, 0.001f, -1, 1, "%.5f", ImGuiSliderFlags.AlwaysClamp);
                var offset = attributes.FirstOrDefault(a => a.Length > 13 && a.StartsWith("heels_offset") && a[12] is '_' or '=');
                if (offset == null) {
                    ImGui.Text("模型未指定偏移值。");
                } else if (offset[12] == '_') {
                    var str = offset[13..].Replace("n_", "-").Replace('a', '0').Replace('b', '1').Replace('c', '2').Replace('d', '3').Replace('e', '4').Replace('f', '5').Replace('g', '6').Replace('h', '7').Replace('i', '8').Replace('j', '9').Replace('_', '.');
                    ImGui.Text($"当前偏移：{str}");
                } else {
                    ImGui.Text($"当前偏移值为：{offset[13..]}");
                }

                if (ImGui.Button("保存MDL文件")) {
                    if (_fileDialogManager == null) {
                        _fileDialogManager = new FileDialogManager();
                        PluginService.PluginInterface.UiBuilder.Draw += _fileDialogManager.Draw;
                    }

                    try {
                        _fileDialogManager.SaveFileDialog("保存MDL文件...", "MDL File{.mdl}", "output.mdl", ".mdl", (b, files) => {
                            attributes.RemoveAll(a => a.StartsWith("heels_offset"));
                            if (useTextoolSafeAttribute) {
                                var valueStr = mdlEditorOffset.ToString(CultureInfo.InvariantCulture).Replace("-", "n_").Replace(".", "_").Replace("0", "a").Replace("1", "b").Replace("2", "c").Replace("3", "d").Replace("4", "e").Replace("5", "f").Replace("6", "g").Replace("7", "h").Replace("8", "i").Replace("9", "j");

                                attributes.Add($"heels_offset_{valueStr}");
                            } else {
                                attributes.Add($"heels_offset={mdlEditorOffset.ToString(CultureInfo.InvariantCulture)}");
                            }

                            loadedFile.Attributes = attributes.ToArray();
                            var outputBytes = loadedFile.Write();
                            File.WriteAllBytes(files, outputBytes);
                            loadedFile = null;
                        }, Path.GetDirectoryName(loadedFilePath), true);
                    } catch (Exception ex) {
                        mdlEditorException = ex;
                    }
                }

                if (ImGui.Button("取消")) {
                    loadedFile = null;
                }
            } else {
                if (ImGui.Button("选择MDL文件")) {
                    if (_fileDialogManager == null) {
                        _fileDialogManager = new FileDialogManager();
                        PluginService.PluginInterface.UiBuilder.Draw += _fileDialogManager.Draw;
                    }

                    try {
                        _fileDialogManager.OpenFileDialog("选择MDL文件...", "MDL File{.mdl}", (b, files) => {
                            if (files.Count != 1) return;
                            loadedFilePath = files[0];
                            PluginService.Log.Info($"Loading MDL: {loadedFilePath}");

                            config.ModelEditorLastFolder = Path.GetDirectoryName(loadedFilePath) ?? string.Empty;
                            var bytes = File.ReadAllBytes(loadedFilePath);
                            loadedFile = new MdlFile(bytes);
                            var attributes = loadedFile.Attributes.ToList();
                            var offset = attributes.FirstOrDefault(a => a.StartsWith("heels_offset") && a.Length > 13);

                            if (offset != null) {
                                if (offset[12] == '_') {
                                    // TexTools safe attribute
                                    useTextoolSafeAttribute = true;

                                    var str = offset[13..].Replace("n_", "-").Replace('a', '0').Replace('b', '1').Replace('c', '2').Replace('d', '3').Replace('e', '4').Replace('f', '5').Replace('g', '6').Replace('h', '7').Replace('i', '8').Replace('j', '9').Replace('_', '.');

                                    if (!float.TryParse(str, CultureInfo.InvariantCulture, out mdlEditorOffset)) {
                                        mdlEditorOffset = 0;
                                    }
                                } else if (offset[12] == '=') {
                                    useTextoolSafeAttribute = false;
                                    if (!float.TryParse(offset[13..], CultureInfo.InvariantCulture, out mdlEditorOffset)) {
                                        mdlEditorOffset = 0;
                                    }
                                }
                            }
                        }, 1, config.ModelEditorLastFolder, true);
                    } catch (Exception ex) {
                        mdlEditorException = ex;
                    }
                }
            }
        }
    }

    private unsafe void DrawCharacterView(CharacterConfig? characterConfig) {
        if (characterConfig == null) return;

        var wearingMatchCount = 0;
        var usingDefault = true;

        GameObject* activeCharacter = null;
        Character* activeCharacterAsCharacter = null;
        IOffsetProvider? activeHeelConfig = null;

        if (characterConfig is GroupConfig gc) {
            var target = new[] { PluginService.Targets.SoftTarget, PluginService.Targets.Target, PluginService.ClientState.LocalPlayer }.FirstOrDefault(t => t is Dalamud.Game.ClientState.Objects.Types.Character character && gc.Matches(((GameObject*)character.Address)->DrawObject, character.Name.TextValue, (character is PlayerCharacter pc) ? pc.HomeWorld.Id : ushort.MaxValue));
            if (target is Dalamud.Game.ClientState.Objects.Types.Character) {
                activeCharacter = (GameObject*)target.Address;
                activeCharacterAsCharacter = (Character*)activeCharacter;
                activeHeelConfig = characterConfig.GetFirstMatch(activeCharacterAsCharacter);
                if (target is PlayerCharacter pc) {
                    ImGui.TextDisabled($"预览显示基于 {target.Name.TextValue} ({pc.HomeWorld?.GameData?.Name.RawString})");
                } else {
                    ImGui.TextDisabled($"预览显示基于 {target.Name.TextValue} (NPC)");
                }

                Plugin.RequestUpdateAll();
            }
        } else {
            var player = PluginService.Objects.FirstOrDefault(t => t is PlayerCharacter playerCharacter && playerCharacter.Name.TextValue == selectedName && playerCharacter.HomeWorld.Id == selectedWorld);
            if (player is PlayerCharacter) {
                activeCharacter = (GameObject*)player.Address;
                activeCharacterAsCharacter = (Character*)activeCharacter;
                activeHeelConfig = characterConfig.GetFirstMatch(activeCharacterAsCharacter);
                Plugin.NeedsUpdate[activeCharacter->ObjectIndex] = true;
            }
        }

        if (activeCharacter != null && Plugin.IpcAssignedData.TryGetValue(activeCharacter->ObjectID, out var ipcCharacterConfig)) {
            characterConfig = ipcCharacterConfig;
        }

        if (characterConfig is IpcCharacterConfig) {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "此角色的配置已由另一个插件分配。");
            if (Plugin.IsDebug && activeCharacter != null) {
                ImGui.SameLine();
                if (ImGui.SmallButton("清除IPC")) {
                    Plugin.IpcAssignedData.Remove(activeCharacter->ObjectID);
                    selectedCharacter = null;
                    selectedWorld = 0;
                    selectedName = string.Empty;
                    return;
                }
            }
        }

        if (characterConfig is not IpcCharacterConfig && config.ShowCopyUi && newWorld != 0) {
            ImGui.InputText("角色名称", ref newName, 64);
            var worldName = PluginService.Data.GetExcelSheet<World>()!.GetRow(newWorld)!.Name.ToDalamudString().TextValue;
            if (ImGui.BeginCombo("世界", worldName)) {
                foreach (var world in PluginService.Data.GetExcelSheet<World>()!.Where(w => w.IsPublic).OrderBy(w => w.Name.ToDalamudString().TextValue, StringComparer.OrdinalIgnoreCase)) {
                    if (ImGui.Selectable($"{world.Name.ToDalamudString().TextValue}", world.RowId == newWorld)) {
                        newWorld = world.RowId;
                    }
                }

                ImGui.EndCombo();
            }

            using (ImRaii.Disabled(!ImGui.GetIO().KeyShift)) {
                if (ImGui.Button("创建组") && selectedCharacter != null) {
                    if (new GroupConfig { Label = $"组来自[{selectedName}@{worldName}]", HeelsConfig = selectedCharacter.HeelsConfig, EmoteConfigs = selectedCharacter.EmoteConfigs, Enabled = false }.Initialize() is GroupConfig group) {
                        var copy = JsonConvert.DeserializeObject<GroupConfig>(JsonConvert.SerializeObject(group));
                        if (copy != null) {
                            config.Groups.Add(copy);
                            selectedCharacter = null;
                            selectedName = string.Empty;
                            selectedWorld = 0;
                            selectedGroup = copy;
                        }
                    }
                }
            }

            if (!ImGui.GetIO().KeyShift && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                ImGui.SetTooltip("按住SHIFT键\n\n从该角色的配置中创建新的组分配。");
            }

            ImGui.SameLine();
            var isModified = newName != selectedName || newWorld != selectedWorld;
            {
                var newAlreadyExists = config.WorldCharacterDictionary.ContainsKey(newWorld) && config.WorldCharacterDictionary[newWorld].ContainsKey(newName);

                using (ImRaii.Disabled(isModified == false || newAlreadyExists)) {
                    if (ImGui.Button("重命名角色配置")) {
                        if (selectedCharacter != null && config.TryAddCharacter(newName, newWorld)) {
                            config.WorldCharacterDictionary[newWorld][newName] = selectedCharacter;
                            config.WorldCharacterDictionary[selectedWorld].Remove(selectedName);
                            if (config.WorldCharacterDictionary[selectedWorld].Count == 0) {
                                config.WorldCharacterDictionary.Remove(selectedWorld);
                            }

                            selectedName = newName;
                            selectedWorld = newWorld;
                        }
                    }
                }

                var moveHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);

                ImGui.SameLine();
                using (ImRaii.Disabled(isModified == false || newAlreadyExists)) {
                    if (ImGui.Button("复制角色配置")) {
                        if (config.TryAddCharacter(newName, newWorld)) {
                            var j = JsonConvert.SerializeObject(selectedCharacter);
                            config.WorldCharacterDictionary[newWorld][newName] = (JsonConvert.DeserializeObject<CharacterConfig>(j) ?? new CharacterConfig()).Initialize();
                        }
                    }
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) || moveHovered) {
                    using (ImRaii.Tooltip()) {
                        ImGui.Text(moveHovered ? "更改此配置分配的角色。" : "将此配置复制给另一个角色。");
                        if (!isModified) {
                            ImGui.TextColored(ImGuiColors.DalamudYellow, "更改上面选框/输入框中的角色名称或世界名称。");
                        } else if (newAlreadyExists) {
                            ImGui.TextColored(ImGuiColors.DalamudOrange, "新角色已拥有配置。");
                        }
                    }
                }

                if (isModified && newAlreadyExists) {
                    ImGui.SameLine();
                    ImGui.TextDisabled("配置中已存在此角色。");
                }
            }

            ImGuiExt.Separator();
        }

        if (characterConfig is not IpcCharacterConfig && characterConfig.HeelsConfig.Count > 0 && !characterConfig.HeelsConfig.Any(hc => hc.Enabled)) {
            ImGui.BeginGroup();
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudOrange);
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.Text(FontAwesomeIcon.ExclamationTriangle.ToIconString());
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextWrapped("此角色下的所有偏移项当前都处于禁用状态。单击“启用”标题下的复选框开始使用高跟鞋偏移。");
            ImGui.PopStyleColor();
            ImGui.EndGroup();

            if (ImGui.IsItemHovered() && firstCheckboxScreenPosition is not { X : 0, Y : 0 }) {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                var dl = ImGui.GetForegroundDrawList();
                dl.AddLine(ImGui.GetMousePos(), firstCheckboxScreenPosition, ImGui.GetColorU32(ImGuiColors.DalamudOrange), 2);
            }
        }

        using (ImRaii.Disabled(characterConfig is IpcCharacterConfig)) {
            if (characterConfig is not (IpcCharacterConfig or GroupConfig) && ImGui.Checkbox($"为[{selectedName}]启用偏移。", ref characterConfig.Enabled)) {
                Plugin.RequestUpdateAll();
            }

            if (selectedCharacter is { Enabled: false }) {
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudRed, "此配置已禁用。");
            }

            ImGuiExt.Separator();

            if (characterConfig is not IpcCharacterConfig && ImGui.CollapsingHeader("装备偏移", ImGuiTreeNodeFlags.DefaultOpen)) {
                var activeFootwear = GetModelIdForPlayer(activeCharacter, ModelSlot.Feet);
                var activeFootwearPath = GetModelPathForPlayer(activeCharacter, ModelSlot.Feet);

                var activeTop = GetModelIdForPlayer(activeCharacter, ModelSlot.Top);
                var activeTopPath = GetModelPathForPlayer(activeCharacter, ModelSlot.Top);

                var activeLegs = GetModelIdForPlayer(activeCharacter, ModelSlot.Legs);
                var activeLegsPath = GetModelPathForPlayer(activeCharacter, ModelSlot.Legs);

                var windowMax = ImGui.GetWindowPos() + ImGui.GetWindowSize();
                if (ImGui.BeginTable("OffsetsTable", 5)) {
                    ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, checkboxSize * 4 + 3 * ImGuiHelpers.GlobalScale);
                    ImGui.TableSetupColumn("标签", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
                    ImGui.TableSetupColumn("偏移", ImGuiTableColumnFlags.WidthFixed, (90 + (config.ShowPlusMinusButtons ? 50 : 0)) * ImGuiHelpers.GlobalScale);
                    ImGui.TableSetupColumn("服装", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, checkboxSize);

                    TableHeaderRow(TableHeaderAlign.Right, TableHeaderAlign.Center, TableHeaderAlign.Center, TableHeaderAlign.Left);
                    var deleteIndex = -1;
                    for (var i = 0; i < characterConfig.HeelsConfig.Count; i++) {
                        ImGui.BeginDisabled(beginDrag == i);
                        ImGui.PushID($"heels_{i}");
                        var heelConfig = characterConfig.HeelsConfig[i];
                        heelConfig.Label ??= string.Empty;
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();

                        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.One * ImGuiHelpers.GlobalScale);
                        ImGui.PushFont(UiBuilder.IconFont);

                        if (ImGui.Button($"{(char)(heelConfig.Locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen)}", new Vector2(checkboxSize))) {
                            if (heelConfig.Locked == false || ImGui.GetIO().KeyShift)
                                heelConfig.Locked = !heelConfig.Locked;
                        }

                        if (ImGui.IsItemHovered()) {
                            ImGui.PopFont();
                            if (heelConfig.Locked && ImGui.GetIO().KeyShift) {
                                ImGui.SetTooltip("解锁");
                            } else if (heelConfig.Locked) {
                                ImGui.SetTooltip("按住SHIFT键点击解锁");
                            } else {
                                ImGui.SetTooltip("锁定");
                            }

                            ImGui.PushFont(UiBuilder.IconFont);
                        }

                        if (beginDrag >= 0 && MouseWithin(ImGui.GetItemRectMin(), new Vector2(windowMax.X, ImGui.GetItemRectMax().Y))) {
                            endDrag = i;
                            endDragPosition = ImGui.GetItemRectMin();
                        }

                        ImGui.SameLine();

                        if (beginDrag != i && heelConfig.Locked) ImGui.BeginDisabled(heelConfig.Locked);

                        if (ImGui.Button($"{(char)FontAwesomeIcon.Trash}##delete", new Vector2(checkboxSize)) && ImGui.GetIO().KeyShift) {
                            deleteIndex = i;
                        }

                        if (ImGui.IsItemHovered() && !ImGui.GetIO().KeyShift) {
                            ImGui.PopFont();
                            ImGui.SetTooltip("按住SHIFT键点击删除。");
                            ImGui.PushFont(UiBuilder.IconFont);
                        }

                        ImGui.SameLine();
                        if (beginDrag != i && heelConfig.Locked) ImGui.EndDisabled();
                        ImGui.Button($"{(char)FontAwesomeIcon.ArrowsUpDown}", new Vector2(checkboxSize));
                        if (beginDrag == -1 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
                            beginDrag = i;
                            endDrag = i;
                            endDragPosition = ImGui.GetItemRectMin();
                        }

                        ImGui.SameLine();
                        ImGui.PopFont();
                        ImGui.PopStyleVar();
                        if (ImGui.Checkbox("##enable", ref heelConfig.Enabled)) {
                            if (heelConfig.Enabled) {
                                foreach (var heel in characterConfig.GetDuplicates(heelConfig, true)) {
                                    heel.Enabled = false;
                                }

                                heelConfig.Enabled = true;
                            }
                        }

                        if (i == 0) {
                            firstCheckboxScreenPosition = ImGui.GetItemRectMin() + ImGui.GetItemRectSize() / 2;
                        }

                        if (ImGui.IsItemHovered()) {
                            ImGui.BeginTooltip();

                            if (heelConfig.Enabled) {
                                ImGui.Text("单击禁用此偏移。");
                            } else {
                                ImGui.Text("单击启用此偏移。");
                                var match = characterConfig.GetDuplicates(heelConfig, true).FirstOrDefault();
                                if (match != null) {
                                    if (!string.IsNullOrWhiteSpace(match.Label)) {
                                        ImGui.TextDisabled($"'{match.Label}' 将被禁用，因为使用了相同物品。");
                                    } else {
                                        ImGui.TextDisabled($"影响相同物品的偏移项将被禁用。");
                                    }
                                }
                            }

                            ImGui.EndTooltip();
                        }

                        if (beginDrag != i && heelConfig.Locked) ImGui.BeginDisabled(heelConfig.Locked);

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        ImGui.InputText("##label", ref heelConfig.Label, 100);

                        ImGui.TableNextColumn();

                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        if (ImGuiExt.FloatEditor("##offset", ref heelConfig.Offset, 0.001f, float.MinValue, float.MaxValue, "%.5f")) {
                            if (heelConfig.Enabled) Plugin.RequestUpdateAll();
                        }

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

                        var pathMode = heelConfig.PathMode;
                        var pathDisplay = heelConfig.Path ?? string.Empty;
                        if (pathMode) {
                            pathDisplay = pathDisplay.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                            if (!string.IsNullOrWhiteSpace(PenumbraModFolder) && !string.IsNullOrWhiteSpace(pathDisplay) && pathDisplay.StartsWith(PenumbraModFolder, StringComparison.InvariantCultureIgnoreCase)) {
                                pathDisplay = "[Penumbra] " + (heelConfig.Slot != ModelSlot.Feet ? $"[{heelConfig.Slot}] " : "") + pathDisplay.Remove(0, PenumbraModFolder.Length);
                            } else {
                                pathDisplay = (heelConfig.Slot != ModelSlot.Feet ? $"[{heelConfig.Slot}] " : "") + pathDisplay;
                            }
                        }

                        if (ImGui.BeginCombo("##footwear", pathMode ? pathDisplay : GetModelName(heelConfig.ModelId, heelConfig.Slot), ImGuiComboFlags.HeightLargest)) {
                            if (ImGui.BeginTabBar("##footwear_tabs")) {
                                if (pathMode) {
                                    if (ImGui.TabItemButton("模型ID")) {
                                        heelConfig.PathMode = false;
                                        (heelConfig.Slot, heelConfig.RevertSlot) = (heelConfig.RevertSlot, heelConfig.Slot);
                                    }
                                }

                                if (ImGui.BeginTabItem((pathMode ? "模型路径" : "模型ID") + "###currentConfigType")) {
                                    if (pathMode) {
                                        heelConfig.Path ??= string.Empty;
                                        ImGui.TextWrapped("根据模型的文件路径分配偏移值，可以是游戏路径或半影(Penumbra)的模型路径。");
                                        ImGui.TextDisabled("文件路径：");
                                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                                        ImGui.InputText("##pathInput", ref heelConfig.Path, 1024);

                                        ImGui.TextDisabled("装备槽：");
                                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                                        if (ImGui.BeginCombo("##slotInput", $"{heelConfig.Slot}")) {
                                            if (ImGui.Selectable($"{ModelSlot.Top}", heelConfig.Slot == ModelSlot.Top)) heelConfig.Slot = ModelSlot.Top;
                                            if (ImGui.Selectable($"{ModelSlot.Legs}", heelConfig.Slot == ModelSlot.Legs)) heelConfig.Slot = ModelSlot.Legs;
                                            if (ImGui.Selectable($"{ModelSlot.Feet}", heelConfig.Slot == ModelSlot.Feet)) heelConfig.Slot = ModelSlot.Feet;
                                            ImGui.EndCombo();
                                        }

                                        var activeSlotPath = heelConfig.Slot switch {
                                            ModelSlot.Top => activeTopPath,
                                            ModelSlot.Legs => activeLegsPath,
                                            _ => activeFootwearPath
                                        };

                                        if (activeSlotPath != null) {
                                            if (ImGui.Button("当前模型路径")) {
                                                heelConfig.Path = activeSlotPath;
                                            }

                                            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                                                ImGui.SetTooltip(activeSlotPath);
                                            }
                                        }
                                    } else {
                                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                                        if (ImGui.IsWindowAppearing()) {
                                            footwearSearch = string.Empty;
                                            ImGui.SetKeyboardFocusHere();
                                        }

                                        ImGui.InputTextWithHint("##footwearSearch", "搜索...", ref footwearSearch, 100);

                                        if (ImGui.BeginChild("##footwearSelectScroll", new Vector2(-1, 200))) {
                                            foreach (var shoeModel in shoeModelList.Value.Values) {
                                                if (!string.IsNullOrWhiteSpace(footwearSearch)) {
                                                    if (!((ushort.TryParse(footwearSearch, out var searchId) && searchId == shoeModel.Id) || (shoeModel.Name ?? $"Unknown#{shoeModel.Id}").Contains(footwearSearch, StringComparison.InvariantCultureIgnoreCase) || shoeModel.Items.Any(shoeItem => shoeItem.Name.ToDalamudString().TextValue.Contains(footwearSearch, StringComparison.InvariantCultureIgnoreCase)))) {
                                                        continue;
                                                    }
                                                }

                                                if (ImGui.Selectable($"{shoeModel.Name}##shoeModel_{shoeModel.Id}")) {
                                                    heelConfig.ModelId = shoeModel.Id;
                                                    heelConfig.Slot = shoeModel.Slot;
                                                    ImGui.CloseCurrentPopup();
                                                }

                                                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && shoeModel.Items.Count > 3) {
                                                    ShowModelTooltip(shoeModel.Id, shoeModel.Slot);
                                                }
                                            }
                                        }

                                        ImGui.EndChild();
                                    }

                                    ImGui.EndTabItem();
                                }

                                if (!pathMode) {
                                    if (ImGui.TabItemButton("模型路径")) {
                                        heelConfig.PathMode = true;
                                        (heelConfig.Slot, heelConfig.RevertSlot) = (heelConfig.RevertSlot, heelConfig.Slot);
                                    }
                                }

                                ImGui.EndTabBar();
                            }

                            ImGui.EndCombo();
                        }

                        ImGui.TableNextColumn();
                        ImGui.EndDisabled();

                        if ((heelConfig.Slot == ModelSlot.Feet && ((heelConfig.PathMode == false && activeFootwear == heelConfig.ModelId) || (heelConfig.PathMode && activeFootwearPath != null && activeFootwearPath.Equals(heelConfig.Path, StringComparison.OrdinalIgnoreCase)))) || (heelConfig.Slot == ModelSlot.Legs && ((heelConfig.PathMode == false && activeLegs == heelConfig.ModelId) || (heelConfig.PathMode && activeLegsPath != null && activeLegsPath.Equals(heelConfig.Path, StringComparison.OrdinalIgnoreCase)))) || (heelConfig.Slot == ModelSlot.Top && ((heelConfig.PathMode == false && activeTop == heelConfig.ModelId) || (heelConfig.PathMode && activeTopPath != null && activeTopPath.Equals(heelConfig.Path, StringComparison.OrdinalIgnoreCase))))) {
                            ShowActiveOffsetMarker(activeCharacterAsCharacter != null, heelConfig.Enabled, activeHeelConfig == heelConfig, "当前装备");
                            if (heelConfig.Enabled) {
                                wearingMatchCount++;
                                usingDefault = false;
                            }
                        }

                        ImGui.PopID();
                    }

                    if (deleteIndex >= 0) {
                        characterConfig.HeelsConfig.RemoveAt(deleteIndex);
                    }

                    ImGui.EndTable();

                    if (wearingMatchCount > 1) {
                        ImGui.BeginGroup();
                        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled));
                        ImGui.PushFont(UiBuilder.IconFont);
                        ImGui.Text(FontAwesomeIcon.InfoCircle.ToIconString());
                        ImGui.PopFont();
                        ImGui.SameLine();
                        ImGui.TextWrapped("你佩戴的装备匹配到多个已启用的偏移项。");
                        ImGui.PopStyleColor();
                        ImGui.EndGroup();
                        if (ImGui.IsItemHovered()) {
                            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                            ImGui.BeginTooltip();
                            ImGui.Text("偏移值将按以下优先级生效：");
                            ImGui.Text(" - 首先启用你佩戴的'[身体装备]'偏移值。");
                            ImGui.Text(" - 然后启用你佩戴的'[腿部装备]'偏移值。");
                            ImGui.Text(" - 最后启用你佩戴的'[脚部装备]'偏移值。");
                            ImGui.EndTooltip();
                        }
                    }

                    if (beginDrag >= 0) {
                        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) {
                            if (endDrag != beginDrag) {
                                var move = characterConfig.HeelsConfig[beginDrag];
                                characterConfig.HeelsConfig.RemoveAt(beginDrag);
                                characterConfig.HeelsConfig.Insert(endDrag, move);
                            }

                            beginDrag = -1;
                            endDrag = -1;
                        } else {
                            var dl = ImGui.GetWindowDrawList();
                            dl.AddLine(endDragPosition, endDragPosition + new Vector2(ImGui.GetWindowContentRegionMax().X, 0), ImGui.GetColorU32(ImGuiCol.DragDropTarget), 2 * ImGuiHelpers.GlobalScale);
                        }
                    }

                    bool ShowAddButton(ushort id, ModelSlot slot) {
                        if (shoeModelList.Value.ContainsKey((id, slot))) {
                            if (ImGui.Button($"为[{GetModelName(id, slot)}]添加一条偏移设置")) {
                                characterConfig.HeelsConfig.Add(new HeelConfig() { ModelId = id, Slot = slot, Enabled = !characterConfig.HeelsConfig.Any(h => h is { PathMode: false } && h.ModelId == id) });
                            }

                            return true;
                        }

                        return false;
                    }

                    void ShowAddPathButton(string? path, ModelSlot slot) {
                        var pathDisplay = path ?? string.Empty;

                        pathDisplay = pathDisplay.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                        if (!string.IsNullOrWhiteSpace(PenumbraModFolder) && !string.IsNullOrWhiteSpace(pathDisplay) && pathDisplay.StartsWith(PenumbraModFolder, StringComparison.InvariantCultureIgnoreCase)) {
                            pathDisplay = "[Penumbra] " + (slot != ModelSlot.Feet ? $"[{slot}] " : "") + pathDisplay.Remove(0, PenumbraModFolder.Length);
                        } else {
                            pathDisplay = (slot != ModelSlot.Feet ? $"[{slot}] " : "") + pathDisplay;
                        }

                        if (ImGui.Button($"添加路径：{pathDisplay}")) {
                            characterConfig.HeelsConfig.Add(new HeelConfig() { PathMode = true, Path = path, Slot = slot, Enabled = !characterConfig.HeelsConfig.Any(h => h is { PathMode: true } && h.Path == path) });
                        }

                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                            ImGui.SetTooltip($"{path}");
                        }
                    }

                    if (characterConfig is not IpcCharacterConfig) {
                        if (ImGui.GetIO().KeyShift != config.PreferModelPath && (ImGui.GetIO().KeyCtrl ? activeLegsPath : ImGui.GetIO().KeyAlt ? activeTopPath : activeFootwearPath) != null) {
                            ShowAddPathButton(ImGui.GetIO().KeyCtrl ? activeLegsPath : ImGui.GetIO().KeyAlt ? activeTopPath : activeFootwearPath, ImGui.GetIO().KeyCtrl ? ModelSlot.Legs : ImGui.GetIO().KeyAlt ? ModelSlot.Top : ModelSlot.Feet);
                        } else {
                            if (!(ShowAddButton(activeTop, ModelSlot.Top) || ShowAddButton(activeLegs, ModelSlot.Legs) || ShowAddButton(activeFootwear, ModelSlot.Feet))) {
                                if (ImGui.Button($"添加一条新的偏移设置")) {
                                    characterConfig.HeelsConfig.Add(new HeelConfig() { ModelId = 0, Slot = ModelSlot.Feet, Enabled = !characterConfig.HeelsConfig.Any(h => h is { PathMode: false, ModelId: 0 }) });
                                }
                            }
                        }
                    }
                }
            }

            ImGuiExt.Separator();
            var tableDl = ImGui.GetWindowDrawList();
            var w = ImGui.GetContentRegionAvail().X;

            var emoteOffsetsOpen = true;

            if (characterConfig is IpcCharacterConfig) {
                ImGui.CollapsingHeader("情感动作偏移##ipcCharacterConfig", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Leaf);
            } else {
                emoteOffsetsOpen = ImGui.CollapsingHeader("情感动作偏移", ImGuiTreeNodeFlags.DefaultOpen);
            }

            if (emoteOffsetsOpen && characterConfig.EmoteConfigs != null) {
                if (ImGui.BeginTable("emoteOffsets", characterConfig is IpcCharacterConfig ? 6 : 8, ImGuiTableFlags.NoClip)) {
                    if (characterConfig is not IpcCharacterConfig) {
                        ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, checkboxSize * 4 + 3 * ImGuiHelpers.GlobalScale);
                        ImGui.TableSetupColumn("标签", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
                    }

                    ImGui.TableSetupColumn("情感动作", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("高度偏移", ImGuiTableColumnFlags.WidthFixed, (90 + (config.ShowPlusMinusButtons ? 50 : 0)) * ImGuiHelpers.GlobalScale);
                    ImGui.TableSetupColumn("前后偏移", ImGuiTableColumnFlags.WidthFixed, (90 + (config.ShowPlusMinusButtons ? 50 : 0)) * ImGuiHelpers.GlobalScale);
                    ImGui.TableSetupColumn("左右偏移", ImGuiTableColumnFlags.WidthFixed, (90 + (config.ShowPlusMinusButtons ? 50 : 0)) * ImGuiHelpers.GlobalScale);
                    ImGui.TableSetupColumn("    旋转", ImGuiTableColumnFlags.WidthFixed, (50 + (config.ShowPlusMinusButtons ? 50 : 0)) * ImGuiHelpers.GlobalScale);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, checkboxSize);

                    if (characterConfig is IpcCharacterConfig) {
                        TableHeaderRow(TableHeaderAlign.Left, TableHeaderAlign.Center, TableHeaderAlign.Center, TableHeaderAlign.Center, TableHeaderAlign.Center, TableHeaderAlign.Left);
                    } else {
                        TableHeaderRow(TableHeaderAlign.Right, TableHeaderAlign.Center, TableHeaderAlign.Left, TableHeaderAlign.Center, TableHeaderAlign.Center, TableHeaderAlign.Center, TableHeaderAlign.Center, TableHeaderAlign.Left);
                    }

                    var i = 0;
                    EmoteConfig? deleteEmoteConfig = null;

                    for (var emoteIndex = 0; emoteIndex < characterConfig.EmoteConfigs.Count; emoteIndex++) {
                        var e = characterConfig.EmoteConfigs[emoteIndex];
                        using var _ = ImRaii.PushId($"emoteConfig_{i++}");
                        ImGui.TableNextRow();

                        if (characterConfig is not IpcCharacterConfig) {
                            ImGui.TableNextColumn();
                            if (i != 0) {
                                var s = ImGui.GetStyle().ItemSpacing.Y / 2;
                                tableDl.AddLine(ImGui.GetCursorScreenPos() - new Vector2(0, s), ImGui.GetCursorScreenPos() + new Vector2(w, -s), ImGui.GetColorU32(ImGuiCol.Separator) & 0x20FFFFFF);
                            }

                            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(1))) {
                                ImGui.Dummy(new Vector2(checkboxSize));
                                ImGui.SameLine();
                                using (ImRaii.Disabled(e.Locked || !ImGui.GetIO().KeyShift))
                                using (ImRaii.PushFont(UiBuilder.IconFont)) {
                                    if (ImGui.Button(FontAwesomeIcon.Trash.ToIconString(), new Vector2(checkboxSize))) {
                                        deleteEmoteConfig = e;
                                    }
                                }

                                if (e.Locked == false && !ImGui.GetIO().KeyShift && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
                                    ImGui.SetTooltip("按住SHIFT键点击删除。");
                                }

                                ImGui.SameLine();
                                using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), e.Editing))
                                using (ImRaii.PushFont(UiBuilder.IconFont)) {
                                    if (ImGui.Button(FontAwesomeIcon.Edit.ToIconString(), new Vector2(checkboxSize))) {
                                        e.Editing = !e.Editing;
                                    }
                                }

                                ImGui.SameLine();
                                ImGui.Checkbox("##enable", ref e.Enabled);
                            }

                            ImGui.TableNextColumn();
                            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                            ImGui.InputText("##label", ref e.Label, 100);
                        }

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

                        var previewEmoteName = characterConfig is not IpcCharacterConfig && e is { Editing: false, LinkedEmotes.Count: > 0 } ? $"{e.Emote.Name} (+ {e.LinkedEmotes.Count} other{(e.LinkedEmotes.Count > 1 ? "s" : "")})" : e.Emote.Name;

                        ImGuiExt.IconTextFrame(e.Emote.Icon, previewEmoteName);
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        ImGuiExt.FloatEditor("##height", ref e.Offset.Y, 0.0001f, allowPlusMinus: characterConfig is not IpcCharacterConfig);
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        ImGuiExt.FloatEditor("##forward", ref e.Offset.Z, 0.0001f, allowPlusMinus: characterConfig is not IpcCharacterConfig);
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        ImGuiExt.FloatEditor("##side", ref e.Offset.X, 0.0001f, allowPlusMinus: characterConfig is not IpcCharacterConfig);
                        ImGui.TableNextColumn();
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        var rot = e.Rotation * 180f / MathF.PI;

                        if (ImGuiExt.FloatEditor("##rotation", ref rot, format: "%.0f", allowPlusMinus: characterConfig is not IpcCharacterConfig, customPlusMinus: 1)) {
                            if (rot < 0) rot += 360;
                            if (rot >= 360) rot -= 360;
                            e.Rotation = rot * MathF.PI / 180f;
                        }

                        ImGui.TableNextColumn();

                        var activeEmote = EmoteIdentifier.Get(activeCharacterAsCharacter);
                        
                        ShowActiveOffsetMarker(activeCharacterAsCharacter != null && activeEmote != null &&(activeEmote == e.Emote || (characterConfig is not IpcCharacterConfig && e.Editing == false && e.LinkedEmotes.Contains(activeEmote))), e.Enabled, activeHeelConfig == e, "当前正在执行情感动作。");

                        if (characterConfig is IpcCharacterConfig || e.Editing) {
                            var fl = characterConfig is not IpcCharacterConfig;
                            foreach (var linked in e.LinkedEmotes.ToArray()) {
                                using var __ = ImRaii.PushId($"linkedEmote_{i++}");
                                ImGui.TableNextRow();

                                if (characterConfig is not IpcCharacterConfig) {
                                    ImGui.TableNextColumn();
                                    ImGui.TableNextColumn();
                                    using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(1))) {
                                        using (ImRaii.PushFont(UiBuilder.IconFont)) {
                                            ImGui.Text($"{(char)FontAwesomeIcon.ArrowUpLong}");
                                        }

                                        var paddingSize = (ImGui.GetContentRegionAvail().X - ImGui.GetItemRectSize().X * 2 - ImGui.GetFrameHeight() * 2 - 4) / 2f;
                                        ImGui.SameLine();
                                        ImGui.Dummy(new Vector2(paddingSize, 1));
                                        ImGui.SameLine();

                                        if (ImGuiExt.IconButton(FontAwesomeIcon.Unlink, null, ImGui.GetIO().KeyShift, "取消链接情感动作", "按住SHIFT")) {
                                            e.LinkedEmotes.Remove(linked);
                                            characterConfig.EmoteConfigs.Insert(emoteIndex + 1, new EmoteConfig() { Enabled = e.Enabled, Emote = linked, Offset = new Vector3(e.Offset.X, e.Offset.Y, e.Offset.Z), Rotation = e.Rotation });
                                        }

                                        ImGui.SameLine();

                                        if (ImGuiExt.IconButton(FontAwesomeIcon.Trash, null, ImGui.GetIO().KeyShift, "删除链接的情感动作", "按住SHIFT")) {
                                            e.LinkedEmotes.Remove(linked);
                                        }

                                        ImGui.SameLine();
                                        ImGui.Dummy(new Vector2(paddingSize, 1));
                                        ImGui.SameLine();

                                        using (ImRaii.PushFont(UiBuilder.IconFont)) {
                                            ImGui.Text($"{(char)FontAwesomeIcon.ArrowUpLong}");
                                        }
                                    }
                                }

                                ImGui.TableNextColumn();

                                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                                ImGuiExt.IconTextFrame(linked.Icon, linked.Name);
                                ImGui.TableNextColumn();
                                using (ImRaii.PushFont(UiBuilder.IconFont)) {
                                    ImGui.Text($"{(char)FontAwesomeIcon.ArrowUpLong}");
                                }

                                if (fl) {
                                    fl = false;
                                    ImGui.SameLine();
                                    ImGui.TextDisabled("链接的情感动作使用与其基础相同的偏移量。");
                                }

                                ImGui.TableNextColumn();
                                ImGui.TableNextColumn();
                                ImGui.TableNextColumn();
                                ImGui.TableNextColumn();

                                
                                
                                ShowActiveOffsetMarker(activeCharacterAsCharacter != null && activeEmote == linked, e.Enabled, activeHeelConfig == e, "情感动作当前正在执行");
                            }

                            if (characterConfig is not IpcCharacterConfig) {
                                ImGui.TableNextRow();
                                ImGui.TableNextColumn();
                                ImGui.TableNextColumn();
                                ImGui.TableNextColumn();

                                using (ImRaii.Disabled(activeCharacterAsCharacter == null || activeEmote == null || e.Emote == activeEmote || e.LinkedEmotes.Contains(activeEmote)))
                                using (ImRaii.PushFont(UiBuilder.IconFont)) {
                                    if (ImGui.Button(FontAwesomeIcon.PersonDressBurst.ToIconString(), new Vector2(checkboxSize))) {
                                        if (activeEmote != null) {
                                            e.LinkedEmotes.Add(activeEmote);
                                        }
                                    }
                                }

                                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && activeCharacterAsCharacter != null) {
                                    if (activeEmote == null) {
                                        ImGui.SetTooltip($"链接激活的情感动作");
                                    } else {
                                        ImGui.SetTooltip($"链接激活的情感动作：\n{activeEmote.Name}");
                                    }
                                    
                                }

                                ImGui.SameLine();

                                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                                if (ImGui.BeginCombo("##addLinkedEmote", "链接情感动作...", ImGuiComboFlags.HeightLargest)) {
                                    if (ImGui.IsWindowAppearing()) {
                                        searchInput = string.Empty;
                                        ImGui.SetKeyboardFocusHere();
                                    }

                                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                                    ImGui.InputTextWithHint("##searchInput", "搜索...", ref searchInput, 128);

                                    if (ImGui.BeginChild("##searchScroll", new Vector2(ImGui.GetContentRegionAvail().X, 300))) {
                                        
                                        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.ButtonHovered))) {
                                            foreach (var emote in EmoteIdentifier.List) {
                                                if (!string.IsNullOrWhiteSpace(searchInput)) {
                                                    if (!(emote.Name.Contains(searchInput, StringComparison.InvariantCultureIgnoreCase) || (ushort.TryParse(searchInput, out var searchShort) && searchShort == emote.EmoteModeId))) continue;
                                                }

                                                if (emote == e.Emote || e.LinkedEmotes.Contains(emote)) continue;
                                                // if (emote.Icon == 0) continue;
                                                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                                                if (ImGuiExt.IconTextFrame(emote.Icon, emote.Name, true)) {
                                                    e.LinkedEmotes.Add(emote);
                                                }
                                            }
                                        }
                                        
                                    }

                                    ImGui.EndChild();

                                    ImGui.EndCombo();
                                }
                            }
                        }
                    }

                    if (deleteEmoteConfig != null) {
                        characterConfig.EmoteConfigs.Remove(deleteEmoteConfig);
                    }

                    ImGui.EndTable();
                }

                if (characterConfig is not IpcCharacterConfig) {
                    var currentEmote = EmoteIdentifier.Get(activeCharacterAsCharacter);
                    using (ImRaii.Disabled(currentEmote == null))
                    using (ImRaii.PushFont(UiBuilder.IconFont)) {
                        if (ImGui.Button(FontAwesomeIcon.PersonDressBurst.ToIconString(), new Vector2(checkboxSize))) {
                            if (currentEmote != null) {
                                characterConfig.EmoteConfigs.Add(new EmoteConfig() { Emote = currentEmote, Enabled = characterConfig.EmoteConfigs.All(ec => ec.Enabled == false || ec.Emote != currentEmote) });
                            }
                        }
                    }

                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && activeCharacterAsCharacter != null) {
                        if (currentEmote == null) {
                            ImGui.SetTooltip($"添加当前情感动作");
                        } else {
                            ImGui.SetTooltip($"添加当前情感动作：\n{currentEmote.Name}");
                        }
                    }

                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(250 * ImGuiHelpers.GlobalScale);
                    if (ImGui.BeginCombo("##addCurrentEmote", "添加情感动作...", ImGuiComboFlags.HeightLargest)) {
                        if (ImGui.IsWindowAppearing()) {
                            searchInput = string.Empty;
                            ImGui.SetKeyboardFocusHere();
                        }

                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        ImGui.InputTextWithHint("##searchInput", "搜索...", ref searchInput, 128);
                        if (ImGui.BeginChild("##searchScroll", new Vector2(ImGui.GetContentRegionAvail().X, 300))) {
                            using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, ImGui.GetColorU32(ImGuiCol.ButtonHovered))) {
                                foreach (var emote in EmoteIdentifier.List) {
                                    if (!string.IsNullOrWhiteSpace(searchInput)) {
                                        if (!(emote.Name.Contains(searchInput, StringComparison.InvariantCultureIgnoreCase) || (ushort.TryParse(searchInput, out var searchShort) && searchShort == emote.EmoteModeId))) continue;
                                    }
                                    
                                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                                    if (ImGuiExt.IconTextFrame(emote.Icon, emote.Name, true)) {
                                        characterConfig.EmoteConfigs.Add(new EmoteConfig() { Emote = emote, Enabled = characterConfig.EmoteConfigs.All(ec => ec.Enabled == false || ec.Emote != emote) });
                                    }
                                }
                            }
                        }

                        ImGui.EndChild();
                        ImGui.EndCombo();
                    }
                }
            }

            ImGuiExt.Separator();
            ImGuiExt.FloatEditor("默认偏移", ref characterConfig.DefaultOffset, 0.001f, allowPlusMinus: characterConfig is not IpcCharacterConfig);
            ImGui.SameLine();
            ImGuiComponents.HelpMarker("默认偏移将用于所有尚未配置的鞋类。");
            ImGui.SameLine();
            ShowActiveOffsetMarker(activeCharacterAsCharacter != null && usingDefault, true, activeHeelConfig == characterConfig, "默认偏移处于激活状态");
        }

        if (Plugin.IsDebug && activeCharacter != null) {
            if (ImGui.TreeNode("Debug Information")) {
                if (ImGui.TreeNode("激活偏移")) {
                    if (activeHeelConfig == null) {
                        ImGui.TextDisabled("无激活的偏移");
                    } else if (activeHeelConfig is CharacterConfig cc) {
                        ImGui.Text($"使用默认偏移：{cc.DefaultOffset}");
                    } else {
                        Util.ShowStruct(activeHeelConfig, 0);
                    }
                    ImGui.TreePop();
                }
                
                if (ImGui.TreeNode("情感动作")) {
                    var emoteId = EmoteIdentifier.Get(activeCharacterAsCharacter);
                    if (emoteId == null) {
                        ImGui.TextDisabled("无");
                    } else {
                        Util.ShowObject(emoteId);
                    }
                    
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
        }

        if (Plugin.IsDebug) {
            if (characterConfig is IpcCharacterConfig ipcCharacter && ImGui.TreeNode("IPC Data")) {
                if (ipcCharacter.EmotePosition != null && activeCharacterAsCharacter->Mode is Character.CharacterModes.EmoteLoop or Character.CharacterModes.InPositionLoop) {
                    ImGui.Text("Position Error:");
                    var pos = (Vector3) activeCharacter->Position;
                    var emotePos = ipcCharacter.EmotePosition.GetOffset();

                    var eR = 180f / MathF.PI * ipcCharacter.EmotePosition.R;
                    var cR = 180f / MathF.PI * activeCharacter->Rotation;
                    
                    var rotDif = 180 - MathF.Abs(MathF.Abs(eR - cR) - 180);
                    ImGui.Indent();
                    ImGui.Text($"Position: {Vector3.Distance(pos, emotePos)}");
                    ImGui.Text($"Rotation: {rotDif}");
                    if (ImGui.GetIO().KeyShift) {
                        if (PluginService.GameGui.WorldToScreen(pos, out var a)) {
                            var dl = ImGui.GetBackgroundDrawList(ImGuiHelpers.MainViewport);
                            dl.AddCircleFilled(a, 3, 0xFF0000FF);
                        }

                        if (PluginService.GameGui.WorldToScreen(emotePos, out var b)) {
                            var dl = ImGui.GetBackgroundDrawList(ImGuiHelpers.MainViewport);
                            dl.AddCircle(b, 3, 0xFF00FF00);
                        }
                    }
                    
                    ImGui.Unindent();
                }
                
                ImGui.TextWrapped(ipcCharacter.IpcJson);
                ImGui.TreePop();
            }
        }
    }

    private void ShowActiveOffsetMarker(bool show, bool isEnabled, bool isActive, string tooltipText) {
        if (!show) {
            using (ImRaii.PushFont(UiBuilder.IconFont)) 
                ImGui.Dummy(ImGui.CalcTextSize(FontAwesomeIcon.ArrowLeft.ToIconString()));
            return;
        }
        
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, isEnabled && isActive))
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet, isEnabled && isActive == false))
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), isEnabled == false))
        using (ImRaii.PushFont(UiBuilder.IconFont)) {
            ImGui.Text(FontAwesomeIcon.ArrowLeft.ToIconString());
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            using (ImRaii.Tooltip()) {
                ImGui.Text(tooltipText);
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen, isEnabled && isActive))
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.DalamudViolet, isEnabled && isActive == false))
                using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetColorU32(ImGuiCol.TextDisabled), isEnabled == false)) {
                    ImGui.Text($"此条设置{(isActive ? "已激活" : "未激活")}");
                    if (!isActive) {
                        if (isEnabled) {
                            ImGui.Text("正在优先使用另一条设置。");
                        } else {
                            ImGui.Text("此条设置已被禁用。");
                        }
                    }
                }
            }
        }
    }

    private static unsafe ushort GetModelIdForPlayer(GameObject* obj, ModelSlot slot) {
        if (obj == null) return ushort.MaxValue;
        if (obj->DrawObject == null) return ushort.MaxValue;
        if (obj->DrawObject->Object.GetObjectType() != ObjectType.CharacterBase) return ushort.MaxValue;
        var characterBase = (CharacterBase*)obj->DrawObject;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human) return ushort.MaxValue;
        var human = (Human*)obj->DrawObject;
        if (human == null) return ushort.MaxValue;
        return slot switch {
            ModelSlot.Feet => human->Feet.Id,
            ModelSlot.Top => human->Top.Id,
            ModelSlot.Legs => human->Legs.Id,
            _ => ushort.MaxValue
        };
    }

    private static unsafe string? GetModelPathForPlayer(GameObject* obj, ModelSlot slot) {
        if (obj == null) return null;
        if (obj->DrawObject == null) return null;
        if (obj->DrawObject->Object.GetObjectType() != ObjectType.CharacterBase) return null;
        var characterBase = (CharacterBase*)obj->DrawObject;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human) return null;
        var human = (Human*)obj->DrawObject;
        if (human == null) return null;
        if ((byte)slot > human->CharacterBase.SlotCount) return null;
        var modelArray = human->CharacterBase.Models;
        if (modelArray == null) return null;
        var feetModel = modelArray[(byte)slot];
        if (feetModel == null) return null;
        var modelResource = feetModel->ModelResourceHandle;
        if (modelResource == null) return null;

        return modelResource->ResourceHandle.FileName.ToString();
    }

    private string? GetModelName(ushort modelId, ModelSlot slot, bool nullOnNoMatch = false) {
        if (modelId == 0) return "Smallclothes" + (slot == ModelSlot.Feet ? " (赤脚)" : "");

        if (shoeModelList.Value.TryGetValue((modelId, slot), out var shoeModel)) {
            return shoeModel.Name ?? $"Unknown#{modelId}";
        }

        return nullOnNoMatch ? null : $"Unknown#{modelId}";
    }

    private void ShowModelTooltip(ushort modelId, ModelSlot slot) {
        ImGui.BeginTooltip();

        try {
            if (modelId == 0) {
                ImGui.Text("Smallclothes (赤脚)");
                return;
            }

            if (shoeModelList.Value.TryGetValue((modelId, slot), out var shoeModel)) {
                foreach (var i in shoeModel.Items) {
                    ImGui.Text($"{i.Name.ToDalamudString().TextValue}");
                }
            } else {
                ImGui.Text($"未知物品 (Model#{modelId})");
            }
        } finally {
            ImGui.EndTooltip();
        }
    }

    public override void OnClose() {
        PluginService.PluginInterface.SavePluginConfig(config);
        base.OnClose();
    }

    private bool MouseWithin(Vector2 min, Vector2 max) {
        var mousePos = ImGui.GetMousePos();
        return mousePos.X >= min.X && mousePos.Y <= max.X && mousePos.Y >= min.Y && mousePos.Y <= max.Y;
    }

    private void TableHeaderRow(params TableHeaderAlign[] aligns) {
        ImGui.TableNextRow();
        for (var i = 0; i < ImGui.TableGetColumnCount(); i++) {
            ImGui.TableNextColumn();
            var label = ImGui.TableGetColumnName(i);
            ImGui.PushID($"TableHeader_{i}");
            var align = aligns.Length <= i ? TableHeaderAlign.Left : aligns[i];

            switch (align) {
                case TableHeaderAlign.Center: {
                    var textSize = ImGui.CalcTextSize(label);
                    var space = ImGui.GetContentRegionAvail().X;
                    ImGui.TableHeader("");
                    ImGui.SameLine(space / 2f - textSize.X / 2f);
                    ImGui.Text(label);

                    break;
                }
                case TableHeaderAlign.Right: {
                    ImGui.TableHeader("");
                    var textSize = ImGui.CalcTextSize(label);
                    var space = ImGui.GetContentRegionAvail().X;
                    ImGui.SameLine(space - textSize.X);
                    ImGui.Text(label);
                    break;
                }
                default:
                    ImGui.TableHeader(label);
                    break;
            }

            ImGui.PopID();
        }
    }

    public void ToggleWithWarning() {
        if (IsOpen && hiddenStopwatch.ElapsedMilliseconds < 1000) {
            IsOpen = false;
        } else if (IsOpen == false) {
            IsOpen = true;
            notVisibleWarningCancellationTokenSource?.Cancel();
            notVisibleWarningCancellationTokenSource = new CancellationTokenSource();
            PluginService.Framework.RunOnTick(() => {
                if (notVisibleWarningCancellationTokenSource == null || notVisibleWarningCancellationTokenSource.IsCancellationRequested) return;

                // UI Should be visible but was never drawn
                var message = new SeStringBuilder();
                message.AddText("[");
                message.AddUiForeground($"{plugin.Name}", 48);
                message.AddText("] 设置窗口当前处于隐藏状态：");

                if (PluginService.ClientState.IsGPosing) {
                    message.AddText("处于集体动作");
                    message.AddUiForeground(37);
                    message.Add(clickAllowInGposePayload);
                    message.AddText("点击这里");
                    message.Add(RawPayload.LinkTerminator);
                    message.AddUiForegroundOff();
                    message.AddText("以允许在集体动作中显示设置窗口。");
                } else if (PluginService.PluginInterface.UiBuilder.CutsceneActive) {
                    message.AddText("处于过场动画");
                    message.AddUiForeground(37);
                    message.Add(clickAllowInCutscenePayload);
                    message.AddText("点击这里");
                    message.Add(RawPayload.LinkTerminator);
                    message.AddUiForegroundOff();
                    message.AddText("以允许在过场动画中显示设置窗口。");
                } else {
                    // Unknown reason, don't mention it at all
                    return;
                }

                PluginService.ChatGui.PrintError(message.Build());
            }, TimeSpan.FromMilliseconds(250), cancellationToken: notVisibleWarningCancellationTokenSource.Token);
        } else {
            notVisibleWarningCancellationTokenSource?.Cancel();
            IsOpen = false;
        }
    }

    private class ShoeModel {
        public readonly List<Item> Items = new();
        public ushort Id;

        private string? nameCache;
        public ModelSlot Slot;

        public string? Name {
            get {
                if (nameCache != null) return nameCache;
                nameCache = Items.Count switch {
                    0 => $"未知#{Id}",
                    1 => Items[0].Name.ToDalamudString().TextValue,
                    2 => string.Join("和", Items.Select(i => i.Name.ToDalamudString().TextValue)),
                    > 3 => $"{Items[0].Name.ToDalamudString().TextValue} & {Items.Count - 1} 个其他同模装备。",
                    _ => string.Join(", ", Items.Select(i => i.Name.ToDalamudString().TextValue))
                };

                switch (Slot) {
                    case ModelSlot.Legs: {
                        nameCache = $"[腿部装备] {nameCache}";
                        break;
                    }
                    case ModelSlot.Top: {
                        nameCache = $"[身体装备] {nameCache}";
                        break;
                    }
                }

                return nameCache;
            }
            set => nameCache = value;
        }
    }

    private enum TableHeaderAlign {
        Left,
        Center,
        Right
    }
}
