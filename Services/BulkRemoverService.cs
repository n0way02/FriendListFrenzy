using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FriendListFrenzy.Models;
using Lumina.Excel.Sheets;

namespace FriendListFrenzy.Services;

public enum BulkEngineState
{
    Idle,
    OpeningFriendList,
    WaitingForListReady,
    OpeningContextMenu,
    WaitingForContextMenu,
    WaitingForGroupPicker,
    WaitingForConfirmDialog,
    WaitingForRemovalVerify,
    WaitingForGroupVerify,
    CooldownBeforeNext,
}

public sealed unsafe class BulkRemoverService : IDisposable
{
    private sealed class BulkTaskItem
    {
        public required ulong ContentId { get; init; }
        public required string Name { get; init; }
        public required BulkActionKind Action { get; init; }
        public byte TargetGroup { get; init; }
    }

    private readonly Queue<BulkTaskItem> queue = new();
    private readonly List<BulkActionLogEntry> actionLog = new();
    private readonly HashSet<string> removeFriendLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> assignGroupLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ushort> visibleAddonIdsBefore = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private BulkEngineState state = BulkEngineState.Idle;
    private BulkTaskItem? currentTarget;
    private long nextStepAtMs;
    private long stateEnteredAtMs;
    private uint friendListMainCommandId;
    private int currentRetries;

    // Stability tracking for FriendList reload after opening or removing a friend
    private int lastObservedEntryCount = -1;
    private long entryCountStableSinceMs;
    private int countBeforeRemoval;
    private ushort groupDialogAddonId;
    private bool groupDialogFallbackSent;

    private const long ListLoadTimeoutMs = 9000;
    private const long MenuTimeoutMs = 4500;
    private const long ListStableDurationMs = 450;

    public bool IsRunning => state != BulkEngineState.Idle;
    public bool IsPaused { get; private set; }
    public BulkActionKind CurrentBatchKind { get; private set; } = BulkActionKind.None;
    public byte CurrentBatchTargetGroup { get; private set; }
    public int TotalInBatch { get; private set; }
    public int SucceededCount { get; private set; }
    public int FailedCount { get; private set; }
    public int RemainingCount => queue.Count + (currentTarget != null ? 1 : 0);
    public string CurrentTargetName => currentTarget?.Name ?? string.Empty;
    public string StatusMessage { get; private set; } = string.Empty;
    public IReadOnlyList<BulkActionLogEntry> ActionLog => actionLog;

