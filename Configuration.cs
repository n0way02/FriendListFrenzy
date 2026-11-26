using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;
using FriendListFrenzy.Models;
using FriendListFrenzy.Services;

namespace FriendListFrenzy;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>"en" (default) or "pt"</summary>
    public string Language { get; set; } = "en";

    /// <summary>Delay in ms between each bulk native FriendList operation (safe default 1000ms).</summary>
    public int RemovalDelayMs { get; set; } = 1000;

    /// <summary>Close the native FriendList window when a bulk batch finishes.</summary>
    public bool CloseNativeListAfterBatch { get; set; } = true;

    /// <summary>Print a summary in FFXIV chat when bulk operations finish.</summary>
    public bool PrintChatSummary { get; set; } = true;

    /// <summary>Show FC tag next to friend names.</summary>
    public bool ShowFcTag { get; set; } = true;

    /// <summary>Show custom nickname if set.</summary>
    public bool ShowNickname { get; set; } = true;

    /// <summary>Show Last Seen column.</summary>
    public bool ShowLastSeen { get; set; } = true;

    /// <summary>Custom display names & colors for FFXIV's 7 native Friend Groups (keys 1..7).</summary>
    public Dictionary<byte, NativeGroupCustomization> NativeGroups { get; set; } = new();

    /// <summary>Legacy local groups kept for backwards JSON compatibility.</summary>
    public List<FriendGroup> Groups { get; set; } = new();

    /// <summary>Per-friend metadata keyed by ContentId.</summary>
    public Dictionary<ulong, FriendMetadata> FriendsMeta { get; set; } = new();

    public FriendMetadata GetOrCreateMeta(ulong contentId, string currentName)
    {
        if (!FriendsMeta.TryGetValue(contentId, out var meta))
        {
            meta = new FriendMetadata
            {
                ContentId = contentId,
                LastKnownName = currentName,
                FirstSeenUtc = DateTime.UtcNow,
            };
            FriendsMeta[contentId] = meta;
        }
        else if (!string.IsNullOrWhiteSpace(currentName))
        {
            meta.LastKnownName = currentName;
        }

        return meta;
    }

    public NativeGroupCustomization GetOrCreateNativeGroup(byte groupId)
    {
        if (!NativeGroups.TryGetValue(groupId, out var custom))
        {
            var def = NativeFriendGroups.GetDef(groupId);
            custom = new NativeGroupCustomization
            {
                CustomName = string.Empty,
                Color = def?.DefaultColor ?? Vector4.One,
                UseCustomColor = false,
            };
            NativeGroups[groupId] = custom;
        }
        return custom;
    }

    public string GetNativeGroupLabel(byte groupId, bool includeSymbol = true)
    {
        var def = NativeFriendGroups.GetDef(groupId);
        if (def == null)
            return L.T("Ungrouped", "Sem Grupo");

        var customName = NativeGroups.TryGetValue(groupId, out var c) && !string.IsNullOrWhiteSpace(c.CustomName)
            ? c.CustomName.Trim()
            : L.T(def.Value.DefaultNameEn, def.Value.DefaultNamePt);

        return includeSymbol ? $"{def.Value.Symbol} {customName}" : customName;
    }

    public Vector4 GetNativeGroupColor(byte groupId)
    {
        var def = NativeFriendGroups.GetDef(groupId);
        if (def == null)
            return new Vector4(0.62f, 0.58f, 0.72f, 1f);

        if (NativeGroups.TryGetValue(groupId, out var c) && c.UseCustomColor)
            return c.Color;

        return def.Value.DefaultColor;
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
