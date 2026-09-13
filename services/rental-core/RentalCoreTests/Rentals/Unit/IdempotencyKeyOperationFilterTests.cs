using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi;
using ProjectY.Shared.Idempotency;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace RentalOperationsTests.Unit;

/// <summary>
/// No test opens the Swagger document -- it is served only in Development -- so this
/// is what notices when an OpenAPI.NET upgrade changes the shapes the filter writes.
/// </summary>
public sealed class IdempotencyKeyOperationFilterTests
{
    [Fact]
    public void AWrite_DocumentsTheIdempotencyKeyAndItsRefusals()
    {
        var operation = new OpenApiOperation();

        new IdempotencyKeyOperationFilter().Apply(operation, Context("POST"));

        var parameter = Assert.Single(operation.Parameters!);
        Assert.Equal(IdempotencyOptions.HeaderName, parameter.Name);
        Assert.Equal(ParameterLocation.Header, parameter.In);
        Assert.Equal(JsonSchemaType.String, parameter.Schema!.Type);
        Assert.Equal(200, parameter.Schema.MaxLength);
        Assert.Contains("409", operation.Responses!.Keys);
        Assert.Contains("422", operation.Responses.Keys);
    }

    [Fact]
    public void ARead_IsLeftAlone()
    {
        var operation = new OpenApiOperation();

        new IdempotencyKeyOperationFilter().Apply(operation, Context("GET"));

        Assert.Empty(operation.Parameters ?? []);
        Assert.Empty(operation.Responses ?? new OpenApiResponses());
    }

    private static OperationFilterContext Context(string method) => new(
        new ApiDescription { HttpMethod = method }, null!, new SchemaRepository(), new OpenApiDocument(), null!);
}
