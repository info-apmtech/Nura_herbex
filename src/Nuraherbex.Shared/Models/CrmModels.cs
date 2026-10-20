namespace Nuraherbex.Shared.Models;

// ---------------------------------------------------------------------------
// Sales: orders created from a lead, customer view, and the CRM dashboard
// ---------------------------------------------------------------------------

/// <summary>An order that belongs to the customer behind a lead (matched by phone number).</summary>
public class LeadOrderDto
{
    public string Id { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public decimal TotalAmount { get; set; }
    public string PaymentMethod { get; set; } = "";
    public string PaymentStatus { get; set; } = "";
    public string DeliveryStatus { get; set; } = "";
    public string FulfillmentStatus { get; set; } = "";
    public string? Courier { get; set; }
    public string? Awb { get; set; }
    public DateTime? DeliveredAt { get; set; }
    /// <summary>"Stamix x 2, Stamix Pro x 1".</summary>
    public string Items { get; set; } = "";
}

public class LeadOrderLineRequest
{
    public string ProductId { get; set; } = "";
    public int Quantity { get; set; } = 1;
}

/// <summary>Create an order for a lead (e.g. a customer who agreed to buy on WhatsApp).</summary>
public class LeadOrderRequest
{
    public List<LeadOrderLineRequest> Items { get; set; } = new();
    public string? CouponCode { get; set; }
    public string? CustomerName { get; set; }
    /// <summary>Required for the order confirmation e-mail.</summary>
    public string Email { get; set; } = "";
    public AddressDto Address { get; set; } = new();
    /// <summary>False = cash on delivery. True = the customer already paid (UPI / bank transfer).</summary>
    public bool AlreadyPaid { get; set; }
    public string? PaymentReference { get; set; }
}

public class LeadOrderResponse : ApiResult
{
    public string OrderId { get; set; } = "";
    public decimal TotalAmount { get; set; }
}

// ---- Dashboard ----

public class CrmDashboardResponse : ApiResult
{
    public int Days { get; set; }
    public DateTime From { get; set; }
    /// <summary>True when the numbers cover only the signed-in employee's own leads.</summary>
    public bool Personal { get; set; }

    public int NewLeads { get; set; }
    public int ConvertedLeads { get; set; }
    /// <summary>Of the leads created in the period, the share that is now a customer (0-100).</summary>
    public int ConversionRate { get; set; }
    public int OpenLeads { get; set; }
    public int Unassigned { get; set; }
    public int Orders { get; set; }
    public decimal Revenue { get; set; }
    public decimal AverageOrderValue { get; set; }

    /// <summary>Median minutes from a customer's first message to the first reply by a person. Null when nothing to measure.</summary>
    public int? MedianFirstReplyMinutes { get; set; }
    /// <summary>Share of first messages answered by a person within 60 minutes (0-100).</summary>
    public int RepliedWithinHourPercent { get; set; }
    public int AwaitingFirstReply { get; set; }

    public int FollowUpsOverdue { get; set; }
    public int FollowUpsToday { get; set; }

    public List<CrmFunnelRow> Funnel { get; set; } = new();
    public List<CrmSourceRow> BySource { get; set; } = new();
    public List<CrmEmployeeRow> ByEmployee { get; set; } = new();
    public List<CrmTrendPoint> Trend { get; set; } = new();
    public List<CrmAttentionRow> NeedsAttention { get; set; } = new();
}

public class CrmFunnelRow
{
    public string Status { get; set; } = "";
    public int Count { get; set; }
}

public class CrmSourceRow
{
    public string Source { get; set; } = "";
    public int Leads { get; set; }
    public int Converted { get; set; }
    public int ConversionPercent { get; set; }
    public decimal Revenue { get; set; }
}

public class CrmEmployeeRow
{
    public long? EmployeeId { get; set; }
    public string Name { get; set; } = "";
    public int Leads { get; set; }
    public int Converted { get; set; }
    public decimal Revenue { get; set; }
    public int? MedianFirstReplyMinutes { get; set; }
    public int OpenLeads { get; set; }
    public int OverdueFollowUps { get; set; }
}

public class CrmTrendPoint
{
    public string Day { get; set; } = "";
    public int NewLeads { get; set; }
    public int Converted { get; set; }
}

/// <summary>A lead that needs a person now (unanswered, unassigned or with an overdue task).</summary>
public class CrmAttentionRow
{
    public long LeadId { get; set; }
    public string Name { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime Since { get; set; }
    public string? Owner { get; set; }
}
