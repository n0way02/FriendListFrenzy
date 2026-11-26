using System;
using System.Numerics;

namespace FriendListFrenzy.Models;

/// <summary>
/// Matches FFXIV's native InfoProxyCommonList.CharacterData.DisplayGroup (0..7).
/// </summary>
public static class NativeFriendGroups
{
    public const byte None = 0;
    public const byte Star = 1;
    public const byte Circle = 2;
    public const byte Triangle = 3;
    public const byte Diamond = 4;
    public const byte Heart = 5;
    public const byte Spade = 6;
    public const byte Club = 7;

    public const int MaxGroupId = 7;

    public readonly record struct GroupDef(byte Id, string Symbol, string DefaultNameEn, string DefaultNamePt, Vector4 DefaultColor);

    public static readonly GroupDef[] All =
    [
        new(Star,     "★", "Star",     "Estrela",   new Vector4(1.000f, 0.902f, 0.000f, 1f)), // #FFE600
        new(Circle,   "●", "Circle",   "Círculo",   new Vector4(0.220f, 0.741f, 0.973f, 1f)), // #38BDF8
        new(Triangle, "▲", "Triangle", "Triângulo", new Vector4(0.290f, 0.871f, 0.502f, 1f)), // #4ADE80
        new(Diamond,  "◆", "Diamond",  "Losango",   new Vector4(0.753f, 0.518f, 0.988f, 1f)), // #C084FC
        new(Heart,    "♥", "Heart",    "Coração",   new Vector4(0.984f, 0.443f, 0.522f, 1f)), // #FB7185
        new(Spade,    "♠", "Spade",    "Espadas",   new Vector4(0.506f, 0.549f, 0.973f, 1f)), // #818CF8
        new(Club,     "♣", "Club",     "Paus",      new Vector4(0.984f, 0.573f, 0.235f, 1f)), // #FB923C
    ];

    public static GroupDef? GetDef(byte groupId)
        => groupId >= 1 && groupId <= MaxGroupId ? All[groupId - 1] : null;

    public static string GetSymbol(byte groupId)
        => GetDef(groupId)?.Symbol ?? "○";
}

public enum FriendOnlineState
{
    Offline,
    Online,
    InDuty,
    Away,
    Busy,
}

public enum BulkActionKind
{
    None,
    RemoveFriend,
    SetNativeGroup,
    ClearNativeGroup,
}

public sealed class FriendEntry
{
    public ulong ContentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public ushort HomeWorldId { get; set; }
    public string HomeWorldName { get; set; } = string.Empty;
    public ushort CurrentWorldId { get; set; }
    public string CurrentWorldName { get; set; } = string.Empty;
    public string FcTag { get; set; } = string.Empty;
    public byte JobId { get; set; }
    public string JobAbbrev { get; set; } = string.Empty;
    public string JobAbbr => JobAbbrev;
    public uint LocationId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public FriendOnlineState OnlineState { get; set; } = FriendOnlineState.Offline;
    public int RawIndex { get; set; }

    public bool IsOnline => OnlineState != FriendOnlineState.Offline;
    public bool IsInDuty => OnlineState == FriendOnlineState.InDuty;
    public bool IsAway => OnlineState == FriendOnlineState.Away;
    public bool IsBusy => OnlineState == FriendOnlineState.Busy;

    /// <summary>
    /// FFXIV native Friend List group (0 = Ungrouped, 1..7 = Star..Club).
    /// Read directly from InfoProxyCommonList.CharacterData.Group.
    /// </summary>
    public byte NativeGroup { get; set; }
}

public sealed class FriendMetadata
{
    public ulong ContentId { get; set; }
    public string LastKnownName { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty; // Legacy field kept for JSON compatibility
    public bool IsProtected { get; set; }
    public bool UseCustomColor { get; set; }
    public Vector4 CustomColor { get; set; } = new(1.0f, 0.902f, 0.0f, 1.0f);
    public DateTime? LastSeenOnlineUtc { get; set; }
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class NativeGroupCustomization
{
    public string CustomName { get; set; } = string.Empty;
    public Vector4 Color { get; set; } = Vector4.One;
    public bool UseCustomColor { get; set; }
}

/// <summary>Legacy local group model retained only for config deserialization compatibility.</summary>
public sealed class FriendGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Group";
    public Vector4 Color { get; set; } = new(0.62f, 0.35f, 1.0f, 1.0f);
}

public sealed class BulkActionLogEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string FriendName { get; set; } = string.Empty;
    public BulkActionKind Action { get; set; } = BulkActionKind.RemoveFriend;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
