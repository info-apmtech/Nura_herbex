namespace Nuraherbex.Api.Options;

/// <summary>Optional Meta-approved WhatsApp templates used to send journey steps automatically. Without one, a step stays a task for the employee.</summary>
public class FollowUpOptions
{
    public const string Section = "FollowUps";
    /// <summary>Turn off on any extra process (e.g. a developer PC) that shares the production database, so tasks are not created twice.</summary>
    public bool WorkerEnabled { get; set; } = true;
    /// <summary>Keyed by journey step key: day1-welcome, day3-usage, day7-experience, day8-review, day12-reorder, day14-reorder-followup.</summary>
    public Dictionary<string, FollowUpTemplate> Templates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class FollowUpTemplate
{
    public string Name { get; set; } = "";
    /// <summary>Body variables in order, each one of: name | orderId | reviewLink | storeLink.</summary>
    public List<string> Params { get; set; } = ["name"];
}
