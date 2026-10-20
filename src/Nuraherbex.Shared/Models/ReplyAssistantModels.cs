namespace Nuraherbex.Shared.Models;

// ---------------------------------------------------------------------------
// Reply assistant: a drafted WhatsApp reply that a person edits before sending
// ---------------------------------------------------------------------------

public class ReplySuggestRequest
{
    /// <summary>Ignore the saved draft for this customer message and write a new one.</summary>
    public bool Fresh { get; set; }
    /// <summary>Optional change request applied to <see cref="CurrentDraft"/>: "shorter", "friendlier", "in Tamil", or free text.</summary>
    public string? Instruction { get; set; }
    /// <summary>The text currently in the reply box (what the instruction should change).</summary>
    public string? CurrentDraft { get; set; }
}

public class ReplySuggestionResponse : ApiResult
{
    public string Reply { get; set; } = "";
    /// <summary>Short label for what the customer asked, e.g. "Price question".</summary>
    public string Intent { get; set; } = "";
    /// <summary>True when a person should read it carefully before sending (health questions, complaints, anything unclear).</summary>
    public bool NeedsReview { get; set; }
    public string? ReviewNote { get; set; }
    /// <summary>ai | guide (built-in replies, no AI key or the AI was unavailable)</summary>
    public string Source { get; set; } = "guide";
    /// <summary>The customer message the draft answers.</summary>
    public string? RespondingTo { get; set; }
}

public class ReplyAssistantSettingsDto : ApiResult
{
    /// <summary>Show drafted replies to employees.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Business facts and tone the assistant must follow (products, delivery, payment, what never to promise).</summary>
    public string Knowledge { get; set; } = "";
    /// <summary>An AI key is configured on the server.</summary>
    public bool AiConnected { get; set; }
    public string Model { get; set; } = "";
}

public class ReplyAssistantSettingsRequest
{
    public bool Enabled { get; set; } = true;
    public string Knowledge { get; set; } = "";
}
