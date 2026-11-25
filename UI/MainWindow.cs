using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FriendListFrenzy.Models;
using FriendListFrenzy.Services;

namespace FriendListFrenzy.UI;

public sealed class MainWindow : Window
{
    private readonly HashSet<ulong> selectedIds = new();

    // Filters & sorting
    private string searchQuery = string.Empty;
    private int statusFilter; // 0=All, 1=Online, 2=Offline, 3=Protected
    private string groupFilterId = "__all__"; // "__all__", "__none__", or group.Id
    private int sortMode; // 0=Online First, 1=Name A-Z, 2=World, 3=Last Seen, 4=Group

    // Edit friend modal state
    private ulong editingContentId;
    private string editFriendName = string.Empty;
    private string editNickname = string.Empty;
    private string editNote = string.Empty;
    private string editGroupId = string.Empty;
    private bool editProtected;
    private bool editUseColor;
    private Vector4 editColor = Theme.YellowMain;
    private bool openEditModal;

    // Bulk confirm modal state
    private bool openConfirmModal;
    private List<FriendEntry> pendingBatchTargets = new();

    // New group form
    private string newGroupName = string.Empty;
    private Vector4 newGroupColor = Theme.YellowMain;

    public MainWindow() : base("FriendList Frenzy###FriendListFrenzyMain", ImGuiWindowFlags.NoScrollbar)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(880, 560),
            MaximumSize = new Vector2(1900, 1200),
        };
        Size = new Vector2(1060, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var pushedColors = Theme.PushTheme();
        var pushedVars = Theme.PushStyleVars();

        try
        {
            DrawHeaderBar();

            if (Plugin.BulkRemover.IsActive)
            {
                ImGui.Spacing();
                DrawActiveBatchBanner();
            }

            ImGui.Spacing();

            if (ImGui.BeginTabBar("##FLFTabs"))
            {
                if (ImGui.BeginTabItem(L.T("Friends", "Amigos")))
                {
                    DrawFriendsTab();
                    ImGui.EndTabItem();
                }

                var bulkTabLabel = Plugin.BulkRemover.IsActive
                    ? L.T($"Bulk Remove ({Plugin.BulkRemover.RemainingCount})###BulkTab", $"Remoção em Massa ({Plugin.BulkRemover.RemainingCount})###BulkTab")
                    : L.T("Bulk Remove###BulkTab", "Remoção em Massa###BulkTab");

                if (ImGui.BeginTabItem(bulkTabLabel))
                {
                    DrawBulkRemoveTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(L.T("Groups", "Grupos")))
                {
                    DrawGroupsTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(L.T("Settings", "Configurações")))
                {
                    DrawSettingsTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            DrawEditFriendModal();
            DrawConfirmBulkModal();
        }
        finally
        {
            ImGui.PopStyleVar(pushedVars);
            ImGui.PopStyleColor(pushedColors);
        }
    }

    private void DrawHeaderBar()
    {
        ImGui.TextColored(Theme.YellowMain, "FRIENDLIST FRENZY");
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, "v1.0.0.0  •");
        ImGui.SameLine();

        var total = Plugin.Friends.TotalCount;
        var online = Plugin.Friends.OnlineCount;
        ImGui.TextColored(Theme.TextWhite, $"{total} / 200");
        ImGui.SameLine();
        ImGui.TextColored(Theme.GreenOnline, $"({online} {L.T("online", "online")})");

        // Right-aligned Refresh + Language toggle
        var refreshW = 92f;
        var langW = 68f;
        var rightOffset = ImGui.GetContentRegionAvail().X - (refreshW + langW + 10f);
        if (rightOffset > 0) ImGui.SameLine(ImGui.GetCursorPosX() + rightOffset);
        else ImGui.SameLine();

        if (ImGui.Button(L.T("Refresh", "Atualizar"), new Vector2(refreshW, 0)))
        {
            Plugin.Friends.RequestRefresh();
            Plugin.Friends.RefreshFromMemory();
        }

        ImGui.SameLine();
        if (ImGui.Button(L.IsPt ? "PT / EN" : "EN / PT", new Vector2(langW, 0)))
        {
            Plugin.Config.Language = L.IsPt ? "en" : "pt";
            Plugin.Config.Save();
        }

        ImGui.Separator();
    }

    private void DrawActiveBatchBanner()
    {
        var br = Plugin.BulkRemover;
        ImGui.BeginChild("##ActiveBatchBanner", new Vector2(0, 62f), true, ImGuiWindowFlags.NoScrollbar);

        var done = br.RemovedCount + br.FailedCount;
        var frac = br.TotalQueued > 0 ? (float)done / br.TotalQueued : 0f;

        ImGui.TextColored(Theme.YellowMain, br.IsPaused
            ? L.T("BULK REMOVAL PAUSED", "REMOÇÃO EM MASSA PAUSADA")
            : L.T("BULK REMOVAL IN PROGRESS", "REMOÇÃO EM MASSA EM ANDAMENTO"));
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, $"— {br.StatusMessage}");

        var btnW = 90f;
        var rightOffset = ImGui.GetContentRegionAvail().X - (btnW * 2 + 12f);
        if (rightOffset > 0) ImGui.SameLine(ImGui.GetCursorPosX() + rightOffset);
        else ImGui.SameLine();

        if (br.IsPaused)
        {
            if (ImGui.Button(L.T("Resume", "Retomar"), new Vector2(btnW, 0))) br.Resume();
        }
        else
        {
            if (ImGui.Button(L.T("Pause", "Pausar"), new Vector2(btnW, 0))) br.Pause();
        }

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedDangerDark);
        if (ImGui.Button(L.T("Cancel", "Cancelar"), new Vector2(btnW, 0))) br.Cancel();
        ImGui.PopStyleColor();

        ImGui.ProgressBar(frac, new Vector2(-1, 16f), $"{done} / {br.TotalQueued}  ({L.T("Removed", "Removidos")}: {br.RemovedCount})");
        ImGui.EndChild();
    }

    private void DrawFriendsTab()
    {
        var allFriends = Plugin.Friends.GetSnapshot();

        // Remove stale selections no longer on friend list
        if (selectedIds.Count > 0)
        {
            var liveSet = new HashSet<ulong>(allFriends.Select(f => f.ContentId));
            selectedIds.RemoveWhere(id => !liveSet.Contains(id));
        }

        if (allFriends.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.YellowMain, L.T(
                "No friend list data loaded yet.",
                "Nenhum dado da Lista de Amigos carregado ainda."));
            ImGui.TextColored(Theme.TextMuted, L.T(
                "Click 'Refresh' above or open your in-game Friend List once to sync.",
                "Clique em 'Atualizar' acima ou abra sua Lista de Amigos no jogo uma vez para sincronizar."));
            return;
        }

        // Row 1: Search + Status + Group + Sort
        ImGui.SetNextItemWidth(250f);
        ImGui.InputTextWithHint("##SearchFriends",
            L.T("Search name, nickname, world, FC, note...", "Buscar nome, apelido, mundo, FC, nota..."),
            ref searchQuery, 128);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        string[] statusOpts =
        [
            L.T("All Status", "Todos"),
            L.T("Online", "Online"),
            L.T("Offline", "Offline"),
            L.T("Protected ★", "Protegidos ★")
        ];
        ImGui.Combo("##StatusFilter", ref statusFilter, statusOpts, statusOpts.Length);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(155f);
        var groupLabel = groupFilterId switch
        {
            "__all__" => L.T("All Groups", "Todos os Grupos"),
            "__none__" => L.T("Ungrouped", "Sem Grupo"),
            _ => Plugin.Config.FindGroup(groupFilterId)?.Name ?? L.T("All Groups", "Todos os Grupos"),
        };
        if (ImGui.BeginCombo("##GroupFilter", groupLabel))
        {
            if (ImGui.Selectable(L.T("All Groups", "Todos os Grupos"), groupFilterId == "__all__"))
                groupFilterId = "__all__";
            if (ImGui.Selectable(L.T("Ungrouped", "Sem Grupo"), groupFilterId == "__none__"))
                groupFilterId = "__none__";
            ImGui.Separator();
            foreach (var g in Plugin.Config.Groups)
            {
                if (ImGui.Selectable(g.Name, groupFilterId == g.Id))
                    groupFilterId = g.Id;
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(145f);
        string[] sortOpts =
        [
            L.T("Sort: Online First", "Ordem: Online"),
            L.T("Sort: Name A-Z", "Ordem: Nome A-Z"),
            L.T("Sort: World", "Ordem: Servidor"),
            L.T("Sort: Last Seen", "Ordem: Visto por último"),
            L.T("Sort: Group", "Ordem: Grupo"),
        ];
        ImGui.Combo("##SortMode", ref sortMode, sortOpts, sortOpts.Length);

        // Apply filters
        var filtered = FilterAndSort(allFriends);

        // Row 2: Bulk Selection Toolbar
        ImGui.Spacing();
        if (ImGui.SmallButton(L.T("Select Visible", "Selecionar Visíveis")))
        {
            foreach (var f in filtered)
            {
                var meta = Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name);
                if (!meta.IsProtected) selectedIds.Add(f.ContentId);
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(L.T("Select Offline", "Selecionar Offline")))
        {
            foreach (var f in filtered)
            {
                if (f.IsOnline) continue;
                var meta = Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name);
                if (!meta.IsProtected) selectedIds.Add(f.ContentId);
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(L.T("Clear Selection", "Limpar Seleção")))
        {
            selectedIds.Clear();
        }

        ImGui.SameLine();
        ImGui.TextColored(Theme.TextMuted, $"  |  {L.T("Showing", "Mostrando")}: {filtered.Count}/{allFriends.Count}");

        if (selectedIds.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.YellowMain, $"  •  {selectedIds.Count} {L.T("selected", "selecionados")}");

            var removeBtnLabel = L.T($"Remove Selected ({selectedIds.Count})", $"Remover Selecionados ({selectedIds.Count})");
            var btnW = MathF.Max(190f, ImGui.CalcTextSize(removeBtnLabel).X + 24f);
            var rightPos = ImGui.GetContentRegionAvail().X - btnW;
            if (rightPos > 0) ImGui.SameLine(ImGui.GetCursorPosX() + rightPos);
            else ImGui.SameLine();

            ImGui.BeginDisabled(Plugin.BulkRemover.IsActive);
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedDangerDark);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.RedDanger);
            if (ImGui.Button(removeBtnLabel, new Vector2(btnW, 0)))
            {
                pendingBatchTargets = allFriends.Where(f => selectedIds.Contains(f.ContentId)).ToList();
                openConfirmModal = true;
            }
            ImGui.PopStyleColor(2);
            ImGui.EndDisabled();
        }

        ImGui.Spacing();

        // Friends Table
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##FriendsMainTable", 8, flags, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##Sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 28f);
            ImGui.TableSetupColumn(L.T("Status", "Status"), ImGuiTableColumnFlags.WidthFixed, 68f);
            ImGui.TableSetupColumn(L.T("Friend", "Amigo"), ImGuiTableColumnFlags.WidthStretch, 1.85f);
            ImGui.TableSetupColumn(L.T("World", "Servidor"), ImGuiTableColumnFlags.WidthStretch, 1.05f);
            ImGui.TableSetupColumn(L.T("Job / Location", "Classe / Local"), ImGuiTableColumnFlags.WidthStretch, 1.45f);
            ImGui.TableSetupColumn(L.T("Group", "Grupo"), ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn(L.T("Last Seen", "Visto"), ImGuiTableColumnFlags.WidthStretch, 0.85f);
            ImGui.TableSetupColumn(L.T("Actions", "Ações"), ImGuiTableColumnFlags.WidthFixed, 86f);
            ImGui.TableHeadersRow();

            foreach (var f in filtered)
            {
                var meta = Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name);
                var grp = Plugin.Config.FindGroup(meta.GroupId);

                ImGui.TableNextRow();
                ImGui.PushID((int)(f.ContentId & 0x7FFFFFFF) ^ f.RawIndex);

                // 1. Select checkbox (or Protected lock)
                ImGui.TableNextColumn();
                if (meta.IsProtected)
                {
                    ImGui.TextColored(Theme.YellowMain, " ★");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(L.T("Protected favorite — cannot be bulk-removed.", "Favorito protegido — não pode ser removido em massa."));
                }
                else
                {
                    var isSel = selectedIds.Contains(f.ContentId);
                    if (ImGui.Checkbox("##chk", ref isSel))
                    {
                        if (isSel) selectedIds.Add(f.ContentId);
                        else selectedIds.Remove(f.ContentId);
                    }
                }

                // 2. Online status
                ImGui.TableNextColumn();
                var (dotCol, stateLabel) = f.OnlineState switch
                {
                    FriendOnlineState.Online => (Theme.GreenOnline, L.T("Online", "Online")),
                    FriendOnlineState.InDuty => (Theme.CyanDuty, L.T("Duty", "Duty")),
                    FriendOnlineState.Away => (Theme.OrangeAway, L.T("AFK", "AFK")),
                    FriendOnlineState.Busy => (Theme.RedDanger, L.T("Busy", "Ocup.")),
                    _ => (Theme.TextMuted, L.T("Offline", "Offline")),
                };
                ImGui.TextColored(dotCol, $"● {stateLabel}");

                // 3. Name + Nickname + FC + Note indicator
                ImGui.TableNextColumn();
                var nameCol = meta.UseCustomColor
                    ? meta.CustomColor
                    : grp != null ? grp.Color : (f.IsOnline ? Theme.TextWhite : Theme.TextLight);

                var displayName = string.IsNullOrWhiteSpace(meta.Nickname)
                    ? f.Name
                    : $"{f.Name} ({meta.Nickname})";
                ImGui.TextColored(nameCol, displayName);
                if (!string.IsNullOrEmpty(f.FcTag))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.TextMuted, $"«{f.FcTag}»");
                }
                if (!string.IsNullOrWhiteSpace(meta.Note) && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{L.T("Note", "Nota")}: {meta.Note}");
                }

                // 4. World
                ImGui.TableNextColumn();
                ImGui.TextColored(f.IsOnline ? Theme.TextLight : Theme.TextMuted, f.WorldDisplay);

                // 5. Job / Location
                ImGui.TableNextColumn();
                if (f.IsOnline)
                {
                    var loc = string.IsNullOrEmpty(f.JobAbbrev)
                        ? f.LocationName
                        : string.IsNullOrEmpty(f.LocationName) ? f.JobAbbrev : $"[{f.JobAbbrev}] {f.LocationName}";
                    ImGui.TextColored(Theme.TextLight, loc);
                }
                else
                {
                    ImGui.TextColored(Theme.TextMuted, "-");
                }

                // 6. Group
                ImGui.TableNextColumn();
                if (grp != null)
                {
                    ImGui.TextColored(grp.Color, grp.Name);
                }
                else
                {
                    ImGui.TextColored(Theme.TextMuted, "-");
                }

                // 7. Last Seen
                ImGui.TableNextColumn();
                ImGui.TextColored(f.IsOnline ? Theme.GreenOnline : Theme.TextMuted,
                    L.FormatRelativeTime(meta.LastSeenOnlineUtc, f.IsOnline));

                // 8. Quick Actions (Protect star + Edit)
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, meta.IsProtected ? Theme.YellowMain : Theme.TextMuted);
                if (ImGui.SmallButton(meta.IsProtected ? "★" : "☆"))
                {
                    meta.IsProtected = !meta.IsProtected;
                    if (meta.IsProtected) selectedIds.Remove(f.ContentId);
                    Plugin.Config.Save();
                }
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(L.T("Toggle Protected Favorite", "Alternar Favorito Protegido"));

                ImGui.SameLine();
                if (ImGui.SmallButton(L.T("Edit", "Editar")))
                {
                    editingContentId = f.ContentId;
                    editFriendName = f.Name;
                    editNickname = meta.Nickname;
                    editNote = meta.Note;
                    editGroupId = meta.GroupId;
                    editProtected = meta.IsProtected;
                    editUseColor = meta.UseCustomColor;
                    editColor = meta.CustomColor;
                    openEditModal = true;
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void DrawBulkRemoveTab()
    {
        var br = Plugin.BulkRemover;

        ImGui.TextColored(Theme.YellowMain, L.T("Bulk Friend Removal Engine", "Motor de Remoção em Massa"));
        ImGui.TextWrapped(L.T(
            "Select multiple friends in the 'Friends' tab using the checkboxes, then click 'Remove Selected'. " +
            "FriendList Frenzy will automatically open the native Friend List, trigger 'Remove from Friend List', verify the confirmation prompt matches the exact friend name, and confirm one-by-one at a safe server-friendly pace.",
            "Selecione vários amigos na aba 'Amigos' usando as caixas de seleção e clique em 'Remover Selecionados'. " +
            "O FriendList Frenzy abrirá a Lista de Amigos nativa, acionará 'Remover da Lista de Amigos', verificará se o diálogo de confirmação corresponde ao nome exato do alvo e confirmará um por um em um ritmo seguro."));

        ImGui.Spacing();
        var delaySec = Plugin.Config.RemovalDelayMs / 1000f;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.SliderFloat(L.T("Delay between removals (seconds)", "Intervalo entre remoções (segundos)"), ref delaySec, 0.8f, 4.0f, "%.1fs"))
        {
            Plugin.Config.RemovalDelayMs = (int)(delaySec * 1000);
            Plugin.Config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Theme.YellowMain, L.T("Removal Activity Log", "Histórico de Remoções"));

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##BulkLogTable", 4, flags, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(L.T("Time", "Hora"), ImGuiTableColumnFlags.WidthFixed, 76f);
            ImGui.TableSetupColumn(L.T("Friend", "Amigo"), ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn(L.T("Result", "Resultado"), ImGuiTableColumnFlags.WidthFixed, 84f);
            ImGui.TableSetupColumn(L.T("Details", "Detalhes"), ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableHeadersRow();

            foreach (var item in br.RecentLog)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextColored(Theme.TextMuted, item.TimeLocal.ToString("HH:mm:ss"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(item.Name);
                ImGui.TableNextColumn();
                ImGui.TextColored(item.Success ? Theme.GreenOnline : Theme.RedDanger,
                    item.Success ? L.T("Removed", "Removido") : L.T("Skipped", "Ignorado"));
                ImGui.TableNextColumn(); ImGui.TextColored(Theme.TextMuted, item.Message);
            }
            ImGui.EndTable();
        }
    }

    private void DrawGroupsTab()
    {
        ImGui.TextColored(Theme.YellowMain, L.T("Create Custom Group", "Criar Grupo Personalizado"));
        ImGui.SetNextItemWidth(260f);
        ImGui.InputTextWithHint("##NewGroupName", L.T("Group name (e.g. Static, RP, Inactive)...", "Nome do grupo (ex: Static, RP, Inativos)..."), ref newGroupName, 40);
        ImGui.SameLine();
        ImGui.ColorEdit4("##NewGroupColor", ref newGroupColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha);
        ImGui.SameLine();
        if (ImGui.Button(L.T("Add Group", "Adicionar Grupo")))
        {
            var trimmed = newGroupName.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                Plugin.Config.Groups.Add(new FriendGroup
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    Name = trimmed,
                    Color = newGroupColor,
                });
                Plugin.Config.Save();
                newGroupName = string.Empty;
            }
        }

        ImGui.Spacing();
        ImGui.Separator();

        var allFriends = Plugin.Friends.GetSnapshot();
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##GroupsTable", 4, flags, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(L.T("Color", "Cor"), ImGuiTableColumnFlags.WidthFixed, 54f);
            ImGui.TableSetupColumn(L.T("Group Name", "Nome do Grupo"), ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn(L.T("Members", "Membros"), ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn(L.T("Actions", "Ações"), ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableHeadersRow();

            string? toDeleteId = null;
            for (var i = 0; i < Plugin.Config.Groups.Count; i++)
            {
                var grp = Plugin.Config.Groups[i];
                var memberCount = allFriends.Count(f =>
                    Plugin.Config.FriendMetadata.TryGetValue(f.ContentId, out var m) && m.GroupId == grp.Id);

                ImGui.TableNextRow();
                ImGui.PushID(grp.Id);

                ImGui.TableNextColumn();
                var col = grp.Color;
                if (ImGui.ColorEdit4("##gcol", ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                {
                    grp.Color = col;
                    Plugin.Config.Save();
                }

                ImGui.TableNextColumn();
                var gname = grp.Name;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##gname", ref gname, 40))
                {
                    grp.Name = gname;
                    Plugin.Config.Save();
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(memberCount.ToString());

                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedDangerDark);
                if (ImGui.SmallButton(L.T("Delete", "Excluir")))
                    toDeleteId = grp.Id;
                ImGui.PopStyleColor();

                ImGui.PopID();
            }

            if (toDeleteId != null)
            {
                Plugin.Config.Groups.RemoveAll(g => g.Id == toDeleteId);
                foreach (var meta in Plugin.Config.FriendMetadata.Values)
                {
                    if (meta.GroupId == toDeleteId) meta.GroupId = string.Empty;
                }
                if (groupFilterId == toDeleteId) groupFilterId = "__all__";
                Plugin.Config.Save();
            }

            ImGui.EndTable();
        }
    }

    private void DrawSettingsTab()
    {
        ImGui.TextColored(Theme.YellowMain, L.T("General Settings", "Configurações Gerais"));
        ImGui.Separator();

        var isPt = L.IsPt;
        ImGui.TextUnformatted(L.T("Interface Language:", "Idioma da Interface:"));
        ImGui.SameLine();
        if (ImGui.RadioButton("English (Default)", !isPt))
        {
            Plugin.Config.Language = "en";
            Plugin.Config.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Português (BR)", isPt))
        {
            Plugin.Config.Language = "pt";
            Plugin.Config.Save();
        }

        ImGui.Spacing();
        var closeAfter = Plugin.Config.CloseNativeListAfterBatch;
        if (ImGui.Checkbox(L.T("Close native Friend List window after bulk removal finishes", "Fechar janela nativa Friend List após concluir remoção em massa"), ref closeAfter))
        {
            Plugin.Config.CloseNativeListAfterBatch = closeAfter;
            Plugin.Config.Save();
        }

        var chatSummary = Plugin.Config.PrintChatSummary;
        if (ImGui.Checkbox(L.T("Print summary in chat when bulk removal finishes", "Mostrar resumo no chat ao concluir remoção em massa"), ref chatSummary))
        {
            Plugin.Config.PrintChatSummary = chatSummary;
            Plugin.Config.Save();
        }

        var delaySec = Plugin.Config.RemovalDelayMs / 1000f;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.SliderFloat(L.T("Bulk removal delay", "Intervalo de remoção"), ref delaySec, 0.8f, 4.0f, "%.1fs"))
        {
            Plugin.Config.RemovalDelayMs = (int)(delaySec * 1000);
            Plugin.Config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Theme.YellowMain, L.T("Commands & Safety", "Comandos & Segurança"));
        ImGui.BulletText("/flf  |  /friendlistfrenzy — " + L.T("Toggle FriendList Frenzy window", "Abrir/fechar a janela do FriendList Frenzy"));
        ImGui.BulletText(L.T(
            "Star (★) any friend to protect them so they can never be selected for bulk removal.",
            "Clique na estrela (★) para proteger qualquer amigo e impedir que seja selecionado na remoção em massa."));
        ImGui.BulletText(L.T(
            "Every bulk removal checks the exact friend name in FFXIV's confirmation dialog before clicking Yes.",
            "Cada remoção em massa confere o nome exato do amigo no diálogo do jogo antes de confirmar Sim."));
    }

    private void DrawEditFriendModal()
    {
        if (openEditModal)
        {
            ImGui.OpenPopup("##EditFriendPopup");
            openEditModal = false;
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(440, 320), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal(L.T("Edit Friend##EditFriendPopup", "Editar Amigo##EditFriendPopup"), ref open, ImGuiWindowFlags.NoResize))
        {
            ImGui.TextColored(Theme.YellowMain, editFriendName);
            ImGui.Separator();

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##Nick", L.T("Nickname (optional)...", "Apelido (opcional)..."), ref editNickname, 48);

            // Group selector
            var curGrpName = string.IsNullOrEmpty(editGroupId)
                ? L.T("No Group", "Sem Grupo")
                : Plugin.Config.FindGroup(editGroupId)?.Name ?? L.T("No Group", "Sem Grupo");

            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##EditGrp", curGrpName))
            {
                if (ImGui.Selectable(L.T("No Group", "Sem Grupo"), string.IsNullOrEmpty(editGroupId)))
                    editGroupId = string.Empty;
                foreach (var g in Plugin.Config.Groups)
                {
                    if (ImGui.Selectable(g.Name, editGroupId == g.Id))
                        editGroupId = g.Id;
                }
                ImGui.EndCombo();
            }

            ImGui.Checkbox(L.T("Protected Favorite (prevent bulk removal)", "Favorito Protegido (impede remoção em massa)"), ref editProtected);
            ImGui.Checkbox(L.T("Custom Name Color", "Cor Personalizada no Nome"), ref editUseColor);
            if (editUseColor)
            {
                ImGui.SameLine();
                ImGui.ColorEdit4("##EditCol", ref editColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha);
            }

            ImGui.TextColored(Theme.TextMuted, L.T("Personal Note:", "Nota Pessoal:"));
            ImGui.InputTextMultiline("##EditNote", ref editNote, 256, new Vector2(-1, 76f));

            ImGui.Spacing();
            if (ImGui.Button(L.T("Save", "Salvar"), new Vector2(120, 0)))
            {
                var meta = Plugin.Config.GetOrCreateMeta(editingContentId, editFriendName);
                meta.Nickname = editNickname.Trim();
                meta.Note = editNote.Trim();
                meta.GroupId = editGroupId;
                meta.IsProtected = editProtected;
                meta.UseCustomColor = editUseColor;
                meta.CustomColor = editColor;
                if (meta.IsProtected) selectedIds.Remove(editingContentId);
                Plugin.Config.Save();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(L.T("Cancel", "Cancelar"), new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawConfirmBulkModal()
    {
        if (openConfirmModal)
        {
            ImGui.OpenPopup("##ConfirmBulkPopup");
            openConfirmModal = false;
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(480, 360), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal(L.T("Confirm Bulk Friend Removal##ConfirmBulkPopup", "Confirmar Remoção em Massa##ConfirmBulkPopup"), ref open, ImGuiWindowFlags.NoResize))
        {
            ImGui.TextColored(Theme.RedDanger, L.T(
                $"You are about to remove {pendingBatchTargets.Count} friend(s) from your Friend List:",
                $"Você está prestes a remover {pendingBatchTargets.Count} amigo(s) da sua Lista de Amigos:"));
            ImGui.TextColored(Theme.TextMuted, L.T(
                "Please avoid manually clicking game menus while the queue runs. You can Pause or Cancel anytime.",
                "Evite clicar manualmente em menus do jogo enquanto a fila roda. Você pode Pausar ou Cancelar a qualquer momento."));

            ImGui.Spacing();
            ImGui.BeginChild("##ConfirmList", new Vector2(0, 185f), true);
            foreach (var t in pendingBatchTargets)
            {
                ImGui.BulletText($"{t.Name}  ({t.HomeWorldName})");
            }
            ImGui.EndChild();

            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedDangerDark);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.RedDanger);
            if (ImGui.Button(L.T($"Yes, Remove {pendingBatchTargets.Count} Friends", $"Sim, Remover {pendingBatchTargets.Count} Amigos"), new Vector2(240, 0)))
            {
                Plugin.BulkRemover.StartBatch(pendingBatchTargets);
                selectedIds.Clear();
                ImGui.CloseCurrentPopup();
            }
            ImGui.PopStyleColor(2);

            ImGui.SameLine();
            if (ImGui.Button(L.T("Cancel", "Cancelar"), new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private List<FriendEntry> FilterAndSort(List<FriendEntry> source)
    {
        var q = searchQuery.Trim();
        IEnumerable<FriendEntry> result = source;

        if (statusFilter == 1) result = result.Where(f => f.IsOnline);
        else if (statusFilter == 2) result = result.Where(f => !f.IsOnline);
        else if (statusFilter == 3) result = result.Where(f => Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name).IsProtected);

        if (groupFilterId == "__none__")
        {
            result = result.Where(f => string.IsNullOrEmpty(Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name).GroupId));
        }
        else if (groupFilterId != "__all__")
        {
            result = result.Where(f => Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name).GroupId == groupFilterId);
        }

        if (!string.IsNullOrEmpty(q))
        {
            result = result.Where(f =>
            {
                var meta = Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name);
                return f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || f.HomeWorldName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || f.CurrentWorldName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || f.FcTag.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || f.LocationName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || meta.Nickname.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || meta.Note.Contains(q, StringComparison.OrdinalIgnoreCase);
            });
        }

        return sortMode switch
        {
            1 => result.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            2 => result.OrderBy(f => f.HomeWorldName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            3 => result.OrderByDescending(f => f.IsOnline)
                       .ThenByDescending(f => Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name).LastSeenOnlineUtc ?? DateTime.MinValue)
                       .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                       .ToList(),
            4 => result.OrderBy(f => Plugin.Config.FindGroup(Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name).GroupId)?.Name ?? "zzz", StringComparer.OrdinalIgnoreCase)
                       .ThenByDescending(f => f.IsOnline)
                       .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                       .ToList(),
            _ => result.OrderByDescending(f => f.IsOnline)
                       .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                       .ToList(),
        };
    }
}
