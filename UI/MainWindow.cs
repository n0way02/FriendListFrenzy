using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FriendListFrenzy.Models;
using FriendListFrenzy.Services;

namespace FriendListFrenzy.UI;

public sealed class MainWindow : Window
{
    private const string KofiUrl = "https://ko-fi.com/n0way02";
    private const string EditModalId = "###EditFriendModal";
    private const string ConfirmModalId = "###ConfirmBulkModal";

    private readonly HashSet<ulong> selectedIds = new();
    private string searchQuery = string.Empty;
    private int statusFilter; // 0=All, 1=Online, 2=Offline, 3=Protected
    private int nativeGroupFilter = -1; // -1=All, 0=Ungrouped, 1..7=NativeGroup
    private int sortMode; // 0=Online+Name, 1=Name, 2=World, 3=LastSeen, 4=NativeGroup
    private byte bulkTargetGroup = NativeFriendGroups.Star;

    // Cross-tab jump helper (from Native Groups tab -> Friends tab)
    private bool requestFocusFriendsTab;

    // Groups tab "Move All To" target per source group (1..7)
    private readonly byte[] moveAllTargets = [0, 2, 1, 1, 1, 1, 1, 1];

    // Edit Friend modal state
    private bool openEditModal;
    private ulong editingContentId;
    private string editFriendName = string.Empty;
    private string editNickname = string.Empty;
    private string editNote = string.Empty;
    private byte editNativeGroup;
    private byte editOriginalNativeGroup;
    private bool editProtected;
    private bool editUseColor;
    private Vector4 editColor = Theme.YellowMain;

    // Confirm Bulk Action modal state
    private bool openConfirmModal;
    private BulkActionKind pendingBatchKind = BulkActionKind.None;
    private byte pendingBatchGroup;
    private List<FriendEntry> pendingBatchTargets = new();

    private int pushedColors;
    private int pushedVars;

