namespace Nuraherbex.Shared.Models;

// ---------------------------------------------------------------------------
// CRM leads (admin view)
// ---------------------------------------------------------------------------
public enum LeadStatus
{
    New = 1,
    Interested = 2,
    FollowUp = 3,
    Converted = 4,
    NotInterested = 5,
}

public static class LeadStatuses
{
    public const string New = "new";
    public const string Interested = "interested";
    public const string FollowUp = "follow-up";
    public const string Converted = "converted";
    public const string NotInterested = "not-interested";
    public static readonly string[] All = [New, Interested, FollowUp, Converted, NotInterested];

    public static string ToCode(this LeadStatus status) => status switch
    {
        LeadStatus.New => New,
        LeadStatus.Interested => Interested,
        LeadStatus.FollowUp => FollowUp,
        LeadStatus.Converted => Converted,
        LeadStatus.NotInterested => NotInterested,
        _ => New,
    };

    public static bool TryParse(string? value, out LeadStatus status)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case New: status = LeadStatus.New; return true;
            case Interested: status = LeadStatus.Interested; return true;
            case FollowUp: status = LeadStatus.FollowUp; return true;
            case Converted: status = LeadStatus.Converted; return true;
            case NotInterested: status = LeadStatus.NotInterested; return true;
            default: status = default; return false;
        }
    }
}

public enum LeadSource
{
    WhatsApp = 1,
    Instagram = 2,
    Facebook = 3,
    Website = 4,
    Manual = 5,
}

public static class LeadSources
{
    public const string WhatsApp = "whatsapp";
    public const string Instagram = "instagram";
    public const string Facebook = "facebook";
    public const string Website = "website";
    public const string Manual = "manual";
    public static readonly string[] All = [WhatsApp, Instagram, Facebook, Website, Manual];

    public static string ToCode(this LeadSource source) => source switch
    {
        LeadSource.WhatsApp => WhatsApp,
        LeadSource.Instagram => Instagram,
        LeadSource.Facebook => Facebook,
        LeadSource.Website => Website,
        LeadSource.Manual => Manual,
        _ => WhatsApp,
    };

    public static bool TryParse(string? value, out LeadSource source)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case WhatsApp: source = LeadSource.WhatsApp; return true;
            case Instagram: source = LeadSource.Instagram; return true;
            case Facebook: source = LeadSource.Facebook; return true;
            case Website: source = LeadSource.Website; return true;
            case Manual: source = LeadSource.Manual; return true;
            default: source = default; return false;
        }
    }
}

public class LeadDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public string Source { get; set; } = "whatsapp";
    public string? SourceDetail { get; set; }
    public string Status { get; set; } = LeadStatuses.New;
    /// <summary>unknown | opted_in | opted_out</summary>
    public string ConsentStatus { get; set; } = "unknown";
    public string? FirstMessage { get; set; }
    public string? Notes { get; set; }
    public string? CustomerId { get; set; }
    public int UnreadCount { get; set; }
    /// <summary>Preview of the most recent message in the conversation (list view).</summary>
    public string? LastMessage { get; set; }
    public string? LastMessageDirection { get; set; }
    public DateTime? ServiceWindowExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastContactAt { get; set; }
    public DateTime? ConvertedAt { get; set; }
    public long? AssignedToEmployeeId { get; set; }
    public string? AssignedToName { get; set; }
    public DateTime? AssignedAt { get; set; }
    /// <summary>Set when the first paid (or cash-on-delivery) order is placed. Such a person is a customer and is looked after in Customer care.</summary>
    public DateTime? CustomerSince { get; set; }
    public bool IsCustomer => CustomerSince is not null;
    /// <summary>The sales person who owned the lead when it became a customer (null for a plain website order).</summary>
    public long? ConvertedByEmployeeId { get; set; }
    public string? ConvertedByName { get; set; }
    /// <summary>Confirmed orders that are not delivered, cancelled or returned yet.</summary>
    public int OpenOrders { get; set; }
    /// <summary>Soonest pending follow-up, if any.</summary>
    public DateTime? NextFollowUpAt { get; set; }
    /// <summary>The last follow-up on this lead ended with "no answer" (a retry is waiting).</summary>
    public bool NoAnswer { get; set; }
    /// <summary>Has a pending follow-up due today (Indian calendar day); the Follow-up tab lists these first.</summary>
    public bool FollowUpToday { get; set; }
    /// <summary>Orders placed by this customer (matched by phone) and what they are worth.</summary>
    public int OrderCount { get; set; }
    public decimal LifetimeValue { get; set; }
    public DateTime? LastOrderAt { get; set; }
}

