namespace Nuraherbex.Shared.Models;

/// <summary>Ready-made tasks for each team role, so a marketing, design, sales, support or operations person starts from a proven checklist.</summary>
public static class TaskTemplates
{
    public sealed record Template(string Role, string Key, string Title, string Description, string Priority, int DueInDays, string Repeat, string[] Checklist);

    public static readonly string[] Roles = ["Marketing", "Design", "Sales", "Support", "Operations", "Management"];

    public static readonly Template[] All =
    [
        // ---- Marketing ----
        new("Marketing", "mk-calendar", "Plan this week's content calendar", "Decide the posts, reels and WhatsApp status for the coming week and book the publishing time.", TaskPriorities.Normal, 2, TaskRepeats.Weekly,
            ["Review last week's best-performing post", "List 5 post ideas with product benefit", "Brief the designer for each creative", "Schedule posts on Instagram and Facebook", "Share the plan with sales and support"]),
        new("Marketing", "mk-campaign", "Launch a WhatsApp offer campaign", "Send an offer to opted-in customers. Check the monthly WhatsApp limit before sending.", TaskPriorities.High, 3, TaskRepeats.None,
            ["Check remaining monthly WhatsApp messages", "Write the offer message and get it approved", "Pick the customer list (opted-in only)", "Send a test to the team", "Send the campaign", "Review replies and orders after 24 hours"]),
        new("Marketing", "mk-ads", "Review ad performance report", "Check spend, cost per lead and orders from ads, then decide what to pause or scale.", TaskPriorities.Normal, 7, TaskRepeats.Weekly,
            ["Export spend and leads per campaign", "Mark the cheapest and costliest lead sources", "Pause weak ads", "Note one test to run next week"]),
        new("Marketing", "mk-testimonials", "Collect customer testimonials and photos", "Ask happy customers for a review, photo or short video and get permission to use it.", TaskPriorities.Normal, 7, TaskRepeats.Monthly,
            ["List customers who rated 4-5 stars", "Message them asking for a photo or video", "Record written permission", "Hand the content to design"]),
        new("Marketing", "mk-influencer", "Influencer and partner outreach", "Contact fitness and wellness creators for collaborations.", TaskPriorities.Low, 10, TaskRepeats.None,
            ["Shortlist 10 creators", "Send the intro message", "Track replies in a sheet", "Agree deliverables and send samples"]),

        // ---- Design ----
        new("Design", "ds-creatives", "Create product creatives for a campaign", "Prepare the images and short videos for an upcoming campaign in all required sizes.", TaskPriorities.Normal, 4, TaskRepeats.None,
            ["Confirm the brief and deadline with marketing", "Make the first draft", "Share for feedback", "Export story, feed and WhatsApp sizes", "Upload to the shared folder"]),
        new("Design", "ds-banner", "Design a website offer banner", "Make a banner for the home page and shop page.", TaskPriorities.Normal, 3, TaskRepeats.None,
            ["Get the offer text and dates", "Design desktop and mobile versions", "Check the text is readable on a phone", "Send to the website owner"]),
        new("Design", "ds-packaging", "Revise packaging or label artwork", "Update the label or box artwork with the approved wording and batch details.", TaskPriorities.High, 5, TaskRepeats.None,
            ["Collect the approved text and certifications", "Update the artwork", "Get compliance sign-off", "Send print-ready files to the printer"]),
        new("Design", "ds-video", "Edit a customer or product reel", "Cut a short vertical video for social media.", TaskPriorities.Normal, 3, TaskRepeats.None,
            ["Choose the best clips", "Add captions and branding", "Export 9:16 under 30 seconds", "Share for approval"]),

        // ---- Sales ----
        new("Sales", "sl-hot", "Call today's hot leads", "Phone every lead marked Interested or with a reply in the last 48 hours.", TaskPriorities.High, 1, TaskRepeats.Daily,
            ["Open Leads and filter Interested", "Call each lead and note the outcome", "Schedule a follow-up for anyone not ready", "Create orders for ready customers"]),
        new("Sales", "sl-pipeline", "Weekly pipeline review", "Go through every open lead, close the dead ones and plan next steps.", TaskPriorities.Normal, 5, TaskRepeats.Weekly,
            ["Count new, interested and converted leads", "Mark lost leads as not interested", "Assign unowned leads", "Write the plan for next week"]),
        new("Sales", "sl-reactivate", "Win back customers who have not reordered", "Contact customers whose pack should have finished.", TaskPriorities.Normal, 3, TaskRepeats.Monthly,
            ["List customers 30+ days after delivery with no reorder", "Send the reorder message or call", "Offer the current promotion", "Record the result"]),
        new("Sales", "sl-bulk", "Handle a bulk or dealer enquiry", "Respond to a larger order request with pricing and delivery terms.", TaskPriorities.High, 1, TaskRepeats.None,
            ["Confirm quantity and city", "Share the price and delivery time", "Get approval for any discount", "Follow up in 2 days"]),

        // ---- Support ----
        new("Support", "su-queries", "Clear pending customer queries", "Reply to every customer message that is still waiting.", TaskPriorities.High, 1, TaskRepeats.Daily,
            ["Open Leads sorted by waiting time", "Reply to messages older than 2 hours first", "Escalate order problems", "Mark resolved chats"]),
        new("Support", "su-delayed", "Chase delayed shipments", "Check orders that have not moved in 3 days and update the customer.", TaskPriorities.High, 1, TaskRepeats.Daily,
            ["Open Orders and filter In transit", "Contact the courier about stuck parcels", "Message the customer with the update", "Arrange re-delivery or refund if needed"]),
        new("Support", "su-returns", "Process a return or refund", "Handle a return request from approval to refund.", TaskPriorities.Normal, 3, TaskRepeats.None,
            ["Collect order ID and reason", "Approve or decline with a reason", "Arrange pickup", "Issue the refund and tell the customer"]),
        new("Support", "su-faq", "Update the FAQ from this week's questions", "Add the most common questions and answers to the website and reply guide.", TaskPriorities.Low, 7, TaskRepeats.Weekly,
            ["List the top 5 repeated questions", "Write clear answers", "Add them to the reply guide", "Tell the team"]),

        // ---- Operations ----
        new("Operations", "op-dispatch", "Pack and dispatch pending orders", "Pack every confirmed order and hand it to the courier.", TaskPriorities.High, 1, TaskRepeats.Daily,
            ["Print labels for confirmed orders", "Pack with the seal and batch sticker", "Hand over to the courier", "Mark orders as packed"]),
        new("Operations", "op-stock", "Stock count and reorder check", "Count stock and raise a production request if it is low.", TaskPriorities.Normal, 7, TaskRepeats.Weekly,
            ["Count finished packs", "Compare with last week's sales", "Raise a production request if under 2 weeks of stock", "Update stock in Products"]),
        new("Operations", "op-cod", "Reconcile COD remittances", "Match courier cash remittances against delivered COD orders.", TaskPriorities.Normal, 7, TaskRepeats.Weekly,
            ["Download the courier remittance report", "Match each delivered COD order", "Flag any missing amounts", "Send the summary to accounts"]),

        // ---- Management ----
        new("Management", "mg-review", "Monthly business review", "Review sales, WhatsApp usage, follow-up completion and team workload.", TaskPriorities.Normal, 7, TaskRepeats.Monthly,
            ["Check sales and conversion in the CRM dashboard", "Check WhatsApp messages used against the monthly limit", "Check the follow-up completion report", "Agree three priorities for next month"]),
    ];

    public static IEnumerable<Template> For(string? role) =>
        string.IsNullOrWhiteSpace(role) ? All : All.Where(t => t.Role.Equals(role, StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps a department or designation name to a template role (e.g. "Graphic Designer" gives Design).</summary>
    public static string? RoleFor(string? name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("market")) return "Marketing";
        if (n.Contains("design") || n.Contains("creative")) return "Design";
        if (n.Contains("sales")) return "Sales";
        if (n.Contains("support") || n.Contains("service")) return "Support";
        if (n.Contains("operation") || n.Contains("logistic") || n.Contains("warehouse")) return "Operations";
        if (n.Contains("manage") || n.Contains("admin")) return "Management";
        return null;
    }
}
