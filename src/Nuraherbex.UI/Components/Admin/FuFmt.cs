using System.Globalization;
using Nuraherbex.Shared.Models;

namespace Nuraherbex.UI.Components.Admin;

/// <summary>Display helpers shared by the follow-up screens (times are shown in the viewer's own clock).</summary>
public static class FuFmt
{
    public static string Icon(string type) => type switch
    {
        FollowUpTypes.Call => "Phone",
        FollowUpTypes.WhatsApp => "MessageCircle",
        FollowUpTypes.Review => "Star",
        FollowUpTypes.Reorder => "RefreshCw",
        FollowUpTypes.Order => "Truck",
        _ => "Clock",
    };

    public static string TypeLabel(string type) => type switch
    {
        FollowUpTypes.Call => "Call",
        FollowUpTypes.WhatsApp => "WhatsApp",
        FollowUpTypes.Review => "Review request",
        FollowUpTypes.Reorder => "Reorder",
        FollowUpTypes.Order => "Order alert",
        _ => "Other",
    };

    public static DateTime Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
    public static DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

    private static string Clock(DateTime l) => l.ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>"Today, 4:30 PM", "Tomorrow, 10:00 AM" or "Wed 15 Oct, 10:00 AM".</summary>
    public static string Due(DateTime utc)
    {
        var l = Local(utc);
        var today = DateTime.Now.Date;
        if (l.Date == today) return "Today, " + Clock(l);
        if (l.Date == today.AddDays(1)) return "Tomorrow, " + Clock(l);
        if (l.Date == today.AddDays(-1)) return "Yesterday, " + Clock(l);
        return l.ToString("ddd d MMM", CultureInfo.InvariantCulture) + ", " + Clock(l);
    }

    public static string DayHeading(DateTime utc)
    {
        var l = Local(utc).Date;
        var today = DateTime.Now.Date;
        if (l == today) return "Today";
        if (l == today.AddDays(1)) return "Tomorrow";
        return l.ToString("dddd, d MMMM", CultureInfo.InvariantCulture);
    }

    /// <summary>"2 days overdue" for tasks whose day has passed.</summary>
    public static string Overdue(DateTime utc)
    {
        var days = (DateTime.Now.Date - Local(utc).Date).Days;
        return days <= 0 ? "Due earlier today" : days == 1 ? "1 day overdue" : days + " days overdue";
    }

    public static string Origin(FollowUpDto f) => f.Source switch
    {
        "journey" => "15-day journey",
        "system" => "Order alert",
        _ => "",
    };

    public static string Outcome(string? outcome)
    {
        if (string.IsNullOrEmpty(outcome)) return "";
        var code = outcome.Split(':')[0];
        var note = outcome.Contains(':') ? outcome[(outcome.IndexOf(':') + 1)..].Trim() : "";
        var label = code switch
        {
            FollowUpOutcomes.Done => "Completed",
            FollowUpOutcomes.Interested => "Interested",
            FollowUpOutcomes.NotInterested => "Not interested",
            FollowUpOutcomes.Converted => "Ordered",
            FollowUpOutcomes.NoAnswer => "No answer",
            FollowUpOutcomes.Callback => "Call back later",
            "skipped" => "Skipped",
            _ => outcome,
        };
        return string.IsNullOrEmpty(note) ? label : $"{label} — {note}";
    }

    public static string DateInput(DateTime local) => local.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    /// <summary>Tomorrow-ish quick choices for scheduling: 10:00 local on the given day offset.</summary>
    public static DateTime AtTen(int addDays) => DateTime.Now.Date.AddDays(addDays).AddHours(10);
    public static DateTime At(int addDays, int hour) => DateTime.Now.Date.AddDays(addDays).AddHours(hour);

    /// <summary>Quick picks: this morning / evening while they are still ahead, then tomorrow, in 2 days and next week.</summary>
    public static List<(string Label, DateTime When)> QuickPicks()
    {
        var picks = new List<(string, DateTime)>();
        if (DateTime.Now < At(0, 10).AddMinutes(-5)) picks.Add(("Today morning · 10 AM", At(0, 10)));
        if (DateTime.Now < At(0, 18).AddMinutes(-5)) picks.Add(("Today evening · 6 PM", At(0, 18)));
        picks.Add(("Tomorrow morning", At(1, 10)));
        picks.Add(("Tomorrow evening", At(1, 18)));
        picks.Add(("In 2 days", At(2, 10)));
        picks.Add(("Next week", At(7, 10)));
        return picks;
    }
}
