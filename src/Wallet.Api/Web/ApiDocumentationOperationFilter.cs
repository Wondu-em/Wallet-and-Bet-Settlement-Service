using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Wallet.Api.Web;

public sealed class ApiDocumentationOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var endpointMetadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        if (endpointMetadata.OfType<IAuthorizeData>().Any() &&
            !endpointMetadata.OfType<IAllowAnonymous>().Any())
        {
            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = new List<string>()
            });
        }

        var path = context.ApiDescription.RelativePath?.TrimStart('/');
        var method = context.ApiDescription.HttpMethod;
        if (method != "POST" || path is null)
            return;

        if (path is "wallet/deposit" or "wallet/withdraw" or "bets" or
            "admin/events/{id}/settle" or "admin/events/{id}/void" or "webhooks/deposit")
        {
            AddHeader(operation, "Idempotency-Key",
                "Required request idempotency key (1 to 200 characters).");
        }

        if (path == "webhooks/deposit")
        {
            AddHeader(operation, "X-Timestamp", "Request signing time as Unix seconds.");
            AddHeader(operation, "X-Signature",
                "Hex HMAC-SHA256 of the UTF-8 bytes of `timestamp + \".\" + raw request body`.");
        }
    }

    private static void AddHeader(OpenApiOperation operation, string name, string description)
    {
        operation.Parameters ??= new List<IOpenApiParameter>();
        var existing = operation.Parameters.OfType<OpenApiParameter>()
            .FirstOrDefault(parameter => parameter.In == ParameterLocation.Header && parameter.Name == name);
        if (existing is not null)
        {
            existing.Required = true;
            existing.Description = description;
            return;
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = true,
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String }
        });
    }
}
