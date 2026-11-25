using System;

namespace FriendListFrenzy.Services;

public static class L
{
    public static bool IsPt => Plugin.Config.Language == "pt";

    /// <summary>English first (default), Portuguese second.</summary>
    public static string T(string en, string pt) => IsPt ? pt : en;

    public static string FormatRelativeTime(DateTime? utcTime, bool isOnlineNow)
    {
        if (isOnlineNow)
            return T("Online now", "Online agora");

        if (utcTime == null)
            return T("Not seen yet", "Nunca visto");

        var span = DateTime.UtcNow - utcTime.Value;
        if (span.TotalMinutes < 2) return T("Just now", "Agora há pouco");
        if (span.TotalMinutes < 60) return T($"{(int)span.TotalMinutes}m ago", $"há {(int)span.TotalMinutes}m");
        if (span.TotalHours < 24) return T($"{(int)span.TotalHours}h ago", $"há {(int)span.TotalHours}h");
        if (span.TotalDays < 30) return T($"{(int)span.TotalDays}d ago", $"há {(int)span.TotalDays}d");
        if (span.TotalDays < 365) return T($"{(int)(span.TotalDays / 30)}mo ago", $"há {(int)(span.TotalDays / 30)}mês");
        return T($"{(int)(span.TotalDays / 365)}y ago", $"há {(int)(span.TotalDays / 365)}a");
    }
}
