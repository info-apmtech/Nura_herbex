namespace Nuraherbex.Shared.Models;

// ---------------------------------------------------------------------------
// File library: PDFs, videos and images an administrator uploads once and staff send to customers
// ---------------------------------------------------------------------------

public static class LibraryKinds
{
    public const string Pdf = "pdf", Video = "video", Image = "image", Document = "document";

    /// <summary>What WhatsApp accepts as an attachment: documents up to 100 MB, videos up to 16 MB, images up to 5 MB.</summary>
    public const long MaxUpload = 100L * 1024 * 1024, MaxVideoAttachment = 16L * 1024 * 1024, MaxImageAttachment = 5L * 1024 * 1024;

    public static string Label(string kind) => kind switch { Pdf => "PDF", Video => "Video", Image => "Image", _ => "Document" };


    /// <summary>Whether the file may go out as a WhatsApp attachment (otherwise only as a link).</summary>
    public static bool CanAttach(string kind, long size) => kind switch
    {
        Video => size <= MaxVideoAttachment,
        Image => size <= MaxImageAttachment,
        _ => size <= MaxUpload,
    };
}

public class LibraryFileDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>pdf | video | image | document</summary>
    public string Kind { get; set; } = LibraryKinds.Document;
    public string FileName { get; set; } = "";
    public string Mime { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; }
    public string UploadedBy { get; set; } = "";
    /// <summary>Public link to the file. Anyone with it can open the file; deleting the file closes it.</summary>
    public string Url { get; set; } = "";
    /// <summary>Can be sent as a WhatsApp attachment (size is within WhatsApp's limit for its type).</summary>
    public bool CanAttach { get; set; } = true;
    public int TimesSent { get; set; }
}

public class LibraryListResponse : ApiResult
{
    public List<LibraryFileDto> Files { get; set; } = new();
}

public class LibraryFileResponse : ApiResult
{
    public LibraryFileDto? File { get; set; }
}

public class LibraryUpdateRequest
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
}

public class SendFileRequest
{
    public string FileId { get; set; } = "";
    /// <summary>attachment = the file itself as a WhatsApp document, video or image | link = a message with the file's link</summary>
    public string Mode { get; set; } = "attachment";
    public string? Caption { get; set; }
}

public class SendTemplateRequest
{
    public string Name { get; set; } = "";
    public List<string> Params { get; set; } = new();
}

// ---------------------------------------------------------------------------
// WhatsApp (Meta) templates: the only messages that may start a conversation, or reopen one after the 24-hour window
// ---------------------------------------------------------------------------

/// <summary>One value a template needs. <c>Kind</c> says where it comes from: name, agent, order (filled in) or text (typed by the sender).</summary>
public sealed record MetaParam(string Kind, string Label, string Sample);

/// <summary>A template written for Meta's approval. The body uses {{1}}, {{2}}… exactly as Meta expects.</summary>
public sealed record MetaTemplateDef(string Name, string Stage, string Category, string Title, string Body, MetaParam[] Params, string? Button = null, string? Note = null)
{
    public string Filled(IReadOnlyList<string> values)
    {
        var s = Body;
        for (var i = 0; i < Params.Length; i++) s = s.Replace("{{" + (i + 1) + "}}", i < values.Count ? values[i] : "…");
        return s;
    }

    public string Sampled() => Filled(Params.Select(p => p.Sample).ToList());
}

public static class MetaTemplates
{
    private static readonly MetaParam Name = new("name", "Customer's first name", "Asha");
    private static readonly MetaParam Agent = new("agent", "Your name", "Priya");
    private static readonly MetaParam Order = new("order", "Order number", "26110669798256");

    public const string Utility = "UTILITY", Marketing = "MARKETING";