    public BulkRemoverService()
    {
        LoadLocalizedGameLabels();
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    private void LoadLocalizedGameLabels()
    {
        // Known multi-language fallbacks (EN / DE / FR / JP)
        removeFriendLabels.Add("Remove from Friend List");
        removeFriendLabels.Add("Remove Friend");
        removeFriendLabels.Add("Aus der Freundesliste entfernen");
        removeFriendLabels.Add("Retirer de la liste d'amis");
        removeFriendLabels.Add("フレンドリストから削除");

        assignGroupLabels.Add("Assign Friend Group");
        assignGroupLabels.Add("Add to Friend Group");
        assignGroupLabels.Add("Change Friend Group");
        assignGroupLabels.Add("Assign to Friend Group");
        assignGroupLabels.Add("Freundesgruppe zuweisen");
        assignGroupLabels.Add("Zu Freundesgruppe hinzufügen");
        assignGroupLabels.Add("Freundesgruppe ändern");
        assignGroupLabels.Add("Attribuer un groupe d'amis");
        assignGroupLabels.Add("Attribuer à un groupe d'amis");
        assignGroupLabels.Add("Ajouter à un groupe d'amis");
        assignGroupLabels.Add("Changer de groupe d'amis");
        assignGroupLabels.Add("フレンドグループ設定");
        assignGroupLabels.Add("フレンドグループに追加");
        assignGroupLabels.Add("フレンドグループを変更");

        try
        {
            var addonSheet = Plugin.DataManager.GetExcelSheet<Addon>();
            if (addonSheet != null)
            {
                foreach (var row in addonSheet)
                {
                    var text = row.Text.ExtractText().Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    if (text.Contains("Remove from Friend", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Aus der Freundesliste entfernen", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Retirer de la liste d'amis", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("フレンドリストから削除", StringComparison.Ordinal))
                    {
                        removeFriendLabels.Add(text);
                    }
                    else if (text.Contains("Assign Friend Group", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Add to Friend Group", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Change Friend Group", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Freundesgruppe zuweisen", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Freundesgruppe ändern", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("groupe d'amis", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("フレンドグループ設定", StringComparison.Ordinal)
                        || text.Contains("フレンドグループに追加", StringComparison.Ordinal)
                        || text.Contains("フレンドグループを変更", StringComparison.Ordinal))
                    {
                        assignGroupLabels.Add(text);
                    }
                }
            }

            // Locate MainCommand ID for "Friend List" so we can reliably open it via UIModule.ExecuteMainCommand
            var mcSheet = Plugin.DataManager.GetExcelSheet<MainCommand>();
            if (mcSheet != null)
            {
                foreach (var row in mcSheet)
                {
                    var name = row.Name.ExtractText().Trim();
                    if (name.Equals("Friend List", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Freundesliste", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Liste d'amis", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("フレンドリスト", StringComparison.Ordinal))
                    {
                        friendListMainCommandId = row.RowId;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to load localized Friend List labels.");
        }
    }

    public void StartRemoveBatch(IEnumerable<FriendEntry> targets)
        => StartBatchInternal(targets, BulkActionKind.RemoveFriend, 0);

    public void StartSetGroupBatch(IEnumerable<FriendEntry> targets, byte targetNativeGroup)
    {
        if (targetNativeGroup < 1 || targetNativeGroup > NativeFriendGroups.MaxGroupId)
            return;
        StartBatchInternal(targets, BulkActionKind.SetNativeGroup, targetNativeGroup);
    }

    public void StartClearGroupBatch(IEnumerable<FriendEntry> targets)
        => StartBatchInternal(targets, BulkActionKind.ClearNativeGroup, 0);

    private void StartBatchInternal(IEnumerable<FriendEntry> targets, BulkActionKind kind, byte targetGroup)
    {
        if (IsRunning) return;

        queue.Clear();
        SucceededCount = 0;
        FailedCount = 0;
        IsPaused = false;
        currentTarget = null;
        CurrentBatchKind = kind;
        CurrentBatchTargetGroup = targetGroup;

        foreach (var t in targets)
        {
            if (kind == BulkActionKind.RemoveFriend)
            {
                var meta = Plugin.Config.GetOrCreateMeta(t.ContentId, t.Name);
                if (meta.IsProtected)
                {
                    AddLog(t.Name, kind, false, L.T("Skipped (Protected favorite)", "Ignorado (Favorito protegido)"));
                    FailedCount++;
                    continue;
                }
            }
            else if (kind == BulkActionKind.SetNativeGroup && t.NativeGroup == targetGroup)
            {
                AddLog(t.Name, kind, true, L.T("Already in this group", "Já está neste grupo"));
                SucceededCount++;
                continue;
            }
            else if (kind == BulkActionKind.ClearNativeGroup && t.NativeGroup == 0)
            {
                AddLog(t.Name, kind, true, L.T("Already ungrouped", "Já está sem grupo"));
                SucceededCount++;
                continue;
            }

            queue.Enqueue(new BulkTaskItem
            {
                ContentId = t.ContentId,
                Name = t.Name,
                Action = kind,
                TargetGroup = targetGroup,
            });
        }

        TotalInBatch = queue.Count;
        if (TotalInBatch == 0)
        {
            CurrentBatchKind = BulkActionKind.None;
            StatusMessage = L.T("Nothing to change (all targets already matched or protected).", "Nada para alterar (todos os alvos já correspondem ou estão protegidos).");
            return;
        }

        Transition(BulkEngineState.OpeningFriendList, GetBatchActionLabel(kind, targetGroup), 120);
    }

    public void TogglePause()
    {
        if (!IsRunning) return;
        IsPaused = !IsPaused;
        StatusMessage = IsPaused
            ? L.T("Paused by user.", "Pausado pelo usuário.")
            : L.T("Resuming...", "Retomando...");
    }

    public void Cancel()
    {
        if (!IsRunning) return;
        queue.Clear();
        currentTarget = null;
        IsPaused = false;
        state = BulkEngineState.Idle;
        CurrentBatchKind = BulkActionKind.None;
        StatusMessage = L.T(
            $"Cancelled. Completed: {SucceededCount}, Failed: {FailedCount}.",
            $"Cancelado. Concluídos: {SucceededCount}, Falhas: {FailedCount}.");
    }

    public void ClearLog() => actionLog.Clear();

    public string GetBatchActionLabel(BulkActionKind kind, byte targetGroup) => kind switch
    {
        BulkActionKind.RemoveFriend => L.T("Bulk Friend Removal", "Remoção em Massa"),
        BulkActionKind.SetNativeGroup => L.T(
            $"Set In-Game Group -> {Plugin.Config.GetNativeGroupLabel(targetGroup)}",
            $"Definir Grupo no Jogo -> {Plugin.Config.GetNativeGroupLabel(targetGroup)}"),
        BulkActionKind.ClearNativeGroup => L.T("Remove from In-Game Group", "Remover do Grupo no Jogo"),
        _ => L.T("Idle", "Ocioso"),
    };

    private void Transition(BulkEngineState nextState, string message, long delayMs = 0)
    {
        state = nextState;
        StatusMessage = message;
        var now = clock.ElapsedMilliseconds;
        stateEnteredAtMs = now;
        nextStepAtMs = now + delayMs;
    }

    private void ResetStabilityTracker()
    {
        lastObservedEntryCount = -1;
        entryCountStableSinceMs = clock.ElapsedMilliseconds;
    }

    private void AddLog(string name, BulkActionKind kind, bool success, string message)
    {
        actionLog.Insert(0, new BulkActionLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            FriendName = name,
            Action = kind,
            Success = success,
            Message = message,
        });
        if (actionLog.Count > 250)
            actionLog.RemoveRange(250, actionLog.Count - 250);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (state == BulkEngineState.Idle || IsPaused) return;
        if (!Plugin.ClientState.IsLoggedIn)
        {
            Cancel();
            return;
        }

        var now = clock.ElapsedMilliseconds;
        if (now < nextStepAtMs) return;

        try
        {
            switch (state)
            {
                case BulkEngineState.OpeningFriendList:
                    HandleOpeningFriendList();
                    break;
                case BulkEngineState.WaitingForListReady:
                    HandleWaitingForListReady(now);
                    break;
                case BulkEngineState.OpeningContextMenu:
                    HandleOpeningContextMenu();
                    break;
                case BulkEngineState.WaitingForContextMenu:
                    HandleWaitingForContextMenu(now);
                    break;
                case BulkEngineState.WaitingForGroupPicker:
                    HandleWaitingForGroupPicker(now);
                    break;
                case BulkEngineState.WaitingForConfirmDialog:
                    HandleWaitingForConfirmDialog(now);
                    break;
                case BulkEngineState.WaitingForRemovalVerify:
                    HandleWaitingForRemovalVerify(now);
                    break;
                case BulkEngineState.WaitingForGroupVerify:
                    HandleWaitingForGroupVerify(now);
                    break;
                case BulkEngineState.CooldownBeforeNext:
                    AdvanceToNextOrFinish();
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error in BulkRemoverService state machine.");
            FailCurrentTarget(L.T("Unexpected error while automating Friend List.", "Erro inesperado ao automatizar Lista de Amigos."));
        }
    }

    private void HandleOpeningFriendList()
    {
        if (currentTarget == null)
        {
            if (queue.Count == 0)
            {
                FinishBatch();
                return;
            }
            currentTarget = queue.Dequeue();
            currentRetries = 0;
        }

        var addon = GetVisibleAddon("FriendList");
        if (addon == null)
        {
            var uiModule = UIModule.Instance();
            if (uiModule != null && friendListMainCommandId != 0)
            {
                uiModule->ExecuteMainCommand(friendListMainCommandId);
            }
            else
            {
                var agent = AgentFriendlist.Instance();
                if (agent != null) agent->Show();
            }
        }

        ResetStabilityTracker();
        Transition(BulkEngineState.WaitingForListReady,
            L.T($"Waiting for Friend List ({currentTarget.Name})...", $"Aguardando Lista de Amigos ({currentTarget.Name})..."),
            450);
    }

    private void HandleWaitingForListReady(long now)
    {
        if (currentTarget == null)
        {
            AdvanceToNextOrFinish();
            return;
        }

        var addon = GetVisibleAddon("FriendList");
        if (addon == null || !addon->IsReady)
        {
            if (now - stateEnteredAtMs > ListLoadTimeoutMs)
            {
                FailCurrentTarget(L.T("FriendList window did not open in time.", "Janela FriendList não abriu a tempo."));
            }
            return;
        }

        if (GetVisibleAddon("ContextMenu") != null || GetVisibleAddon("ContextIconMenu") != null || GetVisibleAddon("SelectYesno") != null)
            return;

        var proxy = FriendListService.GetFriendProxy();
        if (proxy == null) return;

        // Always reset native FriendList filter to "All" (0) so every friend is reachable
        if (proxy->FilterGroup != 0)
        {
            proxy->FilterGroup = 0;
            proxy->ApplyFilters();
            ResetStabilityTracker();
            nextStepAtMs = now + 400;
            return;
        }

        var currentCount = (int)proxy->EntryCount;
        if (currentCount <= 0)
        {
            if (now - stateEnteredAtMs > ListLoadTimeoutMs)
            {
                FailCurrentTarget(L.T("Friend List stayed empty while loading.", "Lista de Amigos permaneceu vazia ao carregar."));
            }
            return;
        }

        if (currentCount != lastObservedEntryCount)
        {
            lastObservedEntryCount = currentCount;
            entryCountStableSinceMs = now;
            return;
        }

        if (now - entryCountStableSinceMs < ListStableDurationMs)
            return;

        Plugin.Friends.RefreshFromMemory();

        if (!Plugin.Friends.ContainsContentId(currentTarget.ContentId))
        {
            if (currentTarget.Action == BulkActionKind.RemoveFriend)
            {
                SucceededCount++;
                AddLog(currentTarget.Name, currentTarget.Action, true, L.T("Already removed", "Já removido"));
            }
            else
            {
                FailedCount++;
                AddLog(currentTarget.Name, currentTarget.Action, false, L.T("Friend no longer in list", "Amigo não está mais na lista"));
            }
            currentTarget = null;
            Transition(BulkEngineState.CooldownBeforeNext, L.T("Moving to next...", "Indo para o próximo..."), 200);
            return;
        }

        // Check if native group already matches desired state
        var liveGroup = GetLiveNativeGroup(currentTarget.ContentId);
        if (currentTarget.Action == BulkActionKind.SetNativeGroup && liveGroup == currentTarget.TargetGroup)
        {
            SucceededCount++;
            AddLog(currentTarget.Name, currentTarget.Action, true, L.T("Already in target group", "Já está no grupo destino"));
            currentTarget = null;
            Transition(BulkEngineState.CooldownBeforeNext, L.T("Moving to next...", "Indo para o próximo..."), 150);
            return;
        }
        if (currentTarget.Action == BulkActionKind.ClearNativeGroup && liveGroup == 0)
        {
            SucceededCount++;
            AddLog(currentTarget.Name, currentTarget.Action, true, L.T("Already ungrouped", "Já está sem grupo"));
            currentTarget = null;
            Transition(BulkEngineState.CooldownBeforeNext, L.T("Moving to next...", "Indo para o próximo..."), 150);
            return;
        }

        Transition(BulkEngineState.OpeningContextMenu,
            L.T($"Selecting {currentTarget.Name}...", $"Selecionando {currentTarget.Name}..."),
            120);
    }

    private void HandleOpeningContextMenu()
    {
        if (currentTarget == null)
        {
            AdvanceToNextOrFinish();
            return;
        }

        var addon = GetVisibleAddon("FriendList");
        if (addon == null || !addon->IsReady)
        {
            Transition(BulkEngineState.OpeningFriendList, L.T("Re-opening Friend List...", "Reabrindo Lista de Amigos..."), 350);
            return;
        }

        var proxy = FriendListService.GetFriendProxy();
        if (proxy == null) return;

        countBeforeRemoval = (int)proxy->EntryCount;

        var rowIdx = Plugin.Friends.FindDisplayRowIndex(currentTarget.ContentId);
        if (rowIdx < 0)
        {
            if (currentRetries < 1)
            {
                currentRetries++;
                ResetStabilityTracker();
                Transition(BulkEngineState.WaitingForListReady,
                    L.T($"Waiting for {currentTarget.Name} to appear...", $"Aguardando {currentTarget.Name} aparecer..."),
                    550);
                return;
            }

            FailCurrentTarget(L.T("Could not locate row in Friend List.", "Não foi possível localizar linha na Lista de Amigos."));
            return;
        }

        // Open right-click ContextMenu on rowIdx in native FriendList addon
        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 1; // 1 = Open ContextMenu for entry
        values[1].Type = AtkValueType.Int;
        values[1].Int = rowIdx;
        addon->FireCallback(2, values, true);

        Transition(BulkEngineState.WaitingForContextMenu,
            L.T($"Opening menu for {currentTarget.Name}...", $"Abrindo menu para {currentTarget.Name}..."),
            200);
    }

    private void HandleWaitingForContextMenu(long now)
    {
        if (currentTarget == null)
        {
            AdvanceToNextOrFinish();
            return;
        }

        var cmPtr = Plugin.GameGui.GetAddonByName("ContextMenu", 1);
        if (cmPtr != nint.Zero)
        {
            var cm = (AddonContextMenu*)cmPtr.Address;
            if (cm != null && cm->IsVisible && cm->IsReady)
            {
                var entriesCount = (int)cm->AtkValues[0].UInt;
                var foundIdx = -1;
                var allEntries = new List<string>(entriesCount);

                for (var i = 0; i < entriesCount; i++)
                {
                    var valIdx = i + 8;
                    if (valIdx >= cm->AtkValuesCount) break;
                    var strPtr = cm->AtkValues[valIdx].String.Value;
                    if (strPtr == null) continue;

                    var text = MemoryHelper.ReadSeStringNullTerminated((nint)strPtr).TextValue.Trim();
                    allEntries.Add(text);

                    var matched = currentTarget.Action switch
                    {
                        BulkActionKind.RemoveFriend => IsRemoveFriendLabel(text),
                        BulkActionKind.SetNativeGroup => IsAssignGroupLabel(text),
                        BulkActionKind.ClearNativeGroup => IsAssignGroupLabel(text),
                        _ => false,
                    };

                    if (matched && foundIdx < 0)
                        foundIdx = i;
                }

                if (foundIdx >= 0)
                {
                    if (currentTarget.Action is BulkActionKind.SetNativeGroup or BulkActionKind.ClearNativeGroup)
                    {
                        SnapshotVisibleAddonIds();
                        groupDialogAddonId = 0;
                        groupDialogFallbackSent = false;
                    }

                    var vals = stackalloc AtkValue[3];
                    vals[0].Type = AtkValueType.Int;
                    vals[0].Int = 0;
                    vals[1].Type = AtkValueType.Int;
                    vals[1].Int = foundIdx;
                    vals[2].Type = AtkValueType.Int;
                    vals[2].Int = 0;
                    cm->FireCallback(3, vals, true);

                    if (currentTarget.Action == BulkActionKind.RemoveFriend)
                    {
                        Transition(BulkEngineState.WaitingForConfirmDialog,
                            L.T($"Confirming removal of {currentTarget.Name}...", $"Confirmando remoção de {currentTarget.Name}..."),
                            220);
                    }
                    else
                    {
                        var targetGrp = currentTarget.Action == BulkActionKind.ClearNativeGroup ? (byte)0 : currentTarget.TargetGroup;
                        var grpName = targetGrp == 0 ? L.T("None", "Nenhum") : Plugin.Config.GetNativeGroupLabel(targetGrp);
                        Transition(BulkEngineState.WaitingForGroupPicker,
                            L.T($"Assigning {grpName} to {currentTarget.Name}...", $"Atribuindo {grpName} para {currentTarget.Name}..."),
                            220);
                    }
                    return;
                }

                Plugin.Log.Info($"[FriendListFrenzy] ContextMenu entries for {currentTarget.Name} ({currentTarget.Action}): {string.Join(" | ", allEntries)}");
                cm->Close(true);
                FailCurrentTarget(currentTarget.Action == BulkActionKind.RemoveFriend
                    ? L.T("'Remove from Friend List' not found in menu.", "Opção 'Remover da Lista de Amigos' não encontrada.")
                    : L.T("'Assign Friend Group' not found in menu.", "Opção 'Assign Friend Group' não encontrada no menu."));
                return;
            }
        }

        if (now - stateEnteredAtMs > MenuTimeoutMs)
        {
            if (currentRetries < 1)
            {
                currentRetries++;
                ResetStabilityTracker();
                Transition(BulkEngineState.WaitingForListReady,
                    L.T($"Retrying menu for {currentTarget.Name}...", $"Tentando menu novamente para {currentTarget.Name}..."),
                    350);
            }
            else
            {
                FailCurrentTarget(L.T("Context menu did not open.", "Menu de contexto não abriu."));
            }
        }
    }

    private void HandleWaitingForGroupPicker(long now)
    {
        if (currentTarget == null)
        {
            AdvanceToNextOrFinish();
            return;
        }

        // Target index in the "Friend Groups" dialog list: 0 = None, 1..7 = ★Group 1 .. ♣Group 7
        var expectedGroup = currentTarget.Action == BulkActionKind.ClearNativeGroup
            ? 0
            : Math.Clamp((int)currentTarget.TargetGroup, 1, NativeFriendGroups.MaxGroupId);

        var dlg = FindFriendGroupDialog();
        if (dlg != null && dlg->IsVisible && dlg->IsReady)
        {
            groupDialogAddonId = dlg->Id;

            // 1. Select the exact row (0 = None, 1..7 = Group 1..7) on the 8-item AtkComponentList inside the dialog
            SelectGroupRowInDialog(dlg, expectedGroup);

            // 2. Fire Apply callback: (0, expectedGroup) commits & closes the "Friend Groups" dialog!
            var vals = stackalloc AtkValue[2];
            vals[0].Type = AtkValueType.Int;
            vals[0].Int = 0; // Apply
            vals[1].Type = AtkValueType.Int;
            vals[1].Int = expectedGroup; // 0 = None, 1..7 = Group 1..7
            dlg->FireCallback(2, vals, true);

            Transition(BulkEngineState.WaitingForGroupVerify,
                L.T($"Applying group to {currentTarget.Name}...", $"Aplicando grupo para {currentTarget.Name}..."),
                350);
            return;
        }

        if (now - stateEnteredAtMs > MenuTimeoutMs)
        {
            FailCurrentTarget(L.T("'Friend Groups' window did not open.", "Janela 'Friend Groups' não abriu."));
        }
    }

    private void HandleWaitingForGroupVerify(long now)
    {
        if (currentTarget == null)
        {
            AdvanceToNextOrFinish();
            return;
        }

        // If the Friend Groups dialog is still open after 350ms, try 1-arg Apply callback (0) once
        var openDlg = groupDialogAddonId != 0 ? FindVisibleAddonById(groupDialogAddonId) : FindFriendGroupDialog();
        if (openDlg != null && openDlg->IsVisible && openDlg->IsReady)
        {
            if (!groupDialogFallbackSent && now - stateEnteredAtMs >= 350)
            {
                groupDialogFallbackSent = true;
                var expectedGrp = currentTarget.Action == BulkActionKind.ClearNativeGroup ? 0 : (int)currentTarget.TargetGroup;
                SelectGroupRowInDialog(openDlg, expectedGrp);
                var vals = stackalloc AtkValue[1];
                vals[0].Type = AtkValueType.Int;
                vals[0].Int = 0; // Apply
                openDlg->FireCallback(1, vals, true);
                nextStepAtMs = now + 350;
                return;
            }
        }

        if (GetVisibleAddon("ContextMenu") != null)
            return;

        Plugin.Friends.RefreshFromMemory();
        var liveGroup = GetLiveNativeGroup(currentTarget.ContentId);
        var expectedGroup = currentTarget.Action == BulkActionKind.ClearNativeGroup ? (byte)0 : currentTarget.TargetGroup;

        if (liveGroup == expectedGroup)
        {
            // Close dialog if it somehow stayed open after update
            if (openDlg != null && openDlg->IsVisible)
                openDlg->Close(true);

            SucceededCount++;
            var msg = expectedGroup == 0
                ? L.T("Removed from in-game group (None)", "Removido do grupo no jogo (None)")
                : L.T($"Assigned to {Plugin.Config.GetNativeGroupLabel(expectedGroup)}", $"Atribuído a {Plugin.Config.GetNativeGroupLabel(expectedGroup)}");
            AddLog(currentTarget.Name, currentTarget.Action, true, msg);
            currentTarget = null;

            var delay = Math.Clamp(Plugin.Config.RemovalDelayMs, 500, 5000);
            Transition(BulkEngineState.CooldownBeforeNext,
                L.T($"Updated! Waiting {delay / 1000.0:0.0}s...", $"Atualizado! Aguardando {delay / 1000.0:0.0}s..."),
                delay);
            return;
        }

        if (now - stateEnteredAtMs > ListLoadTimeoutMs)
        {
            if (openDlg != null && openDlg->IsVisible)
                openDlg->Close(true);
            Plugin.Friends.RequestRefresh();
            FailCurrentTarget(L.T("Timed out waiting for group update.", "Tempo esgotado aguardando atualização do grupo."));
        }
    }

    private void HandleWaitingForConfirmDialog(long now)
    {
        var ynPtr = Plugin.GameGui.GetAddonByName("SelectYesno", 1);
        if (ynPtr != nint.Zero)
        {
            var yn = (AddonSelectYesno*)ynPtr.Address;
            if (yn != null && yn->IsVisible && yn->IsReady)
            {
                var promptText = string.Empty;
                if (yn->AtkValuesCount > 0 && yn->AtkValues[0].String.Value != null)
                {
                    promptText = MemoryHelper.ReadSeStringNullTerminated((nint)yn->AtkValues[0].String.Value).TextValue;
                }

                // STRICT SAFETY CHECK: Only confirm Yes if the prompt explicitly names our target friend
                if (currentTarget != null && promptText.Contains(currentTarget.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var vals = stackalloc AtkValue[1];
                    vals[0].Type = AtkValueType.Int;
                    vals[0].Int = 0; // 0 = Yes
                    yn->FireCallback(1, vals, true);

                    ResetStabilityTracker();
                    Transition(BulkEngineState.WaitingForRemovalVerify,
                        L.T($"Waiting for Friend List reload after removing {currentTarget.Name}...",
                            $"Aguardando recarregar após remover {currentTarget.Name}..."),
                        550);
                    return;
                }

                var noVals = stackalloc AtkValue[1];
                noVals[0].Type = AtkValueType.Int;
                noVals[0].Int = 1; // 1 = No
                yn->FireCallback(1, noVals, true);

                if (currentRetries < 1)
                {
                    currentRetries++;
                    ResetStabilityTracker();
                    Transition(BulkEngineState.WaitingForListReady,
                        L.T($"List shifted; retrying {currentTarget?.Name}...",
                            $"Lista mudou; tentando {currentTarget?.Name} novamente..."),
                        800);
                    return;
                }

                FailCurrentTarget(L.T("Safety abort: confirmation dialog did not match target name.", "Abortado por segurança: diálogo não corresponde ao alvo."));
                return;
            }
        }

        if (now - stateEnteredAtMs > MenuTimeoutMs)
        {
            FailCurrentTarget(L.T("Confirmation dialog did not appear.", "Diálogo de confirmação não apareceu."));
        }
    }

    private void HandleWaitingForRemovalVerify(long now)
    {
        if (currentTarget == null)
        {
            AdvanceToNextOrFinish();
            return;
        }

        if (GetVisibleAddon("SelectYesno") != null)
            return;

        var proxy = FriendListService.GetFriendProxy();
        if (proxy == null) return;

        var currentCount = (int)proxy->EntryCount;
        var stillPresent = Plugin.Friends.ContainsContentId(currentTarget.ContentId);

        if (!stillPresent)
        {
            var expectedMinCount = Math.Max(0, countBeforeRemoval - 1);
            if (expectedMinCount > 0 && currentCount < expectedMinCount)
            {
                if (now - stateEnteredAtMs <= ListLoadTimeoutMs)
                    return;
            }

            if (currentCount != lastObservedEntryCount)
            {
                lastObservedEntryCount = currentCount;
                entryCountStableSinceMs = now;
                return;
            }

            if (now - entryCountStableSinceMs < ListStableDurationMs && now - stateEnteredAtMs <= ListLoadTimeoutMs)
                return;

            SucceededCount++;
            AddLog(currentTarget.Name, currentTarget.Action, true, L.T("Removed successfully", "Removido com sucesso"));
            Plugin.Friends.RefreshFromMemory();
            currentTarget = null;

            var delay = Math.Clamp(Plugin.Config.RemovalDelayMs, 500, 5000);
            Transition(BulkEngineState.CooldownBeforeNext,
                L.T($"Removed! Waiting {delay / 1000.0:0.0}s...", $"Removido! Aguardando {delay / 1000.0:0.0}s..."),
                delay);
            return;
        }

        if (now - stateEnteredAtMs > ListLoadTimeoutMs)
        {
            Plugin.Friends.RequestRefresh();
            FailCurrentTarget(L.T("Timed out waiting for server confirmation.", "Tempo esgotado aguardando confirmação do servidor."));
        }
    }

    private void FailCurrentTarget(string reason)
    {
        if (currentTarget != null)
        {
            FailedCount++;
            AddLog(currentTarget.Name, currentTarget.Action, false, reason);
            currentTarget = null;
        }

        var delay = Math.Clamp(Plugin.Config.RemovalDelayMs, 500, 5000);
        Transition(BulkEngineState.CooldownBeforeNext, reason, delay);
    }

    private void AdvanceToNextOrFinish()
    {
        if (queue.Count == 0)
        {
            FinishBatch();
            return;
        }

        Transition(BulkEngineState.OpeningFriendList, L.T("Preparing next friend...", "Preparando próximo amigo..."), 100);
    }

    private void FinishBatch()
    {
        var finishedKind = CurrentBatchKind;
        state = BulkEngineState.Idle;
        currentTarget = null;
        CurrentBatchKind = BulkActionKind.None;

        if (Plugin.Config.CloseNativeListAfterBatch)
        {
            var agent = AgentFriendlist.Instance();
            if (agent != null && agent->IsAddonShown())
                agent->Hide();
        }

        Plugin.Friends.RequestRefresh();
        Plugin.Friends.RefreshFromMemory();

        StatusMessage = finishedKind == BulkActionKind.RemoveFriend
            ? L.T($"Done! Removed: {SucceededCount}, Skipped/Failed: {FailedCount}.", $"Concluído! Removidos: {SucceededCount}, Ignorados/Falhas: {FailedCount}.")
            : L.T($"Done! Updated: {SucceededCount}, Failed: {FailedCount}.", $"Concluído! Atualizados: {SucceededCount}, Falhas: {FailedCount}.");

        if (Plugin.Config.PrintChatSummary)
        {
            Plugin.ChatGui.Print($"[FriendList Frenzy] {StatusMessage}");
        }
    }

    private static byte GetLiveNativeGroup(ulong contentId)
    {
        foreach (var f in Plugin.Friends.Friends)
        {
            if (f.ContentId == contentId)
                return f.NativeGroup;
        }
        return 0;
    }

    private bool IsRemoveFriendLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (removeFriendLabels.Contains(text)) return true;

        return text.Contains("Remove from Friend List", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Remove Friend", StringComparison.OrdinalIgnoreCase)
            || text.Contains("フレンドリストから削除", StringComparison.Ordinal)
            || text.Contains("Freundesliste entfernen", StringComparison.OrdinalIgnoreCase)
            || text.Contains("liste d'amis", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAssignGroupLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (assignGroupLabels.Contains(text)) return true;

        return text.Contains("Friend Group", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Freundesgruppe", StringComparison.OrdinalIgnoreCase)
            || text.Contains("groupe d'amis", StringComparison.OrdinalIgnoreCase)
            || text.Contains("フレンドグループ", StringComparison.Ordinal);
    }

    private void SnapshotVisibleAddonIds()
    {
        visibleAddonIdsBefore.Clear();
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return;
        ref var loaded = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < loaded.Count; i++)
        {
            var unit = loaded.Entries[i].Value;
            if (unit != null && unit->IsVisible)
                visibleAddonIdsBefore.Add(unit->Id);
        }
    }

    private AtkUnitBase* FindFriendGroupDialog()
    {
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return null;
        ref var loaded = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;

        // Pass 1: Look for any newly opened visible ready addon that has an 8-item list (None + 7 Groups)
        AtkUnitBase* fallbackCandidate = null;
        for (var i = 0; i < loaded.Count; i++)
        {
            var unit = loaded.Entries[i].Value;
            if (unit == null || !unit->IsVisible || !unit->IsReady) continue;
            if (visibleAddonIdsBefore.Contains(unit->Id)) continue;

            var name = unit->NameString;
            if (name.Equals("ContextMenu", StringComparison.OrdinalIgnoreCase)
                || name.Equals("FriendList", StringComparison.OrdinalIgnoreCase))
                continue;

            if (HasEightItemGroupList(unit))
            {
                Plugin.Log.Debug($"[FriendListFrenzy] Detected Friend Groups dialog addon: '{name}' (Id={unit->Id}, AtkValues={unit->AtkValuesCount})");
                return unit;
            }

            if (name.Contains("Friend", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Group", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Social", StringComparison.OrdinalIgnoreCase))
            {
                fallbackCandidate = unit;
            }
        }

        return fallbackCandidate;
    }

    private static AtkUnitBase* FindVisibleAddonById(ushort id)
    {
        var stage = AtkStage.Instance();
        if (stage == null || stage->RaptureAtkUnitManager == null) return null;
        ref var loaded = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < loaded.Count; i++)
        {
            var unit = loaded.Entries[i].Value;
            if (unit != null && unit->Id == id && unit->IsVisible)
                return unit;
        }
        return null;
    }

    private static bool HasEightItemGroupList(AtkUnitBase* unit)
    {
        for (var i = 0; i < unit->UldManager.NodeListCount; i++)
        {
            var node = unit->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Component) continue;
            var compNode = (AtkComponentNode*)node;
            if (compNode->Component == null || compNode->Component->GetComponentType() != ComponentType.List) continue;
            var list = (AtkComponentList*)compNode->Component;
            if (list->ListLength == 8)
                return true;
        }
        return false;
    }

    private static void SelectGroupRowInDialog(AtkUnitBase* unit, int rowIndex)
    {
        for (var i = 0; i < unit->UldManager.NodeListCount; i++)
        {
            var node = unit->UldManager.NodeList[i];
            if (node == null || node->Type != NodeType.Component) continue;
            var compNode = (AtkComponentNode*)node;
            if (compNode->Component == null || compNode->Component->GetComponentType() != ComponentType.List) continue;
            var list = (AtkComponentList*)compNode->Component;
            if (list->ListLength == 8)
            {
                list->SelectItem(rowIndex);
                return;
            }
        }
    }

    private static AtkUnitBase* GetVisibleAddon(string name)
    {
        var ptr = Plugin.GameGui.GetAddonByName(name, 1);
        if (ptr == nint.Zero) return null;
        var unit = (AtkUnitBase*)ptr.Address;
        return unit != null && unit->IsVisible ? unit : null;
    }
}
