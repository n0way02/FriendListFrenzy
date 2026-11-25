using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Configuration;

namespace FriendListFrenzy;

[Serializable]
public sealed class FriendGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New Group";
    public Vector4 Color { get; set; } = new(1.000f, 0.902f, 0.000f, 1.000f); // #FFE600
}

[Serializable]
public sealed class FriendMeta
{
    public ulong ContentId { get; set; }
    public string LastKnownName { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public bool IsProtected { get; set; }
    public bool UseCustomColor { get; set; }
    public Vector4 CustomColor { get; set; } = new(1.000f, 0.902f, 0.000f, 1.000f);
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenOnlineUtc { get; set; }
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Default English ("en"), toggleable to Portuguese ("pt").</summary>
    public string Language { get; set; } = "en";

    /// <summary>Delay in milliseconds between each bulk friend removal (server-friendly pacing).</summary>
    public int RemovalDelayMs { get; set; } = 1500;

    /// <summary>Automatically close the native FriendList window when bulk removal finishes.</summary>
    public bool CloseNativeListAfterBatch { get; set; } = true;

    /// <summary>Show a chat summary when a bulk removal batch finishes.</summary>
    public bool PrintChatSummary { get; set; } = true;

    /// <summary>Custom friend groups.</summary>
    public List<FriendGroup> Groups { get; set; } =
    [
        new() { Id = "favorites", Name = "Favorites", Color = new Vector4(1.000f, 0.902f, 0.000f, 1f) },
        new() { Id = "fc",        Name = "Free Company", Color = new Vector4(0.486f, 0.165f, 0.910f, 1f) },
        new() { Id = "static",    Name = "Static / Raid", Color = new Vector4(0.290f, 0.871f, 0.502f, 1f) },
        new() { Id = "cleanup",   Name = "To Remove", Color = new Vector4(0.937f, 0.267f, 0.267f, 1f) },
    ];

    /// <summary>Metadata keyed by Friend ContentId.</summary>
    public Dictionary<ulong, FriendMeta> FriendMetadata { get; set; } = new();

    public FriendMeta GetOrCreateMeta(ulong contentId, string name)
    {
        if (!FriendMetadata.TryGetValue(contentId, out var meta))
        {
            meta = new FriendMeta
            {
                ContentId = contentId,
                LastKnownName = name,
                FirstSeenUtc = DateTime.UtcNow,
            };
            FriendMetadata[contentId] = meta;
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            meta.LastKnownName = name;
        }

        return meta;
    }

    public FriendGroup? FindGroup(string? groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId)) return null;
        foreach (var g in Groups)
            if (g.Id == groupId) return g;
        return null;
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
