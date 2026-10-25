using System.Text.Json;

namespace Nuraherbex.Shared.Models;

// ---------------------------------------------------------------------------
// Organisation: departments, designations and page rights
// ---------------------------------------------------------------------------

/// <summary>One screen of the admin portal that access can be granted to.</summary>
public sealed record PageDef(string Key, string Label, string Group, string Kind, string Description);

/// <summary>
/// Access to a page is one of three levels. For CRM pages (<c>Kind = "crm"</c>) the middle level means "own records only"
/// and the top level "everyone's records". For store and team pages (<c>Kind = "admin"</c>) the middle level means
/// "view only" and the top level "view and change".
/// </summary>
public static class PageCatalog
{
    public const int None = 0, Limited = 1, Full = 2;

    public const string Crm = "crm", Admin = "admin";

    public static readonly PageDef[] Pages =
    [
        new("crm", "Sales dashboard", "Sales & CRM", Crm, "Lead flow, conversion, sales from leads, reply speed and who needs attention."),
        new("leads", "Leads & chat", "Sales & CRM", Crm, "WhatsApp leads, chat, replies, orders for a lead, owner changes."),
        new("followups", "Follow-ups", "Sales & CRM", Crm, "Calls and messages to do, reminders, the 15-day journey and its report."),
        new("tasks", "Tasks", "Work", Crm, "Team tasks with owners, due dates, checklists and comments."),
        new("overview", "Store overview", "Store", Admin, "Store dashboard with orders and customers at a glance."),
        new("orders", "Orders & shipping", "Store", Admin, "All store orders, courier booking, labels, cancellations, order e-mails."),
        new("payments", "Payments", "Store", Admin, "Online payment attempts and settlement."),
        new("customers", "Customers", "Store", Admin, "Customer accounts and addresses."),
        new("products", "Products", "Store", Admin, "Catalogue, prices and stock."),
        new("coupons", "Coupons", "Store", Admin, "Discount codes."),
        new("reviews", "Reviews", "Store", Admin, "Approve or hide customer reviews."),
        new("batches", "Batch reports", "Store", Admin, "Product batch / lab reports."),
        new("formulation", "Formulation", "Store", Admin, "Ingredient and metric content."),
        new("content", "Store content", "Store", Admin, "Website text and images."),
        new("reports", "Reports", "Admin", Admin, "Orders, leads, follow-ups, WhatsApp, tasks, customers and payments reports with CSV, Excel and PDF export."),
        new("whatsapp", "WhatsApp log", "Admin", Admin, "Raw WhatsApp message log and manual sending."),
        new("team", "Team & organisation", "Admin", Admin, "People, departments, designations and page rights, plus assistant and journey settings."),
    ];

    public static PageDef? Find(string key) => Pages.FirstOrDefault(p => p.Key == key);

    public static string LevelLabel(string pageKey, int level) => (Find(pageKey)?.Kind == Crm, level) switch
    {
        (_, <= 0) => "No access",
        (true, 1) => "Own records",
        (true, _) => "Everyone's records",
        (false, 1) => "View only",
        (false, _) => "View and change",
    };

    /// <summary>What people get when they have no designation yet: the same four CRM pages, own records only.</summary>
    public static readonly IReadOnlyDictionary<string, int> DefaultStaff = new Dictionary<string, int>
    {
        ["crm"] = 1, ["leads"] = 1, ["followups"] = 1, ["tasks"] = 1,
    };

    public static Dictionary<string, int> AllFull() => Pages.ToDictionary(p => p.Key, _ => Full);

    /// <summary>Keeps only known pages and levels 0-2.</summary>
    public static Dictionary<string, int> Clean(IDictionary<string, int>? rights)
    {
        var clean = new Dictionary<string, int>();
        if (rights is null) return clean;
        foreach (var (k, v) in rights)
            if (Find(k) is not null && v > 0) clean[k] = Math.Min(v, Full);
        return clean;
    }

