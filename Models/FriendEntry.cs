using System;

namespace FriendListFrenzy.Models;

public enum FriendOnlineState
{
    Offline = 0,
    Online = 1,
    Away = 2,
    Busy = 3,
    InDuty = 4,
}

public sealed class FriendEntry
{
    public ulong ContentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public ushort HomeWorldId { get; init; }
    public string HomeWorldName { get; init; } = string.Empty;
    public ushort CurrentWorldId { get; init; }
    public string CurrentWorldName { get; init; } = string.Empty;
    public byte JobId { get; init; }
    public string JobAbbrev { get; init; } = string.Empty;
    public ushort LocationId { get; init; }
    public string LocationName { get; init; } = string.Empty;
    public string FcTag { get; init; } = string.Empty;
    public byte NativeGroup { get; init; }
    public FriendOnlineState OnlineState { get; init; }
    public bool IsOnline => OnlineState != FriendOnlineState.Offline;
    public int RawIndex { get; init; }

    public string WorldDisplay => IsOnline && CurrentWorldId != 0 && CurrentWorldId != HomeWorldId && !string.IsNullOrEmpty(CurrentWorldName)
        ? $"{HomeWorldName} -> {CurrentWorldName}"
        : HomeWorldName;
}
