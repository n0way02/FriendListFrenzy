using System;
using System.Collections.Generic;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FriendListFrenzy.Models;
using Lumina.Excel.Sheets;

namespace FriendListFrenzy.Services;

public enum BulkRemoveState
{
    Idle,
    OpeningFriendList,
    WaitingForListReady,
    OpeningContextMenu,
    WaitingForContextMenu,
    WaitingForConfirmDialog,
    WaitingForRemovalVerify,
    CooldownBeforeNext,
    Paused,
}

public sealed class BulkRemovalTarget
{
    public ulong ContentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string World { get; init; } = string.Empty;
}

public sealed class BulkRemovalLogItem
{
    public DateTime TimeLocal { get; init; } = DateTime.Now;
    public string Name { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Safely removes multiple selected friends one-by-one via FFXIV's native FriendList UI,
/// ContextMenu, and SelectYesno confirmation — with strict name & ContentId safety checks
/// and full FriendList reload stabilization between every removal.
/// </summary>
public sealed unsafe class BulkRemoverService : IDisposable
{
    private const long MenuTimeoutMs = 5_000;
    private const long ListLoadTimeoutMs = 15_000;
    private const long ListStableDurationMs = 750;

    private readonly Queue<BulkRemovalTarget> queue = new();
    private readonly List<BulkRemovalLogItem> log = new();
    private readonly List<string> removeMenuLabels = new();

    private uint friendListMainCommandId = 29; // Official FFXIV MainCommand RowId for Friend List
    private BulkRemovalTarget? currentTarget;
    private BulkRemoveState state = BulkRemoveState.Idle;
    private BulkRemoveState resumeState = BulkRemoveState.Idle;
    private long stateEnteredAtMs;
    private long nextStepAtMs;
    private int currentRetries;

    // Reload stabilization tracking
    private int countBeforeRemoval;
    private int lastObservedEntryCount = -1;
    private long entryCountStableSinceMs;

    public BulkRemoveState State => state;
    public bool IsActive => state != BulkRemoveState.Idle;
    public bool IsPaused => state == BulkRemoveState.Paused;
    public int TotalQueued { get; private set; }
    public int RemovedCount { get; private set; }
    public int FailedCount { get; private set; }
    public int RemainingCount => queue.Count + (currentTarget != null ? 1 : 0);
    public BulkRemovalTarget? CurrentTarget => currentTarget;
    public string StatusMessage { get; private set; } = string.Empty;
    public IReadOnlyList<BulkRemovalLogItem> RecentLog => log;

    public BulkRemoverService()
    {
        BuildLocalizedLabelsAndCommand();
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.ClientState.Logout += OnLogout;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.ClientState.Logout -= OnLogout;
    }

    private void OnLogout(int type, int code)
    {
        Cancel("Logged out.");
    }

    private void BuildLocalizedLabelsAndCommand()
    {
        // Multilingual fallbacks (EN, JP, DE, FR)
        removeMenuLabels.Add("Remove from Friend List");
        removeMenuLabels.Add("Remove Friend");
        removeMenuLabels.Add("フレンドリストから削除");
        removeMenuLabels.Add("Aus der Freundesliste entfernen");
        removeMenuLabels.Add("Supprimer de la liste d'amis");

        try
        {
            var addonSheet = Plugin.DataManager.GetExcelSheet<Addon>();
            if (addonSheet != null)
            {
                foreach (var row in addonSheet)
                {
                    var text = row.Text.ToString();
                    if (string.IsNullOrWhiteSpace(text) || text.Length > 48) continue;
                    if (text.Contains("Remove from Friend List", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("フレンドリストから削除", StringComparison.Ordinal)
                        || text.Contains("Aus der Freundesliste entfernen", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("Supprimer de la liste d'amis", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!removeMenuLabels.Contains(text))
                            removeMenuLabels.Insert(0, text);
                    }
                }
            }

            var mainCmdSheet = Plugin.DataManager.GetExcelSheet<MainCommand>();
            if (mainCmdSheet != null)
            {
                foreach (var row in mainCmdSheet)
                {
                    var name = row.Name.ToString();
                    if (name.Equals("Friend List", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("フレンドリスト", StringComparison.Ordinal)
                        || name.Equals("Freundesliste", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Liste d'amis", StringComparison.OrdinalIgnoreCase))
                    {
                        friendListMainCommandId = row.RowId;
                        break;
                    }
                }
            }
        }
        catch
        {
            // Keep defaults
        }
    }

    public void StartBatch(IEnumerable<FriendEntry> targets)
    {
        if (IsActive) return;

        queue.Clear();
        log.Clear();
        RemovedCount = 0;
        FailedCount = 0;
        currentTarget = null;
        ResetStabilityTracker();

        foreach (var f in targets)
        {
            var meta = Plugin.Config.GetOrCreateMeta(f.ContentId, f.Name);
            if (meta.IsProtected) continue;

            queue.Enqueue(new BulkRemovalTarget
            {
                ContentId = f.ContentId,
                Name = f.Name,
                World = f.HomeWorldName,
            });
        }

        TotalQueued = queue.Count;
        if (TotalQueued == 0)
        {
            StatusMessage = L.T("No removable friends selected.", "Nenhum amigo removível selecionado.");
            return;
        }

        Transition(BulkRemoveState.OpeningFriendList,
            L.T($"Starting bulk removal ({TotalQueued} friends)...", $"Iniciando remoção em massa ({TotalQueued} amigos)..."));
    }

    public void Pause()
    {
        if (!IsActive || IsPaused) return;
        resumeState = state;
        state = BulkRemoveState.Paused;
        StatusMessage = L.T("Paused by user.", "Pausado pelo usuário.");
    }

    public void Resume()
    {
        if (!IsPaused) return;
        state = resumeState == BulkRemoveState.Paused ? BulkRemoveState.OpeningFriendList : resumeState;
        stateEnteredAtMs = Environment.TickCount64;
        nextStepAtMs = Environment.TickCount64 + 300;
        ResetStabilityTracker();
        StatusMessage = L.T("Resuming...", "Retomando...");
    }

    public void Cancel(string? reason = null)
    {
        if (!IsActive) return;
        queue.Clear();
        currentTarget = null;
        state = BulkRemoveState.Idle;
        StatusMessage = reason ?? L.T("Cancelled by user.", "Cancelado pelo usuário.");
        AddLog("Batch", false, StatusMessage);
    }

    private void Transition(BulkRemoveState next, string message, long delayMs = 0)
    {
        state = next;
        stateEnteredAtMs = Environment.TickCount64;
        nextStepAtMs = Environment.TickCount64 + delayMs;
        StatusMessage = message;
    }

    private void ResetStabilityTracker()
    {
        lastObservedEntryCount = -1;
        entryCountStableSinceMs = Environment.TickCount64;
    }

    private void AddLog(string name, bool success, string message)
    {
        log.Insert(0, new BulkRemovalLogItem
        {
            TimeLocal = DateTime.Now,
            Name = name,
            Success = success,
            Message = message,
        });
        if (log.Count > 200) log.RemoveAt(log.Count - 1);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (state == BulkRemoveState.Idle || state == BulkRemoveState.Paused) return;

        var now = Environment.TickCount64;
        if (now < nextStepAtMs) return;

        try
        {
            switch (state)
            {
                case BulkRemoveState.OpeningFriendList:
                    HandleOpeningFriendList();
                    break;
                case BulkRemoveState.WaitingForListReady:
                    HandleWaitingForListReady(now);
                    break;
                case BulkRemoveState.OpeningContextMenu:
                    HandleOpeningContextMenu();
                    break;
                case BulkRemoveState.WaitingForContextMenu:
                    HandleWaitingForContextMenu(now);
                    break;
                case BulkRemoveState.WaitingForConfirmDialog:
                    HandleWaitingForConfirmDialog(now);
                    break;
                case BulkRemoveState.WaitingForRemovalVerify:
                    HandleWaitingForRemovalVerify(now);
                    break;
                case BulkRemoveState.CooldownBeforeNext:
                    AdvanceToNextOrFinish();
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[FriendListFrenzy] Bulk remover error");
            FailCurrentTarget($"Error: {ex.Message}");
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

        // Open native Friend List window reliably even if completely closed
        var addon = GetVisibleAddon("FriendList");
        if (addon == null)
        {
            var uiModule = UIModule.Instance();
            if (uiModule != null)
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
        Transition(BulkRemoveState.WaitingForListReady,
            L.T($"Waiting for Friend List to load for {currentTarget.Name}...", $"Aguardando Lista de Amigos carregar para {currentTarget.Name}..."),
            500);
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

        // Wait if any popup/context menu from previous action is still closing
        if (GetVisibleAddon("ContextMenu") != null || GetVisibleAddon("SelectYesno") != null)
            return;

        var proxy = FriendListService.GetFriendProxy();
        if (proxy == null) return;

        // Ensure native FriendList filter is set to "All" (group 0) so grouped friends are always visible
        if (proxy->FilterGroup != 0)
        {
            proxy->FilterGroup = 0;
            proxy->ApplyFilters();
            ResetStabilityTracker();
            nextStepAtMs = now + 450;
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

        // Track stability: wait until EntryCount stops changing for ListStableDurationMs
        if (currentCount != lastObservedEntryCount)
        {
            lastObservedEntryCount = currentCount;
            entryCountStableSinceMs = now;
            return;
        }

        if (now - entryCountStableSinceMs < ListStableDurationMs)
            return;

        // List is loaded and stable! Refresh our mirror and check if target is still present.
        Plugin.Friends.RefreshFromMemory();

        if (!Plugin.Friends.ContainsContentId(currentTarget.ContentId))
        {
            RemovedCount++;
            AddLog(currentTarget.Name, true, L.T("Already removed", "Já removido"));
            currentTarget = null;
            Transition(BulkRemoveState.CooldownBeforeNext, L.T("Moving to next...", "Indo para o próximo..."), 250);
            return;
        }

        Transition(BulkRemoveState.OpeningContextMenu,
            L.T($"Selecting {currentTarget.Name}...", $"Selecionando {currentTarget.Name}..."),
            150);
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
            Transition(BulkRemoveState.OpeningFriendList, L.T("Re-opening Friend List...", "Reabrindo Lista de Amigos..."), 350);
            return;
        }

        var proxy = FriendListService.GetFriendProxy();
        if (proxy == null) return;

        countBeforeRemoval = (int)proxy->EntryCount;

        var rowIdx = Plugin.Friends.FindDisplayRowIndex(currentTarget.ContentId);
        if (rowIdx < 0)
        {
            // Wait for reload instead of assuming removed if list is refreshing
            if (currentRetries < 1)
            {
                currentRetries++;
                ResetStabilityTracker();
                Transition(BulkRemoveState.WaitingForListReady,
                    L.T($"Waiting for {currentTarget.Name} to appear in list...", $"Aguardando {currentTarget.Name} aparecer na lista..."),
                    600);
                return;
            }

            RemovedCount++;
            AddLog(currentTarget.Name, true, L.T("Not in Friend List anymore", "Não está mais na lista"));
            currentTarget = null;
            Transition(BulkRemoveState.CooldownBeforeNext, L.T("Next friend...", "Próximo amigo..."), 300);
            return;
        }

        // Open right-click ContextMenu on rowIdx in native FriendList addon
        var values = stackalloc AtkValue[2];
        values[0].Type = AtkValueType.Int;
        values[0].Int = 1; // 1 = Open ContextMenu for entry
        values[1].Type = AtkValueType.Int;
        values[1].Int = rowIdx;
        addon->FireCallback(2, values, true);

        Transition(BulkRemoveState.WaitingForContextMenu,
            L.T($"Opening menu for {currentTarget.Name} (row {rowIdx})...", $"Abrindo menu para {currentTarget.Name} (linha {rowIdx})..."),
            220);
    }

    private void HandleWaitingForContextMenu(long now)
    {
        var cmPtr = Plugin.GameGui.GetAddonByName("ContextMenu", 1);
        if (cmPtr != nint.Zero)
        {
            var cm = (AddonContextMenu*)cmPtr.Address;
            if (cm != null && cm->IsVisible && cm->IsReady)
            {
                var entriesCount = (int)cm->AtkValues[0].UInt;
                var foundIdx = -1;

                for (var i = 0; i < entriesCount; i++)
                {
                    var valIdx = i + 8;
                    if (valIdx >= cm->AtkValuesCount) break;
                    var strPtr = cm->AtkValues[valIdx].String.Value;
                    if (strPtr == null) continue;

                    var text = MemoryHelper.ReadSeStringNullTerminated((nint)strPtr).TextValue.Trim();
                    if (IsRemoveFriendLabel(text))
                    {
                        foundIdx = i;
                        break;
                    }
                }

                if (foundIdx >= 0)
                {
                    var vals = stackalloc AtkValue[3];
                    vals[0].Type = AtkValueType.Int;
                    vals[0].Int = 0;
                    vals[1].Type = AtkValueType.Int;
                    vals[1].Int = foundIdx;
                    vals[2].Type = AtkValueType.Int;
                    vals[2].Int = 0;
                    cm->FireCallback(3, vals, true);

                    Transition(BulkRemoveState.WaitingForConfirmDialog,
                        L.T($"Confirming removal of {currentTarget?.Name}...", $"Confirmando remoção de {currentTarget?.Name}..."),
                        250);
                    return;
                }

                // Close unexpected ContextMenu
                cm->Close(true);
                FailCurrentTarget(L.T("'Remove from Friend List' option not found in menu.", "Opção 'Remover da Lista de Amigos' não encontrada no menu."));
                return;
            }
        }

        if (now - stateEnteredAtMs > MenuTimeoutMs)
        {
            if (currentRetries < 1)
            {
                currentRetries++;
                ResetStabilityTracker();
                Transition(BulkRemoveState.WaitingForListReady,
                    L.T($"Retrying menu for {currentTarget?.Name}...", $"Tentando novamente menu para {currentTarget?.Name}..."),
                    400);
            }
            else
            {
                FailCurrentTarget(L.T("Context menu did not open.", "Menu de contexto não abriu."));
            }
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

                // STRICT SAFETY CHECK: Only confirm Yes if the prompt explicitly names our target friend!
                if (currentTarget != null && promptText.Contains(currentTarget.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var vals = stackalloc AtkValue[1];
                    vals[0].Type = AtkValueType.Int;
                    vals[0].Int = 0; // 0 = Yes
                    yn->FireCallback(1, vals, true);

                    ResetStabilityTracker();
                    Transition(BulkRemoveState.WaitingForRemovalVerify,
                        L.T($"Waiting for Friend List to finish reloading after removing {currentTarget.Name}...",
                            $"Aguardando Lista de Amigos recarregar após remover {currentTarget.Name}..."),
                        550);
                    return;
                }

                // Dialog did not match target (e.g. list shifted while loading) -> close No and retry once after reload
                var noVals = stackalloc AtkValue[1];
                noVals[0].Type = AtkValueType.Int;
                noVals[0].Int = 1; // 1 = No
                yn->FireCallback(1, noVals, true);

                if (currentRetries < 1)
                {
                    currentRetries++;
                    ResetStabilityTracker();
                    Transition(BulkRemoveState.WaitingForListReady,
                        L.T($"List shifted; waiting for reload and retrying {currentTarget?.Name}...",
                            $"Lista mudou durante carregamento; aguardando recarregar para tentar {currentTarget?.Name} novamente..."),
                        800);
                    return;
                }

                FailCurrentTarget(L.T("Safety abort: confirmation dialog did not match target name.", "Abortado por segurança: diálogo não corresponde ao nome do alvo."));
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

        // Wait until SelectYesno finishes closing
        if (GetVisibleAddon("SelectYesno") != null)
            return;

        var proxy = FriendListService.GetFriendProxy();
        if (proxy == null) return;

        var currentCount = (int)proxy->EntryCount;
        var stillPresent = Plugin.Friends.ContainsContentId(currentTarget.ContentId);

        if (!stillPresent)
        {
            // Wait until the server finishes repopulating the Friend List (avoid moving on while EntryCount is 0 or still streaming)
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

            RemovedCount++;
            AddLog(currentTarget.Name, true, L.T("Removed successfully", "Removido com sucesso"));
            Plugin.Friends.RefreshFromMemory();
            currentTarget = null;

            var delay = Math.Clamp(Plugin.Config.RemovalDelayMs, 600, 5000);
            Transition(BulkRemoveState.CooldownBeforeNext,
                L.T($"Removed! Waiting {delay / 1000.0:0.0}s before next...", $"Removido! Aguardando {delay / 1000.0:0.0}s para o próximo..."),
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
            AddLog(currentTarget.Name, false, reason);
            currentTarget = null;
        }

        var delay = Math.Clamp(Plugin.Config.RemovalDelayMs, 600, 5000);
        Transition(BulkRemoveState.CooldownBeforeNext, reason, delay);
    }

    private void AdvanceToNextOrFinish()
    {
        if (queue.Count == 0)
        {
            FinishBatch();
            return;
        }

        Transition(BulkRemoveState.OpeningFriendList, L.T("Preparing next friend...", "Preparando próximo amigo..."), 100);
    }

    private void FinishBatch()
    {
        state = BulkRemoveState.Idle;
        currentTarget = null;

        if (Plugin.Config.CloseNativeListAfterBatch)
        {
            var agent = AgentFriendlist.Instance();
            if (agent != null && agent->IsAddonShown())
                agent->Hide();
        }

        Plugin.Friends.RequestRefresh();
        Plugin.Friends.RefreshFromMemory();

        StatusMessage = L.T(
            $"Done! Removed: {RemovedCount}, Skipped/Failed: {FailedCount}.",
            $"Concluído! Removidos: {RemovedCount}, Ignorados/Falhas: {FailedCount}.");

        if (Plugin.Config.PrintChatSummary)
        {
            Plugin.ChatGui.Print($"[FriendList Frenzy] {StatusMessage}");
        }
    }

    private bool IsRemoveFriendLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var label in removeMenuLabels)
        {
            if (text.Equals(label, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return text.Contains("Remove from Friend", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Remove Friend", StringComparison.OrdinalIgnoreCase)
            || text.Contains("フレンドリストから削除", StringComparison.Ordinal)
            || text.Contains("Freundesliste entfernen", StringComparison.OrdinalIgnoreCase)
            || text.Contains("liste d'amis", StringComparison.OrdinalIgnoreCase);
    }

    private static AtkUnitBase* GetVisibleAddon(string name)
    {
        var ptr = Plugin.GameGui.GetAddonByName(name, 1);
        if (ptr == nint.Zero) return null;
        var unit = (AtkUnitBase*)ptr.Address;
        return unit != null && unit->IsVisible ? unit : null;
    }
}