    public static string ToJson(IDictionary<string, int>? rights) => JsonSerializer.Serialize(Clean(rights));

    public static Dictionary<string, int> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return Clean(JsonSerializer.Deserialize<Dictionary<string, int>>(json)); }
        catch (JsonException) { return new(); }
    }

    public static int LevelFrom(string? json, string page) => FromJson(json).GetValueOrDefault(page);

    /// <summary>Ready-made starting points for the rights editor.</summary>
    public static readonly (string Name, string Hint, Dictionary<string, int> Rights)[] Presets =
    [
        ("Sales executive", "Works own leads, follow-ups and tasks", new() { ["crm"] = 1, ["leads"] = 1, ["followups"] = 1, ["tasks"] = 1 }),
        ("Sales manager", "Sees and manages the whole sales team", new() { ["crm"] = 2, ["leads"] = 2, ["followups"] = 2, ["tasks"] = 2, ["customers"] = 1, ["orders"] = 1, ["team"] = 1, ["reports"] = 1 }),
        ("Customer support", "Helps customers, checks orders and reviews", new() { ["leads"] = 1, ["followups"] = 1, ["tasks"] = 1, ["customers"] = 1, ["orders"] = 1, ["reviews"] = 2 }),
        ("Operations", "Packs and ships orders, manages stock", new() { ["tasks"] = 1, ["overview"] = 1, ["orders"] = 2, ["payments"] = 1, ["customers"] = 1, ["products"] = 2, ["batches"] = 2, ["coupons"] = 1 }),
        ("Marketing", "Runs campaigns, coupons, reviews and website content", new() { ["leads"] = 1, ["followups"] = 1, ["tasks"] = 1, ["customers"] = 1, ["reviews"] = 2, ["coupons"] = 2, ["content"] = 2, ["whatsapp"] = 1 }),
        ("Designer", "Creates banners, creatives and product visuals", new() { ["tasks"] = 1, ["products"] = 1, ["content"] = 2, ["formulation"] = 1 }),
        ("Read-only", "Can look but not change anything", new() { ["crm"] = 1, ["leads"] = 1, ["followups"] = 1, ["tasks"] = 1, ["overview"] = 1, ["orders"] = 1, ["customers"] = 1, ["products"] = 1 }),
        ("Everything", "Full access to every page", AllFull()),
    ];
}

public class DepartmentDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? HeadEmployeeId { get; set; }
    public string? HeadName { get; set; }
    public bool IsActive { get; set; } = true;
    public int EmployeeCount { get; set; }
    public int DesignationCount { get; set; }
    public int OpenTasks { get; set; }
}

public class DepartmentSaveRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? HeadEmployeeId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class DepartmentListResponse : ApiResult
{
    public List<DepartmentDto> Departments { get; set; } = new();
}

public class DesignationDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    /// <summary>Seniority, 1 (junior) to 10 (top). Only used to order the list and the organisation chart.</summary>
    public int Level { get; set; } = 3;
    public bool IsActive { get; set; } = true;
    public int EmployeeCount { get; set; }
    /// <summary>Page key to access level (0 none, 1 limited, 2 full). Pages missing from the list have no access.</summary>
    public Dictionary<string, int> Rights { get; set; } = new();
}

public class DesignationSaveRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? DepartmentId { get; set; }
    public int Level { get; set; } = 3;
    public bool IsActive { get; set; } = true;
    public Dictionary<string, int> Rights { get; set; } = new();
}

public class DesignationListResponse : ApiResult
{
    public List<DesignationDto> Designations { get; set; } = new();
}

public class RightsCatalogResponse : ApiResult
{
    public List<PageDefDto> Pages { get; set; } = new();
    public List<RightsPresetDto> Presets { get; set; } = new();
}

public class PageDefDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Group { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public string LimitedLabel { get; set; } = "";
    public string FullLabel { get; set; } = "";
}

