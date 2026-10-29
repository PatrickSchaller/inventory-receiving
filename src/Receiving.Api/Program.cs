using System.Net;
using Microsoft.Data.Sqlite;
using Receiving.Api;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5087");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.RespectRequiredConstructorParameters = true);
builder.Services.AddSingleton(_ => new InventoryStore(
    builder.Configuration["Inventory:DatabasePath"] ??
    Path.Combine(builder.Environment.ContentRootPath, "data", "inventory.db")));
var app = builder.Build();
app.Services.GetRequiredService<InventoryStore>().Initialize();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
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

app.MapGet("/api/items", (InventoryStore store) => store.GetItems());
app.MapGet("/api/movements", (string? sku, InventoryStore store) => store.GetMovements(sku));
app.MapGet("/api/movements/export", (string? sku, InventoryStore store) =>
    Results.File(CsvExport.Write(store.GetMovements(sku, all: true)), "text/csv; charset=utf-8", "stock-movements.csv"));
app.MapPost("/api/receipts", (ReceiveRequest request, InventoryStore store) => Results.Ok(store.Receive(request)));
app.MapPost("/api/adjustments", (AdjustRequest request, InventoryStore store) =>
{
    store.Adjust(request);
    return Results.NoContent();
});
app.Map("/api/{**path}", () => Results.Problem(statusCode: 404, detail: "This inventory API route does not exist."));

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
