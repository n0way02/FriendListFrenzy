using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FriendListFrenzy.Models;
using Lumina.Excel.Sheets;

namespace FriendListFrenzy.Services;

public sealed unsafe class FriendListService : IDisposable
{
    private const long PollIntervalMs = 2000;

    private readonly Dictionary<ushort, string> worldNames = new();
    private readonly Dictionary<uint, string> jobAbbrevs = new();
    private readonly Dictionary<ushort, string> locationNames = new();
    private readonly object listLock = new();

    private List<FriendEntry> cachedFriends = new();
    private long lastPollMs;
    private long lastSaveMs;
    private bool dirtyMeta;

    public int TotalCount { get; private set; }
    public int OnlineCount { get; private set; }
    public bool HasLoadedData { get; private set; }
    public DateTime LastUpdatedLocal { get; private set; } = DateTime.MinValue;

    public FriendListService()
    {
        BuildLuminaCaches();
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.ClientState.Logout += OnLogout;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.ClientState.Logout -= OnLogout;
        if (dirtyMeta)
        {
            Plugin.Config.Save();
            dirtyMeta = false;
        }
    }

    private void OnLogout(int type, int code)
    {
        lock (listLock)
        {
            cachedFriends.Clear();
            TotalCount = 0;
            OnlineCount = 0;
            HasLoadedData = false;
        }
    }

