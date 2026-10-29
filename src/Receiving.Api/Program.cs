using System.Net;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi;
using Receiving.Api;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5087");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.RespectRequiredConstructorParameters = true);
builder.Services.AddOpenApi(options =>
{
    options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_0;
    options.AddSchemaTransformer((schema, context, _) =>
    {
        // Input types retain nulls for actionable business validation; the contract describes accepted values.
        if (context.JsonPropertyInfo?.AttributeProvider?.IsDefined(typeof(RequiredAttribute), true) == true)
        {
            schema.Type &= ~JsonSchemaType.Null;
            if (schema.Type == JsonSchemaType.String) schema.MinLength = 1;
        }
        return Task.CompletedTask;
    });
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new OpenApiInfo
        {
            Title = "Inventory Receiving API",
            Version = "1.0.0",
            Description = "A local-only ASP.NET Core portfolio API backed by SQLite and used by a React client. " +
                "Receipt references provide durable replay protection; stock corrections use optimistic concurrency. " +
                "Requests are limited to 32 KiB. X-Inventory-Client is an explicit client marker, not authentication. " +
                "No production identity provider, OAuth scopes or public API hosting are included.",
            License = new OpenApiLicense { Name = "MIT", Url = new Uri("https://opensource.org/license/mit") }
        };
        document.Servers = [new OpenApiServer { Url = "http://127.0.0.1:5087", Description = "Run the local demo to execute requests" }];
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        if (context.Description.HttpMethod == "POST")
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "X-Inventory-Client", In = ParameterLocation.Header, Required = true,
                Description = "Must be stockroom. This client marker blocks ordinary cross-site form writes; it is not an API key or user identity.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, Default = JsonValue.Create("stockroom"), Enum = [JsonValue.Create("stockroom")] }
            });
        }
        if (operation.RequestBody?.Content is { } content && content.TryGetValue("application/json", out var body))
            body.Example = JsonNode.Parse(operation.OperationId == "ReceiveStock"
                ? """{"reference":"API-EXPLORER-001","lines":[{"sku":"HW-104","quantity":5}]}"""
                : """{"sku":"HW-104","delta":-2,"expectedVersion":0,"reason":"Count correction; copy the current version before sending"}""");
        if (operation.Responses?.TryGetValue("503", out var unavailable) == true && unavailable is OpenApiResponse response)
            response.Headers = new Dictionary<string, IOpenApiHeader>
            {
                ["Retry-After"] = new OpenApiHeader { Description = "Wait one second, then retry the same receipt or refresh stock.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } }
            };
        return Task.CompletedTask;
    });
});
builder.Services.AddSingleton(_ => new InventoryStore(
    builder.Configuration["Inventory:DatabasePath"] ??
    Path.Combine(builder.Environment.ContentRootPath, "data", "inventory.db")));
var app = builder.Build();
app.Services.GetRequiredService<InventoryStore>().Initialize();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    var styles = context.Request.Path.StartsWithSegments("/api-docs") ? "'self' 'unsafe-inline'" : "'self'";
    context.Response.Headers["Content-Security-Policy"] = $"default-src 'self'; script-src 'self'; style-src {styles}; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    if (context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
    {
        context.Response.StatusCode = 403;
        return;
    }
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Headers["X-Inventory-Client"] != "stockroom")
    {
        await Results.Problem(statusCode: 403, detail: "The request must come from the receiving client.").ExecuteAsync(context);
        return;
    }
    try { await next(context); }
    catch (Exception error) when (!context.Response.HasStarted)
    {
        var (status, message) = error switch
        {
            InventoryException business => (business.Status, business.Message),
            BadHttpRequestException => (400, "The request must contain valid JSON matching the documented fields."),
            SqliteException { SqliteErrorCode: 5 or 6 } => (503, "The stock database is busy. Wait a moment, then retry the same receipt or refresh stock."),
            _ => (500, "The operation could not be confirmed. Refresh stock and review the history before retrying.")
        };
        if (status >= 500) app.Logger.LogError(error, "Inventory request failed");
        if (status == 503) context.Response.Headers.RetryAfter = "1";
        await Results.Problem(statusCode: status, detail: message).ExecuteAsync(context);
    }
});

app.MapOpenApi();
app.MapGet("/api-docs", () => Results.Redirect("/api-docs/index.html")).ExcludeFromDescription();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Path.Combine(AppContext.BaseDirectory, "api-docs")),
    RequestPath = "/api-docs"
});

app.MapGet("/api/items", (InventoryStore store) => store.GetItems())
    .WithName("ListItems").WithTags("Inventory").WithSummary("List stock balances and versions")
    .WithDescription("Returns every item, ordered by code. Use the version in a subsequent stock correction.")
    .Produces<List<Item>>().ProducesProblem(503).ProducesProblem(500);
app.MapGet("/api/movements", (string? sku, InventoryStore store) => store.GetMovements(sku))
    .WithName("ListMovements").WithTags("History").WithSummary("Read the latest 100 stock movements")
    .WithDescription("Optional sku filters by trimmed, uppercased item code. Unknown codes return an empty list. Newest movements appear first.")
    .Produces<List<Movement>>().ProducesProblem(503).ProducesProblem(500);
app.MapGet("/api/movements/export", (string? sku, InventoryStore store) =>
    Results.File(CsvExport.Write(store.GetMovements(sku, all: true)), "text/csv; charset=utf-8", "stock-movements.csv"))
    .WithName("ExportMovements").WithTags("History").WithSummary("Export full movement history as CSV")
    .WithDescription("Optional sku filter. Includes all matching history, beyond the screen's 100-row limit. Formula-like text is neutralized for spreadsheet use.")
    .Produces<string>(200, "text/csv").ProducesProblem(503).ProducesProblem(500);
app.MapPost("/api/receipts", (ReceiveRequest request, InventoryStore store) => Results.Ok(store.Receive(request)))
    .WithName("ReceiveStock").WithTags("Receiving").WithSummary("Receive stock, or safely replay a receipt")
    .WithDescription("One transaction applies all lines. A normalized reference and matching lines return 200 with replayed=true without adding stock. Reusing the reference with a different payload returns 409. Unknown items return 404; all lines roll back together.")
    .Produces<ReceiptResult>().ProducesProblem(400).ProducesProblem(403).ProducesProblem(404)
    .ProducesProblem(409).ProducesProblem(503).ProducesProblem(500);
app.MapPost("/api/adjustments", (AdjustRequest request, InventoryStore store) =>
{
    store.Adjust(request);
    return Results.NoContent();
}).WithName("AdjustStock").WithTags("Corrections").WithSummary("Correct stock using the version you read")
    .WithDescription("Requires a nonzero change, a reason and the current item version. Returns 204 after committing. A stale version or a balance outside 0–1,000,000 returns 409. Adds a movement instead of editing history.")
    .Produces(204).ProducesProblem(400).ProducesProblem(403).ProducesProblem(404)
    .ProducesProblem(409).ProducesProblem(503).ProducesProblem(500);
app.Map("/api/{**path}", () => Results.Problem(statusCode: 404, detail: "This inventory API route does not exist.")).ExcludeFromDescription();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