    /// <summary>The templates we ask Meta to approve. Names are fixed because the app sends by name.</summary>
    public static readonly MetaTemplateDef[] All =
    [
        // ---- Leads ----
        new("nura_lead_reconnect", MessageStages.New, Marketing, "Reconnect with a lead",
            "Hello {{1}}, this is {{2}} from Nura Herbex 🌿 We would love to help you with {{3}}. Reply here and we will continue from where we left off.",
            [Name, Agent, new("text", "What you can help with", "your wellness goals")]),
        new("nura_info_ready", MessageStages.New, Utility, "Details are ready",
            "Hello {{1}}, the details you asked about are ready. Reply YES and {{2}} from Nura Herbex will send them right away.",
            [Name, Agent], Button: "Yes, send them"),
        new("nura_docs_ready", MessageStages.New, Utility, "Guide and video are ready",
            "Hello {{1}}, the usage guide and product video you asked for are ready. Reply here to receive them.",
            [Name], Button: "Send me the files", Note: "Once they tap the button the window reopens: send the PDF and video from the Files button."),
        new("nura_callback_confirm", MessageStages.New, Utility, "Confirm a call",
            "Hello {{1}}, {{2}} from Nura Herbex will call you on {{3}}. Reply here if another time suits you better.",
            [Name, Agent, new("text", "When you will call", "tomorrow at 11 am")]),
        // ---- Interested ----
        new("nura_hot_nudge", MessageStages.Interested, Marketing, "Nudge an interested lead",
            "Hello {{1}}, {{2}} here from Nura Herbex. Your {{3}} is still available. Reply here and I will help you place your order today.",
            [Name, Agent, new("text", "Pack they liked", "3-month Stamix pack")]),
        new("nura_offer", MessageStages.Interested, Marketing, "Share an offer",
            "Hello {{1}}, we have a special offer on {{2}} this week: {{3}}. Reply here to know more.",
            [Name, new("text", "Product", "Stamix 3-month pack"), new("text", "Offer", "free delivery on every order")]),
        // ---- Follow-up ----
        new("nura_followup_reminder", MessageStages.FollowUp, Marketing, "Gentle follow-up",
            "Hello {{1}}, just following up on our earlier chat about Stamix. Would you like to continue? Reply here any time and {{2}} will help you.",
            [Name, Agent]),
        // ---- Customers ----
        new("customer_welcome", MessageStages.Customer, Utility, "Welcome as a customer",
            "Hello {{1}}, thank you for your order {{2}} with Nura Herbex 🌿\nI am {{3}} from our customer care team and I will look after you from here. Message us on this number any time for order updates or help with your product.",
            [Name, Order, Agent], Note: "Sent automatically after the first paid order. Set WhatsApp:CustomerWelcomeTemplate to this name."),
        new("nura_order_checkin", MessageStages.Customer, Utility, "Order on its way",
            "Hello {{1}}, this is {{2}} from Nura Herbex care. Your order {{3}} is on its way. Reply here if you need anything.",
            [Name, Agent, Order]),
        new("nura_usage_tips", MessageStages.Customer, Utility, "Usage tip",
            "Hello {{1}}, a quick tip for your Stamix routine: {{2}}. Reply here if you have any questions.",
            [Name, new("text", "The tip", "take it at the same time every day")]),
        new("nura_feedback", MessageStages.Customer, Marketing, "Ask for feedback",
            "Hello {{1}}, how are you finding Stamix? Reply here and tell us. We read every message.",
            [Name]),
        new("nura_reorder", MessageStages.Customer, Marketing, "Reorder reminder",
            "Hello {{1}}, your Stamix pack will be finishing soon. Reply here and {{2}} will arrange your next order.",
            [Name, Agent]),
        new("nura_issue_followup", MessageStages.Customer, Utility, "Follow up on a concern",
            "Hello {{1}}, this is {{2}} following up on your concern about order {{3}}. Reply here and we will sort it out.",
            [Name, Agent, Order]),
        new("nura_payment_reminder", MessageStages.Customer, Utility, "Payment reminder",
            "Hello {{1}}, your order {{2}} is waiting for a payment of ₹{{3}}. Reply here and we will help you complete it.",
            [Name, Order, new("text", "Amount", "1,299")]),
    ];

    public static MetaTemplateDef? Find(string? name) => All.FirstOrDefault(t => t.Name == name);
}