public class LeadStatusHistoryDto
{
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public string? Note { get; set; }
    public string ChangedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class LeadListResponse : ApiResult
{
    public List<LeadDto> Leads { get; set; } = new();
    public int Total { get; set; }
    /// <summary>Leads nobody owns yet (admin view).</summary>
    public int UnassignedCount { get; set; }
    /// <summary>Lead count per status (ignores the status filter, honours search/source).</summary>
    public Dictionary<string, int> StatusCounts { get; set; } = new();
    /// <summary>People in each panel the caller may open: leads, interested, care (ignores search and filters).</summary>
    public Dictionary<string, int> ViewCounts { get; set; } = new();
    /// <summary>Unread customer messages in this panel.</summary>
    public int UnreadTotal { get; set; }
}

/// <summary>
/// Tabs of the Leads panel. "new" = first enquiry today and nobody has started on it; "older" = the same, but it arrived on an earlier day and is still unopened;
/// "follow-up" = being chased; "no-answer" = the last call or message got no answer; "not-interested" = closed for now.
/// </summary>
public static class LeadBuckets
{
    public const string New = LeadStatuses.New, Older = "older", FollowUp = LeadStatuses.FollowUp, NoAnswer = "no-answer", NotInterested = LeadStatuses.NotInterested;
    public static readonly string[] All = [New, Older, FollowUp, NoAnswer, NotInterested];
}

/// <summary>What happens to a leaving employee's leads and follow-ups: "equal" splits them evenly across the team, "person" gives all to one person, "unassigned" returns them to the admin queue, "auto" follows who is online.</summary>
public static class HandoverModes
{
    public const string Equal = "equal", Person = "person", Unassigned = "unassigned", Auto = "auto";
}

public class HandoverCandidateDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsOnline { get; set; }
    /// <summary>Open leads this person already holds.</summary>
    public int OpenLeads { get; set; }
    public int PendingFollowUps { get; set; }
}

/// <summary>What a person leaving the team holds, and who can take it over (shown in the popup before removal).</summary>
public class HandoverPreviewResponse : ApiResult
{
    public string Name { get; set; } = "";
    public int OpenLeads { get; set; }
    public int Customers { get; set; }
    public int PendingFollowUps { get; set; }
    public List<HandoverCandidateDto> Candidates { get; set; } = new();
}

/// <summary>The three panels over the same people: new and follow-up leads, interested leads, and paid customers.</summary>
public static class LeadViews
{
    public const string Leads = "leads", Interested = "interested", Care = "care";
    public static readonly string[] All = [Leads, Interested, Care];
}

public class LeadDetailResponse : ApiResult
{
    public LeadDto? Lead { get; set; }
    public List<WhatsAppMessageDto> Messages { get; set; } = new();
    public List<LeadStatusHistoryDto> History { get; set; } = new();
    public List<LeadAssignmentDto> Assignments { get; set; } = new();
    /// <summary>Every status and owner change in one newest-first list: what changed, who or what did it, and when.</summary>
    public List<LeadAuditDto> Audit { get; set; } = new();
    public List<FollowUpDto> FollowUps { get; set; } = new();
    public List<LeadOrderDto> Orders { get; set; } = new();
}

public class LeadStatusRequest
{
    public string Status { get; set; } = LeadStatuses.New;
    public string? Note { get; set; }
}

/// <summary>Records that a call or message got no answer. A retry is booked for <see cref="NextDueAt"/> (tomorrow 10 am when empty).</summary>
public class LeadNoAnswerRequest
{
    public string? Note { get; set; }
    public DateTime? NextDueAt { get; set; }
}

public class LeadNotesRequest
{
    public string? Notes { get; set; }
}

