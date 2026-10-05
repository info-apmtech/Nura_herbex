using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Nuraherbex.Api.Services;

/// <summary>Adds a JSON request body to Swagger for endpoints that read the raw request stream (WhatsApp webhook events).</summary>
public class RawJsonBodyOperationFilter : IOperationFilter
{
    private const string SampleEvent = """
        {
          "object": "whatsapp_business_account",
          "entry": [{
            "id": "WABA_ID",
            "changes": [{
              "field": "messages",
              "value": {
                "messaging_product": "whatsapp",
                "metadata": { "display_phone_number": "918888003430", "phone_number_id": "PHONE_NUMBER_ID" },
                "contacts": [{ "profile": { "name": "Test Customer" }, "wa_id": "919876543210" }],
                "messages": [{ "from": "919876543210", "id": "wamid.TEST", "timestamp": "1700000000", "type": "text", "text": { "body": "Hi" } }]
              }
            }]
          }]
        }
        """;

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var path = context.ApiDescription.RelativePath ?? "";
        if (!path.Equals("api/webhooks/whatsapp", StringComparison.OrdinalIgnoreCase) || context.ApiDescription.HttpMethod != "POST") return;

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description = "Raw WhatsApp Cloud API event. If WhatsApp:AppSecret is set, X-Hub-Signature-256 must match the exact body bytes.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object }, Example = JsonNode.Parse(SampleEvent) },
            },
        };
    }
}
