namespace Nuraherbex.Api.Options;

/// <summary>
/// Optional AI drafting of WhatsApp replies. The assistant only writes a draft for an employee to edit and send;
/// it never sends anything itself. Without an API key the built-in reply guide is used instead.
/// </summary>
public class AiOptions
{
    public const string Section = "Ai";

    /// <summary>Anthropic API key. Set with user-secrets or the Ai__ApiKey / ANTHROPIC_API_KEY environment variable, never in a committed file.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Model used for drafting. Replies are short, so a lower <see cref="Effort"/> keeps them fast and cheap.</summary>
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>low | medium | high</summary>
    public string Effort { get; set; } = "low";

    /// <summary>Override only when going through a gateway; leave empty for the Anthropic API.</summary>
    public string BaseUrl { get; set; } = "";

    public int MaxTokens { get; set; } = 2048;
    public int TimeoutSeconds { get; set; } = 40;

    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);
}