public class RightsPresetDto
{
    public string Name { get; set; } = "";
    public string Hint { get; set; } = "";
    public Dictionary<string, int> Rights { get; set; } = new();
}

// ---------------------------------------------------------------------------
// Task management
// ---------------------------------------------------------------------------

public static class TaskStatuses
{
    public const string Todo = "todo", InProgress = "in_progress", Blocked = "blocked", Done = "done";
    public static readonly string[] All = [Todo, InProgress, Blocked, Done];
    public static string Label(string s) => s switch { Todo => "To do", InProgress => "In progress", Blocked => "Blocked", Done => "Done", _ => s };
}

public static class TaskPriorities
{
    public const string Low = "low", Normal = "normal", High = "high", Urgent = "urgent";
    public static readonly string[] All = [Low, Normal, High, Urgent];
}

public static class TaskRepeats
{
    public const string None = "none", Daily = "daily", Weekly = "weekly", Monthly = "monthly";
    public static readonly string[] All = [None, Daily, Weekly, Monthly];
}

public class TaskDto
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = TaskStatuses.Todo;
    public string Priority { get; set; } = TaskPriorities.Normal;
    public DateTime? DueAt { get; set; }
    public bool IsOverdue { get; set; }
    public long? AssignedToEmployeeId { get; set; }
    public string? AssignedToName { get; set; }
    public long? CreatedByEmployeeId { get; set; }
    public string CreatedByName { get; set; } = "";
    public long? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public long? LeadId { get; set; }
    public string? LeadName { get; set; }
    public string? OrderId { get; set; }
    public string? Tags { get; set; }
    public string Repeat { get; set; } = TaskRepeats.None;
    public int ChecklistDone { get; set; }
    public int ChecklistTotal { get; set; }
    public int CommentCount { get; set; }
    /// <summary>Newest comment (shortened), filled on the list so it can be read without opening the task.</summary>
    public string? LastComment { get; set; }
    public string? LastCommentBy { get; set; }
    public DateTime? LastCommentAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>The signed-in person may edit, reassign or delete this task.</summary>
    public bool CanEdit { get; set; }
}

public class TaskChecklistItemDto
{
    public long Id { get; set; }
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public class TaskCommentDto
{
    public long Id { get; set; }
    public string Author { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class TaskActivityDto
{
    public string Actor { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class TaskDetailResponse : ApiResult
{
    public TaskDto? Task { get; set; }
    public List<TaskChecklistItemDto> Checklist { get; set; } = new();
    public List<TaskCommentDto> Comments { get; set; } = new();
    public List<TaskActivityDto> Activity { get; set; } = new();
}

public class TaskCounts
{
    public int Open { get; set; }
    public int Overdue { get; set; }
    public int DueToday { get; set; }
    public int Done { get; set; }
    public int Mine { get; set; }
    public int Unassigned { get; set; }
}

public class TaskListResponse : ApiResult
{
    public List<TaskDto> Items { get; set; } = new();
    public TaskCounts Counts { get; set; } = new();
}

public class TaskSaveRequest
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Priority { get; set; } = TaskPriorities.Normal;
    public DateTime? DueAt { get; set; }
    public long? AssignedToEmployeeId { get; set; }
    public long? DepartmentId { get; set; }
    public long? LeadId { get; set; }
    public string? OrderId { get; set; }
    public string? Tags { get; set; }
    public string Repeat { get; set; } = TaskRepeats.None;
    /// <summary>Checklist lines for a new task.</summary>
    public List<string> Checklist { get; set; } = new();
}

public class TaskStatusRequest
{
    public string Status { get; set; } = TaskStatuses.Todo;
    public string? Note { get; set; }
}

public class TaskCommentRequest
{
    public string Body { get; set; } = "";
}

public class TaskChecklistRequest
{
    public string? Text { get; set; }
    public bool? Done { get; set; }
}
