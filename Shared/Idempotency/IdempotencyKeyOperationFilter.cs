using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace ProjectY.Shared.Idempotency;

public sealed class IdempotencyKeyOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var method = context.ApiDescription.HttpMethod;
        if (method is not ("POST" or "PUT" or "PATCH" or "DELETE"))
        {
            return;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = IdempotencyOptions.HeaderName,
            In = ParameterLocation.Header,
            Required = false,
            Description = "Makes a state-changing request replay-safe for 24 hours. Reusing the key with a different request returns 422.",
            Schema = new OpenApiSchema { Type = "string", MaxLength = 200 }
        });
        operation.Responses.TryAdd("409", new OpenApiResponse
        {
            Description = "Another request with this idempotency key is still running."
        });
        operation.Responses.TryAdd("422", new OpenApiResponse
        {
            Description = "The idempotency key was already used with a different request."
        });
    }
}