public class LeadAssignmentDto
{
    public string? FromName { get; set; }
    public string? ToName { get; set; }
    public string? Reason { get; set; }
    public string ChangedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class LeadAuditDto
{
    /// <summary>"status" or "owner".</summary>
    public string Kind { get; set; } = "";
    public string Summary { get; set; } = "";
    public string? Detail { get; set; }
    public string Actor { get; set; } = "";
    /// <summary>True when the system made the change (background worker, journey, order), not a person.</summary>
    public bool Automatic { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class LeadAssignRequest
{
    /// <summary>Employee to own the lead; null unassigns it (admin only).</summary>
    public long? EmployeeId { get; set; }
    public string? Reason { get; set; }
}

public class LeadReplyRequest
{
    public string Text { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Employees (CRM users)
// ---------------------------------------------------------------------------
public enum EmployeeRole
{
    Admin = 1,
    Sales = 2,
}

public static class EmployeeRoles
{
    public const string Admin = "admin";
    public const string Sales = "sales";

    public static string ToCode(this EmployeeRole role) => role switch
    {
        EmployeeRole.Admin => Admin,
        _ => Sales,
    };

    public static bool TryParse(string? value, out EmployeeRole role)
    {
        switch (value)
        {
            case Admin: role = EmployeeRole.Admin; return true;
            case Sales: role = EmployeeRole.Sales; return true;
            default: role = default; return false;
        }
    }
}

public class EmployeeDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>admin | sales</summary>
    public string Role { get; set; } = EmployeeRoles.Sales;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    /// <summary>Assigned leads that are not yet converted / closed.</summary>
    public int OpenLeads { get; set; }
    public int TotalLeads { get; set; }
    public long? DepartmentId { get; set; }
    public string? DepartmentName { get; set; }
    public long? DesignationId { get; set; }
    public string? DesignationName { get; set; }
    public int OpenTasks { get; set; }
    /// <summary>Signed in to the portal right now (checked in within the last two minutes).</summary>
    public bool IsOnline { get; set; }
    /// <summary>The person switched "WhatsApp active" on, so new WhatsApp leads can be assigned to them.</summary>
    public bool WhatsAppActive { get; set; }
}

public class EmployeeSaveRequest
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Role { get; set; } = EmployeeRoles.Sales;
    public bool IsActive { get; set; } = true;
    /// <summary>Required when creating; when editing, leave empty to keep the current password.</summary>
    public string? Password { get; set; }
    public long? DepartmentId { get; set; }
    public long? DesignationId { get; set; }
}

public class EmployeeListResponse : ApiResult
{
    public List<EmployeeDto> Employees { get; set; } = new();
    /// <summary>New leads are assigned round-robin to active sales employees.</summary>
    public bool AutoAssign { get; set; }
}

public class EmployeeResponse : ApiResult
{
    public EmployeeDto? Employee { get; set; }
}

public class EmployeeRemoveRequest
{
    /// <summary>auto = spread their open leads across the other active sales team | unassigned = leave them in the admin queue</summary>
    public string Reassign { get; set; } = "auto";
}

public class LeadSettingsRequest
{
    public bool AutoAssign { get; set; }
}

public class LeadConsentRequest
{
    /// <summary>opted_in | unknown</summary>
    public string Status { get; set; } = "opted_in";
}

// ---------------------------------------------------------------------------
// Message templates: automatic replies and ready-made replies for every stage of a person
// ---------------------------------------------------------------------------

/// <summary>Where a person is in the journey. Decides which messages fit.</summary>
public static class MessageStages
{
    public const string New = "new", Interested = "interested", FollowUp = "follow-up", NotInterested = "not-interested", Customer = "customer";
    public static readonly string[] All = [New, Interested, FollowUp, NotInterested, Customer];
    public static string Label(string s) => s switch { New => "New leads", Interested => "Interested leads", FollowUp => "Follow-up leads", NotInterested => "Not interested", Customer => "Customers", _ => s };

    /// <summary>The stage that fits a lead (a paid customer always counts as a customer).</summary>
    public static string Of(LeadStatus leadStatus, bool isCustomer) => isCustomer ? Customer : Of(leadStatus.ToCode(), false);

    /// <summary>The stage that fits a lead (a paid customer always counts as a customer).</summary>
    public static string Of(string leadStatus, bool isCustomer) => isCustomer ? Customer : leadStatus switch
    {
        LeadStatuses.Interested => Interested, LeadStatuses.FollowUp => FollowUp, LeadStatuses.NotInterested => NotInterested, _ => New,
    };
}

/// <summary>Keys of the messages the system sends by itself. Their text can be edited but they cannot be deleted.</summary>
public static class MessageKeys
{
    public const string AfterHoursLead = "afterhours.lead", AfterHoursInterested = "afterhours.interested",
        AfterHoursCustomerOpen = "afterhours.customer-open", AfterHoursCustomer = "afterhours.customer",
        GreetingLead = "greeting.lead", GreetingCustomerOpen = "greeting.customer-open", GreetingCustomer = "greeting.customer",
        Delivered = "delivered.customer", Welcome = "welcome.customer";
}

public class MessageTemplateDto
{
    /// <summary>A <see cref="MessageKeys"/> value for automatic messages, a generated id for ready-made replies.</summary>
    public string Key { get; set; } = "";
    /// <summary>auto (sent by the system) | reply (picked by staff in the chat)</summary>
    public string Group { get; set; } = "reply";
    public string Stage { get; set; } = MessageStages.New;
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>When it is sent, shown under automatic messages.</summary>
    public string? Hint { get; set; }
    public bool Enabled { get; set; } = true;
}

public class MessageTemplatesSettings
{
    /// <summary>Send the after-hours reply when nobody on the team is active.</summary>
    public bool AfterHoursEnabled { get; set; } = true;
    public List<MessageTemplateDto> Items { get; set; } = new();
    /// <summary>Names of the <see cref="MetaTemplates"/> Meta has approved. Only these can be sent from the chat.</summary>
    public List<string> ApprovedMetaTemplates { get; set; } = new();
}

public class MessageTemplatesResponse : ApiResult
{
    public MessageTemplatesSettings Settings { get; set; } = new();
}

/// <summary>Placeholders: {name} first name, {agent} the staff member, {order} the latest order id.</summary>
public static class MessageDefaults
{
    public const string OldAfterHoursText = "Hello {name}, thank you for contacting Nura Herbex 🌿\nOur team is away right now (Sunday, holiday or outside working hours). We have received your message and will reply as soon as we are back.\nIn the meantime, send *track* or your order ID (e.g. NH-123456) to get your order status.";

    private static MessageTemplateDto Auto(string key, string stage, string title, string hint, string body) =>
        new() { Key = key, Group = "auto", Stage = stage, Title = title, Hint = hint, Body = body };
    private static MessageTemplateDto Reply(string stage, string title, string body) =>
        new() { Key = "reply-" + Guid.NewGuid().ToString("N")[..8], Group = "reply", Stage = stage, Title = title, Body = body };

    public static List<MessageTemplateDto> Automatic() =>
    [
        Auto(MessageKeys.AfterHoursLead, MessageStages.New, "After hours · new and follow-up leads", "A lead writes while nobody is active. No order tracking is offered.",
            "Hello {name}, thank you for contacting Nura Herbex 🌿\nOur team is away right now (Sunday, holiday or outside working hours). We have received your message and will reply as soon as we are back."),
        Auto(MessageKeys.AfterHoursInterested, MessageStages.Interested, "After hours · interested leads", "An interested lead writes while nobody is active.",
            "Hello {name}, thank you for your interest in Nura Herbex 🌿\nOur team is away right now (Sunday, holiday or outside working hours). Your message is saved and your advisor will reply first thing when we are back, with everything you need to place your order."),
        Auto(MessageKeys.AfterHoursCustomerOpen, MessageStages.Customer, "After hours · customer with an order on its way", "A customer writes while nobody is active and an order is not delivered yet. Offers order tracking.",
            "Hello {name}, thank you for being a Nura Herbex customer 🌿\nOur care team is away right now (Sunday, holiday or outside working hours) and will reply as soon as we are back.\nTo check your order {order} right now, send *track* and we will share its latest status."),
        Auto(MessageKeys.AfterHoursCustomer, MessageStages.Customer, "After hours · customer, all orders delivered", "A customer writes while nobody is active and every order is delivered.",
            "Hello {name}, thank you for being a Nura Herbex customer 🌿\nOur care team is away right now (Sunday, holiday or outside working hours). We have received your message and will reply as soon as we are back."),
        Auto(MessageKeys.GreetingLead, MessageStages.New, "Greeting · leads", "A lead says hi, or asks to track an order they do not have.",
            "Hello {name}, welcome to Nura Herbex 🌿\nThank you for reaching out. A member of our team will be with you shortly to help."),
        Auto(MessageKeys.GreetingCustomerOpen, MessageStages.Customer, "Greeting · customer with an order on its way", "A customer says hi while an order is not delivered yet.",
            "Hello {name}, welcome back to Nura Herbex 🌿\nYour order {order} is on its way to you. Send *track* any time for its latest status, or tell us how we can help."),
        Auto(MessageKeys.GreetingCustomer, MessageStages.Customer, "Greeting · customer, all orders delivered", "A customer says hi and every order is delivered.",
            "Hello {name}, welcome back to Nura Herbex 🌿\nThank you for being our customer. Tell us how we can help and our care team will reply shortly."),
        Auto(MessageKeys.Delivered, MessageStages.Customer, "Track request after delivery", "A customer asks to track but the order is already delivered.",
            "Hello {name}, your order {order} has been delivered 🌿\nIf anything is not right, reply here and our care team will help you."),
        Auto(MessageKeys.Welcome, MessageStages.Customer, "Welcome as a customer", "Sent once, right after the first paid order. Outside the 24-hour window it goes as the approved WhatsApp template.",
            "Hello {name}, thank you for your order {order} with Nura Herbex 🌿\nI am {agent} from our customer care team and I will look after you from here. Message us on this number any time for order updates or help with your product."),
    ];

    public static List<MessageTemplateDto> Replies() =>
    [
        Reply(MessageStages.New, "First reply", "Hello {name}, thank you for contacting Nura Herbex 🌿 I am {agent}. How can I help you today?"),
        Reply(MessageStages.New, "Share product details", "Hi {name}, Stamix is a herbal wellness powder made with traditional ingredients and tested in a lab for every batch. Would you like me to share the benefits, how to use it and the price?"),
        Reply(MessageStages.New, "Ask for a good time to call", "Hi {name}, may I call you to understand what you need and suggest the right pack? Please tell me a time that suits you."),
        Reply(MessageStages.Interested, "Pack options and price", "Hi {name}, happy to help you choose. Shall I share our pack options with the price and the current offer?"),
        Reply(MessageStages.Interested, "How to order", "Hi {name}, ordering is easy: tell me the pack you would like and your delivery address and I will create the order. You can pay online or choose cash on delivery."),
        Reply(MessageStages.Interested, "Gentle nudge", "Hi {name}, just checking whether you had any questions about Stamix. I am glad to help you decide and place your order whenever you are ready."),
        Reply(MessageStages.FollowUp, "Friendly reminder", "Hi {name}, following up on our earlier chat about Stamix. Would you like to go ahead, or is there anything I can clarify?"),
        Reply(MessageStages.FollowUp, "Last check-in", "Hi {name}, I do not want to disturb you, so this is my last note. If you would like to try Stamix later, just reply here and I will help you."),
        Reply(MessageStages.NotInterested, "Polite close", "Thank you for your time, {name}. If you ever want to know more about Nura Herbex, message us here any time. Wishing you good health 🌿"),
        Reply(MessageStages.Customer, "Thank you for your order", "Hi {name}, thank you for ordering from Nura Herbex 🌿 Your order {order} is confirmed and I will keep you updated until it reaches you."),
        Reply(MessageStages.Customer, "How to use", "Hi {name}, for the best results take Stamix as printed on the pack, at the same time each day. Reply here if you need help with the routine."),
        Reply(MessageStages.Customer, "Ask for feedback", "Hi {name}, how are you finding Stamix? Your feedback helps us look after you better. Reply here any time."),
        Reply(MessageStages.Customer, "Reorder reminder", "Hi {name}, your pack will be finishing soon. Would you like me to arrange your next order?"),
        Reply(MessageStages.Customer, "Apology for a problem", "Hi {name}, I am sorry for the trouble with your order {order}. I am looking into it right now and will update you shortly."),
    ];

    public static string Fill(string body, string? name, string? agent = null, string? order = null)
    {
        var first = string.IsNullOrWhiteSpace(name) ? "there" : name.Trim().Split(' ')[0];
        return body.Replace("{name}", first).Replace("{agent}", string.IsNullOrWhiteSpace(agent) ? "our team" : agent.Trim()).Replace("{order}", string.IsNullOrWhiteSpace(order) ? "your order" : order.Trim());
    }
}