    public MainWindow()
        : base("FriendList Frenzy###FriendListFrenzyMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        Size = new Vector2(960, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 500),
            MaximumSize = new Vector2(1800, 1200),
        };
    }

    public override void PreDraw()
    {
        pushedColors = Theme.PushTheme();
        pushedVars = Theme.PushStyleVars();
    }

    public override void PostDraw()
    {
        if (pushedVars > 0) ImGui.PopStyleVar(pushedVars);
        if (pushedColors > 0) ImGui.PopStyleColor(pushedColors);
        pushedVars = 0;
        pushedColors = 0;
    }

    public override void Draw()
    {
        DrawHeaderBar();
        DrawActiveBatchBanner();

        if (ImGui.BeginTabBar("##FlfMainTabs"))
        {
            var friendsTabFlags = requestFocusFriendsTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            requestFocusFriendsTab = false;

            if (ImGui.BeginTabItem(L.T("Friends###TabFriends", "Amigos###TabFriends"), friendsTabFlags))
            {
                DrawFriendsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(L.T("Native Groups###TabGroups", "Grupos do Jogo###TabGroups")))
            {
                DrawGroupsTab();
                ImGui.EndTabItem();
            }

            var queueLabel = Plugin.BulkRemover.IsRunning
                ? L.T($"Bulk Queue ({Plugin.BulkRemover.RemainingCount})###TabQueue", $"Fila ({Plugin.BulkRemover.RemainingCount})###TabQueue")
                : L.T("Bulk Queue & Log###TabQueue", "Fila & Log###TabQueue");

            if (ImGui.BeginTabItem(queueLabel))
            {
                DrawBulkQueueTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem(L.T("Settings###TabSettings", "Configurações###TabSettings")))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        DrawEditFriendModal();
        DrawConfirmBulkModal();
    }

    private void DrawHeaderBar()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var friends = Plugin.Friends.Friends;
        var online = friends.Count(f => f.IsOnline);
        var total = friends.Count;
        var grouped = friends.Count(f => f.NativeGroup > 0);
        var protectedCount = friends.Count(f => Plugin.Config.FriendsMeta.TryGetValue(f.ContentId, out var m) && m.IsProtected);

        // Brand title + stat pills on the left
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.YellowMain, "FRIENDLIST FRENZY");
        ImGui.SameLine(0, 12f * scale);
        UiControls.DrawPillBadge(L.T($"● {online} Online", $"● {online} Online"), Theme.GreenOnline, true);
        ImGui.SameLine(0, 6f * scale);
        UiControls.DrawPillBadge(L.T($"{total} Total", $"{total} Total"), Theme.TextLight);
        ImGui.SameLine(0, 6f * scale);
        UiControls.DrawPillBadge(L.T($"★ {grouped} Grouped", $"★ {grouped} em Grupos"), Theme.PurpleBright);
        if (protectedCount > 0)
        {
            ImGui.SameLine(0, 6f * scale);
            UiControls.DrawPillBadge(L.T($"♥ {protectedCount} Protected", $"♥ {protectedCount} Protegidos"), Theme.YellowMain);
        }

        // Right-aligned utility buttons: Refresh | EN/PT-BR | ♥ Support Me
        var refreshText = L.T("Refresh", "Atualizar");
        var langText = L.IsPt ? "PT-BR" : "EN";
        var kofiText = L.T("♥ Support Me", "♥ Apoie");

        var padX = ImGui.GetStyle().FramePadding.X * 2f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var refreshW = ImGui.CalcTextSize(refreshText).X + padX + 4f * scale;
        var langW = ImGui.CalcTextSize(langText).X + padX + 4f * scale;
        var kofiW = ImGui.CalcTextSize(kofiText).X + padX + 6f * scale;
        var totalRightW = refreshW + langW + kofiW + spacing * 2f;

        var rightX = ImGui.GetWindowContentRegionMax().X - totalRightW;
        if (rightX > ImGui.GetCursorPosX())
            ImGui.SameLine(rightX);
        else
            ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, Theme.BgSurface);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.BgElevated);
        if (ImGui.Button($"{refreshText}##HdrRefresh", new Vector2(refreshW, 0)))
        {
            Plugin.Friends.RequestRefresh();
            Plugin.Friends.RefreshFromMemory();
        }
        ImGui.SameLine();
        if (ImGui.Button($"{langText}##HdrLang", new Vector2(langW, 0)))
        {
            Plugin.Config.Language = L.IsPt ? "en" : "pt";
            Plugin.Config.Save();
        }
        ImGui.PopStyleColor(2);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(L.T("Switch language (EN / PT-BR)", "Alternar idioma (EN / PT-BR)"));

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.YellowMain);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 0.94f, 0.35f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.BgDark);
        if (ImGui.Button($"{kofiText}##HdrKofi", new Vector2(kofiW, 0)))
        {
            try { Process.Start(new ProcessStartInfo(KofiUrl) { UseShellExecute = true }); }
            catch { /* ignore */ }
        }
        ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(KofiUrl);

        ImGui.Dummy(new Vector2(0, 4f * scale));
    }

    private void DrawActiveBatchBanner()
    {
        var bulk = Plugin.BulkRemover;
        if (!bulk.IsRunning) return;

        var scale = ImGuiHelpers.GlobalScale;
        var accent = bulk.CurrentBatchKind == BulkActionKind.RemoveFriend ? Theme.RedDanger : Theme.YellowMain;

        using (UiCard.Begin("", new Vector4(0.14f, 0.04f, 0.28f, 1f), accent))
        {
            var processed = bulk.SucceededCount + bulk.FailedCount;
            var total = Math.Max(1, bulk.TotalInBatch);
            var progress = Math.Clamp((float)processed / total, 0f, 1f);

            ImGui.TextColored(accent, $"● {bulk.GetBatchActionLabel(bulk.CurrentBatchKind, bulk.CurrentBatchTargetGroup)}");
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextLight, $"  {bulk.StatusMessage}");

            var btnW = 86f * scale;
            var rightX = UiCard.ContentRightEdge - ImGui.GetWindowPos().X - (btnW * 2f + 8f * scale);
            if (rightX > ImGui.GetCursorPosX())
                ImGui.SameLine(rightX);
            else
                ImGui.SameLine();

            if (ImGui.Button(bulk.IsPaused ? L.T("Resume", "Retomar") : L.T("Pause", "Pausar"), new Vector2(btnW, 0)))
                bulk.TogglePause();
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedDangerDark);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.RedDanger);
            if (ImGui.Button(L.T("Cancel", "Cancelar"), new Vector2(btnW, 0)))
                bulk.Cancel();
            ImGui.PopStyleColor(2);

            ImGui.ProgressBar(progress, new Vector2(UiCard.InnerWidth, 16f * scale),
                $"{processed}/{bulk.TotalInBatch}  (OK: {bulk.SucceededCount}, {L.T("Err", "Falha")}: {bulk.FailedCount})");
        }
    }

    private void DrawFriendsTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var allFriends = Plugin.Friends.Friends.ToList();

        // Prune stale selections if friends were removed
        if (selectedIds.Count > 0)
        {
            var liveIds = allFriends.Select(f => f.ContentId).ToHashSet();
            selectedIds.RemoveWhere(id => !liveIds.Contains(id));
        }

        if (!Plugin.ClientState.IsLoggedIn)
        {
            ImGui.Spacing();
            using (UiCard.Begin())
            {
                UiControls.InsetNote(L.T("Log in to a character to view and manage your Friend List.", "Entre em um personagem para visualizar e gerenciar sua Lista de Amigos."), Theme.YellowMain);
            }
            return;
        }

        // ── Card 1: Search, Filters & Sort ──
        using (UiCard.Begin())
        {
            var availW = UiCard.InnerWidth;

            // Row 1: Search + Native Group Filter + Sort
            var groupComboW = 195f * scale;
            var sortComboW = 155f * scale;
            var searchW = MathF.Max(160f * scale, availW - groupComboW - sortComboW - 16f * scale);

            ImGui.SetNextItemWidth(searchW);
            ImGui.InputTextWithHint("##FlfSearch", L.T("Search name, nickname, world, FC, location, note...", "Buscar nome, apelido, mundo, FC, local, nota..."), ref searchQuery, 128);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(groupComboW);
            var groupPreview = nativeGroupFilter switch
            {
                -1 => L.T($"All Groups ({allFriends.Count})", $"Todos os Grupos ({allFriends.Count})"),
                0 => L.T($"○ Ungrouped ({allFriends.Count(f => f.NativeGroup == 0)})", $"○ Sem Grupo ({allFriends.Count(f => f.NativeGroup == 0)})"),
                _ => $"{Plugin.Config.GetNativeGroupLabel((byte)nativeGroupFilter)} ({allFriends.Count(f => f.NativeGroup == nativeGroupFilter)})",
            };

            if (ImGui.BeginCombo("##FlfGroupFilter", groupPreview))
            {
                if (ImGui.Selectable(L.T($"All Groups ({allFriends.Count})", $"Todos os Grupos ({allFriends.Count})"), nativeGroupFilter == -1))
                    nativeGroupFilter = -1;
                if (ImGui.Selectable(L.T($"○ Ungrouped ({allFriends.Count(f => f.NativeGroup == 0)})", $"○ Sem Grupo ({allFriends.Count(f => f.NativeGroup == 0)})"), nativeGroupFilter == 0))
                    nativeGroupFilter = 0;

                ImGui.Separator();
                foreach (var g in NativeFriendGroups.All)
                {
                    var cnt = allFriends.Count(f => f.NativeGroup == g.Id);
                    var col = Plugin.Config.GetNativeGroupColor(g.Id);
                    ImGui.PushStyleColor(ImGuiCol.Text, col);
                    if (ImGui.Selectable($"{Plugin.Config.GetNativeGroupLabel(g.Id)} ({cnt})##gf_{g.Id}", nativeGroupFilter == g.Id))
                        nativeGroupFilter = g.Id;
                    ImGui.PopStyleColor();
                }
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(sortComboW);
            string[] sortLabels =
            [
                L.T("Sort: Online", "Ordem: Online"),
                L.T("Sort: Name A-Z", "Ordem: Nome A-Z"),
                L.T("Sort: World", "Ordem: Mundo"),
                L.T("Sort: Last Seen", "Ordem: Visto por último"),
                L.T("Sort: Game Group", "Ordem: Grupo do Jogo"),
            ];
            ImGui.Combo("##FlfSort", ref sortMode, sortLabels, sortLabels.Length);

            // Row 2: Status Filter Pills + Clear Filters
            ImGui.Dummy(new Vector2(0, 2f * scale));
            DrawFilterPill(L.T("All", "Todos"), 0);
            ImGui.SameLine();
            DrawFilterPill(L.T("Online", "Online"), 1);
            ImGui.SameLine();
            DrawFilterPill(L.T("Offline", "Offline"), 2);
            ImGui.SameLine();
            DrawFilterPill(L.T("★ Protected", "★ Protegidos"), 3);

            if (!string.IsNullOrEmpty(searchQuery) || statusFilter != 0 || nativeGroupFilter != -1)
            {
                ImGui.SameLine(0, 12f * scale);
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.BgSurface);
                if (ImGui.SmallButton(L.T("× Reset Filters", "× Limpar Filtros")))
                {
                    searchQuery = string.Empty;
                    statusFilter = 0;
                    nativeGroupFilter = -1;
                }
                ImGui.PopStyleColor();
            }
        }

        var filtered = FilterAndSort(allFriends);

        // ── Card 2: Bulk Operations Toolbar (synced with FFXIV Friend List!) ──
        using (UiCard.Begin("", Theme.BgSurface, selectedIds.Count > 0 ? Theme.YellowMain : Theme.BorderSoft))
        {
            var isBusy = Plugin.BulkRemover.IsRunning;

            // Selection controls
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.BgPanel);
            if (ImGui.Button(L.T($"Select Visible ({filtered.Count})", $"Selecionar Visíveis ({filtered.Count})")))
            {
                foreach (var f in filtered)
                    selectedIds.Add(f.ContentId);
            }
            ImGui.SameLine();
            if (ImGui.Button(L.T("Clear", "Limpar")))
                selectedIds.Clear();
            ImGui.PopStyleColor();

            ImGui.SameLine(0, 10f * scale);
            UiControls.DrawPillBadge(
                L.T($"{selectedIds.Count} selected", $"{selectedIds.Count} selecionado(s)"),
                selectedIds.Count > 0 ? Theme.YellowMain : Theme.TextMuted,
                selectedIds.Count > 0);

            ImGui.SameLine(0, 12f * scale);

            // Bulk In-Game Group Assignment
            ImGui.BeginDisabled(selectedIds.Count == 0 || isBusy);

            ImGui.SetNextItemWidth(155f * scale);
            var targetLabel = Plugin.Config.GetNativeGroupLabel(bulkTargetGroup);
            ImGui.PushStyleColor(ImGuiCol.Text, Plugin.Config.GetNativeGroupColor(bulkTargetGroup));
            var openedGroupCombo = ImGui.BeginCombo("##BulkGroupPick", targetLabel);
            ImGui.PopStyleColor();
            if (openedGroupCombo)
            {
                foreach (var g in NativeFriendGroups.All)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Plugin.Config.GetNativeGroupColor(g.Id));
                    if (ImGui.Selectable($"{Plugin.Config.GetNativeGroupLabel(g.Id)}##bg_{g.Id}", bulkTargetGroup == g.Id))
                        bulkTargetGroup = g.Id;
                    ImGui.PopStyleColor();
                }
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.Button(L.T("Set Game Group", "Definir Grupo")))
            {
                var targets = allFriends.Where(f => selectedIds.Contains(f.ContentId)).ToList();
                if (targets.Count > 0)
                {
                    pendingBatchKind = BulkActionKind.SetNativeGroup;
                    pendingBatchGroup = bulkTargetGroup;
                    pendingBatchTargets = targets;
                    openConfirmModal = true;
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L.T("Assigns or moves selected friends into this native FFXIV Friend Group.", "Adiciona ou move os amigos selecionados para este Grupo nativo no FFXIV."));

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.BgPanel);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.BgElevated);
            if (ImGui.Button(L.T("Clear Group", "Tirar do Grupo")))
            {
                var targets = allFriends.Where(f => selectedIds.Contains(f.ContentId) && f.NativeGroup > 0).ToList();
                if (targets.Count > 0)
                {
                    pendingBatchKind = BulkActionKind.ClearNativeGroup;
                    pendingBatchGroup = 0;
                    pendingBatchTargets = targets;
                    openConfirmModal = true;
                }
            }
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L.T("Removes selected friends from their native FFXIV Friend Group.", "Remove os amigos selecionados do Grupo nativo no FFXIV."));

            ImGui.SameLine(0, 10f * scale);
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedDangerDark);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.RedDanger);
            if (ImGui.Button(L.T($"Remove ({selectedIds.Count})", $"Remover ({selectedIds.Count})")))
            {
                var targets = allFriends.Where(f =>
                {
                    if (!selectedIds.Contains(f.ContentId)) return false;
                    var m = Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name);
                    return !m.IsProtected;
                }).ToList();

                if (targets.Count > 0)
                {
                    pendingBatchKind = BulkActionKind.RemoveFriend;
                    pendingBatchGroup = 0;
                    pendingBatchTargets = targets;
                    openConfirmModal = true;
                }
            }
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(L.T("Safely removes selected non-protected friends from your FFXIV Friend List.", "Remove com segurança os amigos selecionados (não protegidos) da Lista de Amigos."));

            ImGui.EndDisabled();
        }

        // ── Friends Table ──
        if (allFriends.Count == 0)
        {
            using (UiCard.Begin())
            {
                UiControls.InsetNote(L.T(
                    "Friend List has not loaded yet. Click 'Refresh' or open your in-game Friend List (O) once.",
                    "A Lista de Amigos ainda não carregou. Clique em 'Atualizar' ou abra a Lista de Amigos do jogo (O) uma vez."), Theme.YellowMain);
            }
            return;
        }

        var showLastSeen = Plugin.Config.ShowLastSeen;
        var colCount = showLastSeen ? 7 : 6;
        var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;

        var tableAvailH = MathF.Max(180f * scale, ImGui.GetContentRegionAvail().Y - 24f * scale);
        if (ImGui.BeginTable("##FlfFriendsTable", colCount, tableFlags, new Vector2(0, tableAvailH)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##Sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 28f * scale);
            ImGui.TableSetupColumn("★##Prot", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 28f * scale);
            ImGui.TableSetupColumn(L.T("Friend", "Amigo"), ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn(L.T("Game Group", "Grupo no Jogo"), ImGuiTableColumnFlags.WidthStretch, 1.15f);
            ImGui.TableSetupColumn(L.T("World / Status", "Mundo / Status"), ImGuiTableColumnFlags.WidthStretch, 1.35f);
            if (showLastSeen)
                ImGui.TableSetupColumn(L.T("Last Seen", "Visto"), ImGuiTableColumnFlags.WidthFixed, 92f * scale);
            ImGui.TableSetupColumn("##Actions", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 58f * scale);
            ImGui.TableHeadersRow();

            foreach (var f in filtered)
            {
                var meta = Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name);
                ImGui.PushID((int)(f.ContentId ^ (f.ContentId >> 32)));
                ImGui.TableNextRow();

                // Col 0: Selection Checkbox
                ImGui.TableNextColumn();
                var isSelected = selectedIds.Contains(f.ContentId);
                if (ImGui.Checkbox("##chk", ref isSelected))
                {
                    if (isSelected) selectedIds.Add(f.ContentId);
                    else selectedIds.Remove(f.ContentId);
                }

                // Col 1: Protected Favorite Toggle (★)
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.PurpleSoft);
                ImGui.PushStyleColor(ImGuiCol.Text, meta.IsProtected ? Theme.YellowMain : Theme.TextMuted);
                if (ImGui.SmallButton(meta.IsProtected ? "★##fav" : "☆##fav"))
                {
                    meta.IsProtected = !meta.IsProtected;
                    Plugin.Config.Save();
                }
                ImGui.PopStyleColor(3);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(meta.IsProtected
                        ? L.T("Protected favorite (blocked from bulk removal). Click to unprotect.", "Favorito protegido (bloqueado contra remoção em massa). Clique para desproteger.")
                        : L.T("Click to protect from bulk removal.", "Clique para proteger contra remoção em massa."));

                // Col 2: Status Dot + Name + Nickname + FC Tag + ContextMenu
                ImGui.TableNextColumn();
                var dotColor = !f.IsOnline ? Theme.TextMuted
                    : f.IsInDuty ? Theme.CyanDuty
                    : f.IsAway ? Theme.OrangeAway
                    : Theme.GreenOnline;

                ImGui.TextColored(dotColor, f.IsOnline ? "●" : "○");
                ImGui.SameLine(0, 6f * scale);

                var nameColor = meta.UseCustomColor ? meta.CustomColor
                    : f.IsOnline ? Theme.TextWhite : Theme.TextLight;

                ImGui.TextColored(nameColor, f.Name);

                if (Plugin.Config.ShowFcTag && !string.IsNullOrEmpty(f.FcTag))
                {
                    ImGui.SameLine(0, 5f * scale);
                    ImGui.TextColored(Theme.TextMuted, $"«{f.FcTag}»");
                }

                if (Plugin.Config.ShowNickname && !string.IsNullOrEmpty(meta.Nickname))
                {
                    ImGui.SameLine(0, 6f * scale);
                    ImGui.TextColored(Theme.YellowMain, $"({meta.Nickname})");
                }

                if (!string.IsNullOrEmpty(meta.Note))
                {
                    ImGui.SameLine(0, 6f * scale);
                    ImGui.TextColored(Theme.PurpleBright, "[i]");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(meta.Note);
                }

                // Right-click row context menu for 1-click native group sync!
                if (ImGui.BeginPopupContextItem("##RowCtx"))
                {
                    ImGui.TextColored(Theme.YellowMain, f.Name);
                    ImGui.Separator();

                    ImGui.BeginDisabled(Plugin.BulkRemover.IsRunning);
                    if (ImGui.BeginMenu(L.T("Set In-Game Group", "Definir Grupo no Jogo")))
                    {
                        foreach (var g in NativeFriendGroups.All)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, Plugin.Config.GetNativeGroupColor(g.Id));
                            var isCurrent = f.NativeGroup == g.Id;
                            if (ImGui.MenuItem(Plugin.Config.GetNativeGroupLabel(g.Id), string.Empty, isCurrent))
                            {
                                if (!isCurrent)
                                    Plugin.BulkRemover.StartSetGroupBatch([f], g.Id);
                            }
                            ImGui.PopStyleColor();
                        }
                        ImGui.EndMenu();
                    }

                    if (f.NativeGroup > 0)
                    {
                        if (ImGui.MenuItem(L.T("Remove from In-Game Group", "Remover do Grupo no Jogo")))
                            Plugin.BulkRemover.StartClearGroupBatch([f]);
                    }
                    ImGui.EndDisabled();

                    ImGui.Separator();
                    if (ImGui.MenuItem(L.T("Edit Note / Nickname", "Editar Nota / Apelido")))
                        OpenEditModalFor(f, meta);

                    if (ImGui.MenuItem(meta.IsProtected ? L.T("Unprotect Favorite", "Desproteger Favorito") : L.T("Protect Favorite ★", "Proteger Favorito ★")))
                    {
                        meta.IsProtected = !meta.IsProtected;
                        Plugin.Config.Save();
                    }

                    if (!meta.IsProtected && !Plugin.BulkRemover.IsRunning)
                    {
                        ImGui.Separator();
                        ImGui.PushStyleColor(ImGuiCol.Text, Theme.RedDanger);
                        if (ImGui.MenuItem(L.T("Remove from Friend List", "Remover da Lista de Amigos")))
                        {
                            pendingBatchKind = BulkActionKind.RemoveFriend;
                            pendingBatchGroup = 0;
                            pendingBatchTargets = [f];
                            openConfirmModal = true;
                        }
                        ImGui.PopStyleColor();
                    }

                    ImGui.EndPopup();
                }

                // Col 3: Native In-Game Group Badge
                ImGui.TableNextColumn();
                if (f.NativeGroup > 0)
                {
                    UiControls.DrawPillBadge(
                        Plugin.Config.GetNativeGroupLabel(f.NativeGroup),
                        Plugin.Config.GetNativeGroupColor(f.NativeGroup));
                }
                else
                {
                    ImGui.TextColored(Theme.TextMuted, "—");
                }

                // Col 4: World / Job / Location
                ImGui.TableNextColumn();
                var worldText = f.CurrentWorldId != 0 && f.CurrentWorldId != f.HomeWorldId && !string.IsNullOrEmpty(f.CurrentWorldName)
                    ? $"{f.HomeWorldName} -> {f.CurrentWorldName}"
                    : f.HomeWorldName;

                if (f.IsOnline)
                {
                    var detail = !string.IsNullOrEmpty(f.JobAbbr) ? $"[{f.JobAbbr}] {worldText}" : worldText;
                    ImGui.TextColored(Theme.TextLight, detail);
                    if (!string.IsNullOrEmpty(f.LocationName) && ImGui.IsItemHovered())
                        ImGui.SetTooltip(f.LocationName);
                }
                else
                {
                    ImGui.TextColored(Theme.TextMuted, worldText);
                }

                // Col 5: Last Seen
                if (showLastSeen)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextColored(f.IsOnline ? Theme.GreenOnline : Theme.TextMuted,
                        L.FormatRelativeTime(meta.LastSeenOnlineUtc, f.IsOnline));
                }

                // Col 6: Edit Button
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.BgSurface);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.PurpleMain);
                if (ImGui.SmallButton(L.T("Edit##btn", "Edit##btn")))
                    OpenEditModalFor(f, meta);
                ImGui.PopStyleColor(2);

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.TextColored(Theme.TextMuted, L.T(
            $"Showing {filtered.Count} of {allFriends.Count} friends  •  Right-click any friend for quick in-game group actions",
            $"Mostrando {filtered.Count} de {allFriends.Count} amigos  •  Clique com o botão direito em qualquer amigo para ações rápidas de grupo"));
    }

    private void DrawGroupsTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var allFriends = Plugin.Friends.Friends.ToList();
        var isBusy = Plugin.BulkRemover.IsRunning;

        if (!ImGui.BeginChild("##GroupsScroll", Vector2.Zero, false))
            return;

        using (UiCard.Begin(L.T("FFXIV Native Friend Groups", "Grupos Nativos da Lista de Amigos (FFXIV)")))
        {
            UiControls.InsetNote(L.T(
                "These are FFXIV's 7 real in-game Friend Groups. Give each symbol a custom label & color here, filter by group, or move/clear all members of a group directly in the game's Friend List.",
                "Estes são os 7 Grupos reais do FFXIV. Dê um nome e cor personalizados para cada símbolo, filtre por grupo ou mova/limpe todos os membros de um grupo direto na Lista de Amigos do jogo."));
        }

        foreach (var g in NativeFriendGroups.All)
        {
            var custom = Plugin.Config.GetOrCreateNativeGroup(g.Id);
            var members = allFriends.Where(f => f.NativeGroup == g.Id).ToList();
            var onlineMembers = members.Count(f => f.IsOnline);
            var col = Plugin.Config.GetNativeGroupColor(g.Id);

            ImGui.PushID($"ng_{g.Id}");
            using (UiCard.Begin("", Theme.BgPanel, new Vector4(col.X, col.Y, col.Z, 0.42f)))
            {
                // Top row: Symbol + Custom Name Input + Color Picker + Member Count + View Button
                UiControls.DrawPillBadge($"{g.Symbol}  #{g.Id}", col, true);
                ImGui.SameLine(0, 8f * scale);

                var labelBuf = custom.CustomName;
                ImGui.SetNextItemWidth(190f * scale);
                if (ImGui.InputTextWithHint("##gName", L.T(g.DefaultNameEn, g.DefaultNamePt), ref labelBuf, 32))
                {
                    custom.CustomName = labelBuf;
                    Plugin.Config.Save();
                }

                ImGui.SameLine();
                var colEdit = col;
                if (ImGui.ColorEdit4("##gCol", ref colEdit, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                {
                    custom.Color = colEdit;
                    custom.UseCustomColor = true;
                    Plugin.Config.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(L.T("Badge color in FriendList Frenzy", "Cor do badge no FriendList Frenzy"));

                if (custom.UseCustomColor)
                {
                    ImGui.SameLine(0, 4f * scale);
                    ImGui.PushStyleColor(ImGuiCol.Button, Theme.BgSurface);
                    if (ImGui.SmallButton("×##rstCol"))
                    {
                        custom.UseCustomColor = false;
                        custom.Color = g.DefaultColor;
                        Plugin.Config.Save();
                    }
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(L.T("Reset to default color", "Restaurar cor padrão"));
                }

                ImGui.SameLine(0, 12f * scale);
                ImGui.TextColored(members.Count > 0 ? Theme.TextWhite : Theme.TextMuted,
                    L.T($"{members.Count} friends ({onlineMembers} online)", $"{members.Count} amigos ({onlineMembers} online)"));

                // Action row: View in Friends | Move All To [Group] | Clear Group
                ImGui.Dummy(new Vector2(0, 2f * scale));
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.BgSurface);
                if (ImGui.Button(L.T("View Members", "Ver Membros")))
                {
                    nativeGroupFilter = g.Id;
                    requestFocusFriendsTab = true;
                }
                ImGui.PopStyleColor();

                ImGui.SameLine(0, 10f * scale);
                ImGui.BeginDisabled(members.Count == 0 || isBusy);

                // Ensure default move target isn't self
                if (moveAllTargets[g.Id] == g.Id || moveAllTargets[g.Id] < 1 || moveAllTargets[g.Id] > NativeFriendGroups.MaxGroupId)
                    moveAllTargets[g.Id] = g.Id == 1 ? (byte)2 : (byte)1;

                ImGui.SetNextItemWidth(145f * scale);
                var destId = moveAllTargets[g.Id];
                ImGui.PushStyleColor(ImGuiCol.Text, Plugin.Config.GetNativeGroupColor(destId));
                var openDest = ImGui.BeginCombo("##moveDest", Plugin.Config.GetNativeGroupLabel(destId));
                ImGui.PopStyleColor();
                if (openDest)
                {
                    foreach (var dest in NativeFriendGroups.All)
                    {
                        if (dest.Id == g.Id) continue;
                        ImGui.PushStyleColor(ImGuiCol.Text, Plugin.Config.GetNativeGroupColor(dest.Id));
                        if (ImGui.Selectable(Plugin.Config.GetNativeGroupLabel(dest.Id), destId == dest.Id))
                            moveAllTargets[g.Id] = dest.Id;
                        ImGui.PopStyleColor();
                    }
                    ImGui.EndCombo();
                }

                ImGui.SameLine();
                if (ImGui.Button(L.T("Move All Here", "Mover Todos")))
                {
                    pendingBatchKind = BulkActionKind.SetNativeGroup;
                    pendingBatchGroup = moveAllTargets[g.Id];
                    pendingBatchTargets = members;
                    openConfirmModal = true;
                }

                ImGui.SameLine(0, 10f * scale);
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedDangerDark);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.RedDanger);
                if (ImGui.Button(L.T("Empty Group", "Esvaziar Grupo")))
                {
                    pendingBatchKind = BulkActionKind.ClearNativeGroup;
                    pendingBatchGroup = 0;
                    pendingBatchTargets = members;
                    openConfirmModal = true;
                }
                ImGui.PopStyleColor(2);

                ImGui.EndDisabled();
            }
            ImGui.PopID();
        }

        // Ungrouped summary card
        var ungroupedCount = allFriends.Count(f => f.NativeGroup == 0);
        using (UiCard.Begin("", Theme.BgPanel, Theme.BorderSoft))
        {
            UiControls.DrawPillBadge(L.T("○ Ungrouped", "○ Sem Grupo"), Theme.TextMuted);
            ImGui.SameLine(0, 10f * scale);
            ImGui.TextColored(Theme.TextLight, L.T($"{ungroupedCount} friends without an in-game group", $"{ungroupedCount} amigos sem grupo no jogo"));
            ImGui.SameLine(0, 12f * scale);
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.BgSurface);
            if (ImGui.Button(L.T("Filter Ungrouped", "Filtrar Sem Grupo")))
            {
                nativeGroupFilter = 0;
                requestFocusFriendsTab = true;
            }
            ImGui.PopStyleColor();
        }

        ImGui.EndChild();
    }

    private void DrawBulkQueueTab()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var bulk = Plugin.BulkRemover;

        using (UiCard.Begin(L.T("Live Queue Status", "Status da Fila")))
        {
            if (bulk.IsRunning)
            {
                var processed = bulk.SucceededCount + bulk.FailedCount;
                var total = Math.Max(1, bulk.TotalInBatch);
                var progress = Math.Clamp((float)processed / total, 0f, 1f);

                ImGui.TextColored(Theme.YellowMain, bulk.GetBatchActionLabel(bulk.CurrentBatchKind, bulk.CurrentBatchTargetGroup));
                ImGui.TextColored(Theme.TextLight, $"{L.T("Status:", "Status:")} {bulk.StatusMessage}");
                ImGui.TextColored(Theme.TextMuted, $"{L.T("Current target:", "Alvo atual:")} {bulk.CurrentTargetName}");
                ImGui.ProgressBar(progress, new Vector2(UiCard.InnerWidth, 20f * scale),
                    $"{processed}/{bulk.TotalInBatch}  (OK: {bulk.SucceededCount}, {L.T("Failed", "Falhas")}: {bulk.FailedCount})");

                ImGui.Spacing();
                if (ImGui.Button(bulk.IsPaused ? L.T("Resume", "Retomar") : L.T("Pause", "Pausar"), new Vector2(120f * scale, 0)))
                    bulk.TogglePause();
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedDangerDark);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.RedDanger);
                if (ImGui.Button(L.T("Cancel Queue", "Cancelar Fila"), new Vector2(130f * scale, 0)))
                    bulk.Cancel();
                ImGui.PopStyleColor(2);
            }
            else
            {
                ImGui.TextColored(Theme.TextLight, string.IsNullOrEmpty(bulk.StatusMessage)
                    ? L.T("Queue is idle. Select friends in the Friends tab to bulk-assign native groups, clear groups, or remove friends.",
                          "Fila ociosa. Selecione amigos na aba Amigos para atribuir grupos nativos, limpar grupos ou remover amigos em massa.")
                    : bulk.StatusMessage);
            }
        }

        ImGui.TextColored(Theme.YellowMain, L.T("ACTIVITY LOG", "LOG DE ATIVIDADES"));
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.BgSurface);
        if (ImGui.SmallButton(L.T("Clear Log", "Limpar Log")))
            bulk.ClearLog();
        ImGui.PopStyleColor();

        var logFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.ScrollY;
        if (ImGui.BeginTable("##BulkLogTable", 4, logFlags, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn(L.T("Time", "Hora"), ImGuiTableColumnFlags.WidthFixed, 76f * scale);
            ImGui.TableSetupColumn(L.T("Action", "Ação"), ImGuiTableColumnFlags.WidthFixed, 92f * scale);
            ImGui.TableSetupColumn(L.T("Friend", "Amigo"), ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn(L.T("Result", "Resultado"), ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableHeadersRow();

            foreach (var entry in bulk.ActionLog)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextMuted, entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"));

                ImGui.TableNextColumn();
                var (tag, tagCol) = entry.Action switch
                {
                    BulkActionKind.SetNativeGroup => (L.T("GROUP", "GRUPO"), Theme.CyanDuty),
                    BulkActionKind.ClearNativeGroup => (L.T("UNGROUP", "LIMPAR"), Theme.OrangeAway),
                    BulkActionKind.RemoveFriend => (L.T("REMOVE", "REMOVER"), Theme.RedDanger),
                    _ => ("INFO", Theme.TextMuted),
                };
                ImGui.TextColored(tagCol, tag);

                ImGui.TableNextColumn();
                ImGui.TextColored(Theme.TextWhite, entry.FriendName);

                ImGui.TableNextColumn();
                ImGui.TextColored(entry.Success ? Theme.GreenOnline : Theme.RedDanger, entry.Message);
            }

            ImGui.EndTable();
        }
    }

    private void DrawSettingsTab()
    {
        if (!ImGui.BeginChild("##SettingsScroll", Vector2.Zero, false))
            return;

        using (UiCard.Begin(L.T("Bulk Automation (In-Game Sync)", "Automação em Massa (Sincronização no Jogo)")))
        {
            var delay = Plugin.Config.RemovalDelayMs;
            if (UiControls.SettingSliderIntRow("delay",
                L.T("Delay between actions", "Intervalo entre ações"),
                ref delay, 500, 3500, "%d ms",
                L.T("Safer values (900-1500ms) give FFXIV time to sync each change with the server.",
                    "Valores seguros (900-1500ms) dão tempo ao FFXIV para sincronizar cada alteração com o servidor.")))
            {
                Plugin.Config.RemovalDelayMs = delay;
                Plugin.Config.Save();
            }

            var closeAfter = Plugin.Config.CloseNativeListAfterBatch;
            if (UiControls.SettingToggleRow("closeAfter",
                L.T("Close in-game Friend List when batch finishes", "Fechar Lista de Amigos do jogo ao concluir"),
                ref closeAfter))
            {
                Plugin.Config.CloseNativeListAfterBatch = closeAfter;
                Plugin.Config.Save();
            }

            var printChat = Plugin.Config.PrintChatSummary;
            if (UiControls.SettingToggleRow("printChat",
                L.T("Print summary in chat after batch completes", "Mostrar resumo no chat ao concluir"),
                ref printChat))
            {
                Plugin.Config.PrintChatSummary = printChat;
                Plugin.Config.Save();
            }
        }

        using (UiCard.Begin(L.T("Display Preferences", "Preferências de Exibição")))
        {
            var showFc = Plugin.Config.ShowFcTag;
            if (UiControls.SettingToggleRow("showFc", L.T("Show Free Company tags", "Mostrar tags de Free Company"), ref showFc))
            {
                Plugin.Config.ShowFcTag = showFc;
                Plugin.Config.Save();
            }

            var showNick = Plugin.Config.ShowNickname;
            if (UiControls.SettingToggleRow("showNick", L.T("Show custom nicknames", "Mostrar apelidos personalizados"), ref showNick))
            {
                Plugin.Config.ShowNickname = showNick;
                Plugin.Config.Save();
            }

            var showSeen = Plugin.Config.ShowLastSeen;
            if (UiControls.SettingToggleRow("showSeen", L.T("Show Last Seen column", "Mostrar coluna Visto por Último"), ref showSeen))
            {
                Plugin.Config.ShowLastSeen = showSeen;
                Plugin.Config.Save();
            }
        }

        using (UiCard.Begin(L.T("Language & Commands", "Idioma & Comandos")))
        {
            ImGui.TextColored(Theme.TextLight, L.T("Interface Language:", "Idioma da Interface:"));
            ImGui.SameLine();
            if (ImGui.RadioButton("English", !L.IsPt))
            {
                Plugin.Config.Language = "en";
                Plugin.Config.Save();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Português (BR)", L.IsPt))
            {
                Plugin.Config.Language = "pt";
                Plugin.Config.Save();
            }

            ImGui.Dummy(new Vector2(0, 4f));
            UiControls.InsetNote(L.T(
                "Chat commands: /friendlistfrenzy or /flf  •  Right-click any friend row for instant in-game group actions.",
                "Comandos de chat: /friendlistfrenzy ou /flf  •  Clique com o botão direito em qualquer amigo para ações rápidas de grupo."));
        }

        ImGui.EndChild();
    }

    private void DrawFilterPill(string label, int value)
    {
        var active = statusFilter == value;
        ImGui.PushStyleColor(ImGuiCol.Button, active ? Theme.PurpleMain : Theme.BgSurface);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? Theme.PurpleBright : Theme.BgElevated);
        ImGui.PushStyleColor(ImGuiCol.Text, active ? Theme.YellowMain : Theme.TextLight);
        if (ImGui.Button($"{label}##sf_{value}"))
            statusFilter = value;
        ImGui.PopStyleColor(3);
    }

    private void OpenEditModalFor(FriendEntry friend, FriendMetadata meta)
    {
        editingContentId = friend.ContentId;
        editFriendName = friend.Name;
        editNickname = meta.Nickname;
        editNote = meta.Note;
        editNativeGroup = friend.NativeGroup;
        editOriginalNativeGroup = friend.NativeGroup;
        editProtected = meta.IsProtected;
        editUseColor = meta.UseCustomColor;
        editColor = meta.CustomColor;
        openEditModal = true;
    }

    private void DrawEditFriendModal()
    {
        if (openEditModal)
        {
            ImGui.OpenPopup(EditModalId);
            openEditModal = false;
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(450, 395), ImGuiCond.Appearing);
        var title = L.T($"Edit Friend{EditModalId}", $"Editar Amigo{EditModalId}");
        if (ImGui.BeginPopupModal(title, ref open, ImGuiWindowFlags.NoResize))
        {
            ImGui.TextColored(Theme.YellowMain, editFriendName);
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextColored(Theme.TextMuted, L.T("Nickname:", "Apelido:"));
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##EditNick", ref editNickname, 64);

            ImGui.TextColored(Theme.TextMuted, L.T("FFXIV In-Game Friend Group:", "Grupo Nativo no FFXIV:"));
            ImGui.SetNextItemWidth(-1);
            var preview = editNativeGroup == 0
                ? L.T("○ Ungrouped", "○ Sem Grupo")
                : Plugin.Config.GetNativeGroupLabel(editNativeGroup);
            if (ImGui.BeginCombo("##EditNativeGrp", preview))
            {
                if (ImGui.Selectable(L.T("○ Ungrouped", "○ Sem Grupo"), editNativeGroup == 0))
                    editNativeGroup = 0;
                foreach (var g in NativeFriendGroups.All)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Plugin.Config.GetNativeGroupColor(g.Id));
                    if (ImGui.Selectable(Plugin.Config.GetNativeGroupLabel(g.Id), editNativeGroup == g.Id))
                        editNativeGroup = g.Id;
                    ImGui.PopStyleColor();
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
            ImGui.InputTextMultiline("##EditNote", ref editNote, 256, new Vector2(-1, 78f));

            ImGui.Spacing();
            if (ImGui.Button(L.T("Save", "Salvar"), new Vector2(130, 0)))
            {
                var meta = Plugin.Config.GetOrCreateMeta(editingContentId, editFriendName);
                meta.Nickname = editNickname.Trim();
                meta.Note = editNote.Trim();
                meta.IsProtected = editProtected;
                meta.UseCustomColor = editUseColor;
                meta.CustomColor = editColor;
                Plugin.Config.Save();

                // If native in-game group changed, sync it directly to FFXIV's Friend List!
                if (editNativeGroup != editOriginalNativeGroup && !Plugin.BulkRemover.IsRunning)
                {
                    var live = Plugin.Friends.Friends.FirstOrDefault(f => f.ContentId == editingContentId);
                    if (live != null)
                    {
                        if (editNativeGroup == 0)
                            Plugin.BulkRemover.StartClearGroupBatch([live]);
                        else
                            Plugin.BulkRemover.StartSetGroupBatch([live], editNativeGroup);
                    }
                }

                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.BgSurface);
            if (ImGui.Button(L.T("Cancel", "Cancelar"), new Vector2(120, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.PopStyleColor();

            ImGui.EndPopup();
        }
    }

    private void DrawConfirmBulkModal()
    {
        if (openConfirmModal)
        {
            ImGui.OpenPopup(ConfirmModalId);
            openConfirmModal = false;
        }

        var open = true;
        ImGui.SetNextWindowSize(new Vector2(490, 380), ImGuiCond.Appearing);
        var modalTitle = pendingBatchKind switch
        {
            BulkActionKind.RemoveFriend => L.T($"Confirm Bulk Friend Removal{ConfirmModalId}", $"Confirmar Remoção em Massa{ConfirmModalId}"),
            BulkActionKind.SetNativeGroup => L.T($"Confirm In-Game Group Sync{ConfirmModalId}", $"Confirmar Alteração de Grupo no Jogo{ConfirmModalId}"),
            BulkActionKind.ClearNativeGroup => L.T($"Confirm Remove from In-Game Group{ConfirmModalId}", $"Confirmar Remoção do Grupo no Jogo{ConfirmModalId}"),
            _ => $"Confirm{ConfirmModalId}",
        };

        if (ImGui.BeginPopupModal(modalTitle, ref open, ImGuiWindowFlags.NoResize))
        {
            var isRemove = pendingBatchKind == BulkActionKind.RemoveFriend;
            var headerCol = isRemove ? Theme.RedDanger : Theme.YellowMain;

            var headline = pendingBatchKind switch
            {
                BulkActionKind.RemoveFriend => L.T(
                    $"Remove {pendingBatchTargets.Count} friend(s) from your FFXIV Friend List:",
                    $"Remover {pendingBatchTargets.Count} amigo(s) da sua Lista de Amigos do FFXIV:"),
                BulkActionKind.SetNativeGroup => L.T(
                    $"Assign {pendingBatchTargets.Count} friend(s) to native group {Plugin.Config.GetNativeGroupLabel(pendingBatchGroup)}:",
                    $"Mover {pendingBatchTargets.Count} amigo(s) para o grupo nativo {Plugin.Config.GetNativeGroupLabel(pendingBatchGroup)}:"),
                BulkActionKind.ClearNativeGroup => L.T(
                    $"Remove {pendingBatchTargets.Count} friend(s) from their native in-game Friend Group:",
                    $"Remover {pendingBatchTargets.Count} amigo(s) do seu Grupo nativo no FFXIV:"),
                _ => string.Empty,
            };

            UiControls.TextWrappedColored(headerCol, headline);
            UiControls.TextWrappedColored(Theme.TextMuted, L.T(
                "Avoid clicking game menus while the queue runs. You can Pause or Cancel at any time.",
                "Evite clicar nos menus do jogo enquanto a fila roda. Você pode Pausar ou Cancelar a qualquer momento."));

            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.BgDark);
            if (ImGui.BeginChild("##ConfirmList", new Vector2(0, 195f), true))
            {
                foreach (var t in pendingBatchTargets)
                {
                    var curGrp = t.NativeGroup > 0 ? $"  [{Plugin.Config.GetNativeGroupLabel(t.NativeGroup)}]" : string.Empty;
                    ImGui.BulletText($"{t.Name} ({t.HomeWorldName}){curGrp}");
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();

            ImGui.Spacing();
            if (isRemove)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedDangerDark);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.RedDanger);
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button, Theme.PurpleMain);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.PurpleBright);
            }

            var confirmLabel = pendingBatchKind switch
            {
                BulkActionKind.RemoveFriend => L.T($"Yes, Remove {pendingBatchTargets.Count} Friends", $"Sim, Remover {pendingBatchTargets.Count} Amigos"),
                BulkActionKind.SetNativeGroup => L.T($"Sync {pendingBatchTargets.Count} to In-Game Group", $"Sincronizar {pendingBatchTargets.Count} no Jogo"),
                BulkActionKind.ClearNativeGroup => L.T($"Clear Group for {pendingBatchTargets.Count} Friends", $"Limpar Grupo de {pendingBatchTargets.Count} Amigos"),
                _ => L.T("Confirm", "Confirmar"),
            };

            if (ImGui.Button(confirmLabel, new Vector2(265, 0)))
            {
                if (pendingBatchKind == BulkActionKind.RemoveFriend)
                    Plugin.BulkRemover.StartRemoveBatch(pendingBatchTargets);
                else if (pendingBatchKind == BulkActionKind.SetNativeGroup)
                    Plugin.BulkRemover.StartSetGroupBatch(pendingBatchTargets, pendingBatchGroup);
                else if (pendingBatchKind == BulkActionKind.ClearNativeGroup)
                    Plugin.BulkRemover.StartClearGroupBatch(pendingBatchTargets);

                selectedIds.Clear();
                ImGui.CloseCurrentPopup();
            }
            ImGui.PopStyleColor(2);

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.BgSurface);
            if (ImGui.Button(L.T("Cancel", "Cancelar"), new Vector2(120, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.PopStyleColor();

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

        if (nativeGroupFilter == 0)
            result = result.Where(f => f.NativeGroup == 0);
        else if (nativeGroupFilter > 0)
            result = result.Where(f => f.NativeGroup == nativeGroupFilter);

        if (!string.IsNullOrEmpty(q))
        {
            result = result.Where(f =>
            {
                var meta = Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name);
                var groupLabel = f.NativeGroup > 0 ? Plugin.Config.GetNativeGroupLabel(f.NativeGroup, false) : string.Empty;
                return f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || f.HomeWorldName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || f.CurrentWorldName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || f.FcTag.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || f.LocationName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || groupLabel.Contains(q, StringComparison.OrdinalIgnoreCase)
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
            4 => result.OrderBy(f => f.NativeGroup == 0 ? 99 : f.NativeGroup)
                       .ThenByDescending(f => f.IsOnline)
                       .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                       .ToList(),
            _ => result.OrderByDescending(f => f.IsOnline)
                       .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                       .ToList(),
        };
    }
}
