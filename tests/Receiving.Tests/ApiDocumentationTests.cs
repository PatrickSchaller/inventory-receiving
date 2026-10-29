using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Receiving.Api;
using Xunit;

namespace Receiving.Tests;

public sealed class ApiDocumentationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "receiving-api-docs", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> application;
    private readonly HttpClient client;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public ApiDocumentationTests()
    {
        application = new DocumentationApplication(Path.Combine(directory, "stock.db"));
        client = application.CreateClient();
    }

    [Fact]
    public async Task Openapi_contract_matches_the_published_snapshot_and_documents_real_responses()
    {
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.StartsWith("3.0.", actual["openapi"]!.GetValue<string>());
        var paths = actual["paths"]!.AsObject();
        Assert.Equal(5, paths.Count);
        Assert.Equal("ReceiveStock", paths["/api/receipts"]!["post"]!["operationId"]!.GetValue<string>());
        var receive = paths["/api/receipts"]!["post"]!;
        Assert.Contains(receive["parameters"]!.AsArray(), parameter => parameter!["name"]!.GetValue<string>() == "X-Inventory-Client");
        foreach (var status in new[] { "200", "400", "403", "404", "409", "503", "500" })
            Assert.NotNull(receive["responses"]![status]);
        Assert.NotNull(receive["responses"]!["409"]!["content"]!["application/problem+json"]);
        Assert.NotNull(receive["responses"]!["503"]!["headers"]!["Retry-After"]);
        Assert.Null(actual["components"]?["securitySchemes"]);
        var schemas = actual["components"]!["schemas"]!;
        Assert.Contains(schemas["ReceiveRequest"]!["required"]!.AsArray(), property => property!.GetValue<string>() == "reference");
        Assert.Contains(schemas["ReceiveRequest"]!["required"]!.AsArray(), property => property!.GetValue<string>() == "lines");
        Assert.Equal(1, schemas["ReceiptLine"]!["properties"]!["quantity"]!["minimum"]!.GetValue<int>());
        Assert.Equal(100000, schemas["ReceiptLine"]!["properties"]!["quantity"]!["maximum"]!.GetValue<int>());
        Assert.Null(schemas["ReceiveRequest"]!["properties"]!["reference"]!["nullable"]);
        Assert.Null(schemas["ReceiveRequest"]!["properties"]!["lines"]!["nullable"]);
        var path = Path.Combine(Repository(), "docs", "api-reference", "openapi.json");
        if (Environment.GetEnvironmentVariable("UPDATE_API_DOCS") == "1")
            await File.WriteAllTextAsync(path, actual.ToJsonString(Pretty) + "\n", new UTF8Encoding(false));
        Assert.True(File.Exists(path), "Generate the contract snapshot with UPDATE_API_DOCS=1.");
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(path));
        Assert.True(JsonNode.DeepEquals(expected, actual), "The runtime contract changed. Review it and regenerate the published snapshot with UPDATE_API_DOCS=1.");
    }

    [Fact]
    public async Task The_published_http_walkthrough_executes_all_documented_outcomes()
    {
        var file = Path.Combine(Repository(), "docs", "api-reference", "inventory.http");
        var text = (await File.ReadAllTextAsync(file)).Replace("\r\n", "\n");
        var variables = Regex.Matches(text, @"(?m)^@(\w+)\s*=\s*(.+)$")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value.Trim());
        var captures = new JsonArray();
        foreach (var raw in text.Split("###", StringSplitOptions.RemoveEmptyEntries))
        {
            var block = raw;
            foreach (var (name, value) in variables) block = block.Replace("{{" + name + "}}", value);
            var lines = block.Trim().Split('\n');
            var start = Array.FindIndex(lines, line => line.StartsWith("GET ", StringComparison.Ordinal) || line.StartsWith("POST ", StringComparison.Ordinal));
            if (start < 0) continue;
            var status = int.Parse(Regex.Match(block, @"# expect: (\d+)").Groups[1].Value);
            var parts = lines[start].Split(' ', 2);
            var url = new Uri(parts[1]);
            using var request = new HttpRequestMessage(new HttpMethod(parts[0]), url.PathAndQuery);
            var bodyStart = Array.FindIndex(lines, start + 1, line => string.IsNullOrWhiteSpace(line));
            var body = bodyStart >= 0 ? string.Join('\n', lines.Skip(bodyStart + 1)).Trim() : "";
            if (body.Length > 0) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            for (var line = start + 1; line < (bodyStart >= 0 ? bodyStart : lines.Length); line++)
            {
                var header = lines[line].Split(':', 2);
                if (header[0].Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse(header[1].Trim());
                else request.Headers.Add(header[0], header[1].Trim());
            }
            using var response = await client.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();
            Assert.True((int)response.StatusCode == status, $"{lines[0]} expected {status}, got {(int)response.StatusCode}: {responseText}");
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            captures.Add(new JsonObject
            {
                ["scenario"] = lines[0], ["method"] = parts[0], ["path"] = url.PathAndQuery,
                ["request"] = body.Length > 0 ? JsonNode.Parse(body) : null,
                ["status"] = status, ["contentType"] = type,
                ["response"] = type.Contains("json", StringComparison.Ordinal) ? JsonNode.Parse(responseText) : JsonValue.Create(responseText)
            });
        }
        Assert.Equal(12, captures.Count);
        Assert.False(captures[1]!["response"]!["replayed"]!.GetValue<bool>());
        Assert.True(captures[2]!["response"]!["replayed"]!.GetValue<bool>());
        var items = (await client.GetFromJsonAsync<List<Item>>("/api/items"))!;
        var item = items.Single(value => value.Sku == "HW-104");
        Assert.Equal(13, item.Quantity);
        Assert.Equal(2, item.Version);
        var movements = (await client.GetFromJsonAsync<List<Movement>>("/api/movements?sku=HW-104"))!;
        Assert.Equal(3, movements.Count);
        Assert.Single(movements, movement => movement.Kind == "Receipt");
        Assert.Single(movements, movement => movement.Kind == "Adjustment");
        if (Environment.GetEnvironmentVariable("UPDATE_API_DOCS") == "1")
        {
            var document = new JsonObject
            {
                ["recordedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["description"] = "Actual responses from the ordered HTTP walkthrough against a new synthetic test database. This is not a live public API.",
                ["requests"] = captures
            };
            await File.WriteAllTextAsync(Path.Combine(Repository(), "docs", "api-reference", "responses.json"), document.ToJsonString(Pretty) + "\n", new UTF8Encoding(false));
        }
    }

    [Fact]
    public async Task Local_explorer_assets_are_served_without_changing_the_api_behavior()
    {
        var response = await client.GetAsync("/api-docs/index.html");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("vendor/swagger-ui-bundle.js", await response.Content.ReadAsStringAsync());
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        (await client.GetAsync("/api-docs/reference.js")).EnsureSuccessStatusCode();
    }

    private static string Repository()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "InventoryReceiving.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Cannot find repository root.");
    }
    public void Dispose()
    {
        client.Dispose(); application.Dispose();
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
        Directory.Delete(directory);
    }
    private sealed class DocumentationApplication(string path) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Inventory:DatabasePath", path);
    }
}