    private void BuildLuminaCaches()
    {
        try
        {
            var worlds = Plugin.DataManager.GetExcelSheet<World>();
            if (worlds != null)
            {
                foreach (var w in worlds)
                {
                    if (w.RowId > 0 && !string.IsNullOrEmpty(w.Name.ToString()))
                        worldNames[(ushort)w.RowId] = w.Name.ToString();
                }
            }

            var jobs = Plugin.DataManager.GetExcelSheet<ClassJob>();
            if (jobs != null)
            {
                foreach (var j in jobs)
                {
                    var abbr = j.Abbreviation.ToString();
                    if (!string.IsNullOrEmpty(abbr))
                        jobAbbrevs[j.RowId] = abbr;
                }
            }

            var places = Plugin.DataManager.GetExcelSheet<PlaceName>();
            if (places != null)
            {
                foreach (var p in places)
                {
                    var name = p.Name.ToString();
                    if (p.RowId > 0 && !string.IsNullOrEmpty(name))
                        locationNames[(ushort)p.RowId] = name;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[FriendListFrenzy] Failed to populate Lumina caches");
        }
    }

    public static InfoProxyFriendList* GetFriendProxy()
    {
        var module = InfoModule.Instance();
        if (module == null) return null;
        return (InfoProxyFriendList*)module->GetInfoProxyById(InfoProxyId.FriendList);
    }

    public void RequestRefresh()
    {
        try
        {
            var proxy = GetFriendProxy();
            if (proxy != null)
            {
                proxy->RequestData();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[FriendListFrenzy] RequestRefresh failed");
        }
    }

    public bool ContainsContentId(ulong contentId)
    {
        var proxy = GetFriendProxy();
        if (proxy == null) return false;

        var count = Math.Min((int)proxy->EntryCount, proxy->CharDataSpan.Length);
        for (var i = 0; i < count; i++)
        {
            if (proxy->CharDataSpan[i].ContentId == contentId)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves the live UI list display index for a given ContentId in the native FriendList addon.
    /// Uses CharIndexSpan (the active sorted/filtered order shown in AddonFriendList), falling back to CharDataSpan.
    /// </summary>
    public int FindDisplayRowIndex(ulong contentId)
    {
        var proxy = GetFriendProxy();
        if (proxy == null) return -1;

        var entryCount = Math.Min((int)proxy->EntryCount, proxy->CharDataSpan.Length);
        for (var displayRow = 0; displayRow < entryCount; displayRow++)
        {
            if (proxy->GetContentIdForEntry((uint)displayRow) == contentId)
                return displayRow;
        }

        // Fallback: linear scan of CharDataSpan
        for (var i = 0; i < entryCount; i++)
        {
            if (proxy->CharDataSpan[i].ContentId == contentId)
                return i;
        }

        return -1;
    }

    public List<FriendEntry> GetSnapshot()
    {
        lock (listLock)
        {
            return new List<FriendEntry>(cachedFriends);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (now - lastPollMs < PollIntervalMs) return;
        lastPollMs = now;

        if (!Plugin.ClientState.IsLoggedIn) return;

        RefreshFromMemory();

        if (dirtyMeta && now - lastSaveMs > 15_000)
        {
            Plugin.Config.Save();
            dirtyMeta = false;
            lastSaveMs = now;
        }
    }

    public void RefreshFromMemory()
    {
        var proxy = GetFriendProxy();
        if (proxy == null) return;

        var count = Math.Min((int)proxy->EntryCount, proxy->CharDataSpan.Length);
        var list = new List<FriendEntry>(count);
        var online = 0;
        var utcNow = DateTime.UtcNow;

        for (var i = 0; i < count; i++)
        {
            ref readonly var data = ref proxy->CharDataSpan[i];
            if (data.ContentId == 0) continue;

            var name = data.NameString;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var state = MapOnlineState(data.State);
            var isOnline = state != FriendOnlineState.Offline;
            if (isOnline) online++;

            worldNames.TryGetValue(data.HomeWorld, out var homeWorld);
            worldNames.TryGetValue(data.CurrentWorld, out var currentWorld);
            jobAbbrevs.TryGetValue(data.Job, out var jobAbbr);
            locationNames.TryGetValue(data.Location, out var locName);

            list.Add(new FriendEntry
            {
                ContentId = data.ContentId,
                Name = name,
                HomeWorldId = data.HomeWorld,
                HomeWorldName = homeWorld ?? $"World #{data.HomeWorld}",
                CurrentWorldId = data.CurrentWorld,
                CurrentWorldName = currentWorld ?? string.Empty,
                JobId = data.Job,
                JobAbbrev = isOnline ? (jobAbbr ?? string.Empty) : string.Empty,
                LocationId = data.Location,
                LocationName = isOnline ? (locName ?? string.Empty) : string.Empty,
                FcTag = data.FCTagString,
                NativeGroup = (byte)data.Group,
                OnlineState = state,
                RawIndex = i,
            });

            // Track FirstSeen & LastSeenOnline automatically
            var meta = Plugin.Config.GetOrCreateMeta(data.ContentId, name);
            if (isOnline)
            {
                if (meta.LastSeenOnlineUtc == null || (utcNow - meta.LastSeenOnlineUtc.Value).TotalMinutes >= 5)
                {
                    meta.LastSeenOnlineUtc = utcNow;
                    dirtyMeta = true;
                }
            }
        }

        lock (listLock)
        {
            cachedFriends = list;
            TotalCount = list.Count;
            OnlineCount = online;
            HasLoadedData = list.Count > 0 || HasLoadedData;
            LastUpdatedLocal = DateTime.Now;
        }
    }

    private static FriendOnlineState MapOnlineState(InfoProxyCommonList.CharacterData.OnlineStatus status)
    {
        if (status == InfoProxyCommonList.CharacterData.OnlineStatus.Offline)
            return FriendOnlineState.Offline;

        if ((status & InfoProxyCommonList.CharacterData.OnlineStatus.Busy) != 0)
            return FriendOnlineState.Busy;

        if ((status & InfoProxyCommonList.CharacterData.OnlineStatus.AwayFromKeyboard) != 0)
            return FriendOnlineState.Away;

        if ((status & InfoProxyCommonList.CharacterData.OnlineStatus.InDuty) != 0
            || (status & InfoProxyCommonList.CharacterData.OnlineStatus.ViewingCutscene) != 0)
            return FriendOnlineState.InDuty;

        return FriendOnlineState.Online;
    }
}
