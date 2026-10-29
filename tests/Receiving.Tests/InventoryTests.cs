using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Receiving.Api;
using Xunit;

namespace Receiving.Tests;

public sealed class InventoryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "receiving-tests", Guid.NewGuid().ToString("N"));
    private readonly TestApplication application;
    private readonly HttpClient client;

    public InventoryTests()
    {
        application = new TestApplication(Path.Combine(directory, "stock.db"));
        client = application.CreateClient();
        client.DefaultRequestHeaders.Add("X-Inventory-Client", "stockroom");
    }

    private async Task<Item> Item(string sku = "HW-104") =>
        (await client.GetFromJsonAsync<List<Item>>("/api/items"))!.Single(item => item.Sku == sku);

    private async Task<List<Movement>> History(string sku = "HW-104") =>
        (await client.GetFromJsonAsync<List<Movement>>($"/api/movements?sku={sku}"))!;

    private Task<HttpResponseMessage> Receive(string reference, params ReceiptLine[] lines) =>
        client.PostAsJsonAsync("/api/receipts", new { reference, lines });

    [Fact]
    public async Task Receipt_is_durable_and_can_be_replayed_after_reopening()
    {
        var response = await Receive(" delivery-1 ", new("hw-104", 5), new("HW-208", 2));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<ReceiptResult>())!.Replayed);
        application.Dispose();

        using var reopened = new TestApplication(Path.Combine(directory, "stock.db"));
        using var secondClient = reopened.CreateClient();
        secondClient.DefaultRequestHeaders.Add("X-Inventory-Client", "stockroom");
        var replay = await secondClient.PostAsJsonAsync("/api/receipts", new
        {
            reference = "DELIVERY-1", lines = new[] { new ReceiptLine("HW-208", 2), new ReceiptLine("HW-104", 5) }
        });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<ReceiptResult>())!.Replayed);
        var items = (await secondClient.GetFromJsonAsync<List<Item>>("/api/items"))!;
        Assert.Equal(15, items.Single(item => item.Sku == "HW-104").Quantity);
        Assert.Equal(26, items.Single(item => item.Sku == "HW-208").Quantity);
        var history = await secondClient.GetFromJsonAsync<List<Movement>>("/api/movements?sku=HW-104");
        Assert.Single(history!, movement => movement.Kind == "Receipt");
    }

    [Fact]
    public async Task Reusing_a_reference_with_a_different_payload_changes_nothing()
    {
        Assert.Equal(HttpStatusCode.OK, (await Receive("DEL-2", new ReceiptLine("HW-104", 5))).StatusCode);
        var conflict = await Receive("DEL-2", new ReceiptLine("HW-104", 6));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(15, (await Item()).Quantity);
        Assert.Equal(2, (await History()).Count);
    }

    [Fact]
    public async Task An_invalid_later_line_rolls_back_earlier_lines_and_receipt_identity()
    {
        var failed = await Receive("DEL-3", new("HW-104", 5), new("ZZ-NOT-FOUND", 1));
        Assert.Equal(HttpStatusCode.NotFound, failed.StatusCode);
        Assert.Equal(10, (await Item()).Quantity);
        Assert.Single(await History());
        var corrected = await Receive("DEL-3", new ReceiptLine("HW-104", 5));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(15, (await Item()).Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    public async Task Invalid_receipt_quantities_leave_stock_unchanged(int quantity)
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Receive("BAD-QUANTITY", new ReceiptLine("HW-104", quantity))).StatusCode);
        Assert.Equal(10, (await Item()).Quantity);
        Assert.Single(await History());
    }

    [Fact]
    public async Task Duplicate_items_and_empty_or_oversized_receipts_are_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Receive("DUPLICATE", new("HW-104", 2), new(" hw-104 ", 3))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Receive("EMPTY")).StatusCode);
        var tooMany = Enumerable.Range(1, 21).Select(i => new ReceiptLine($"ITEM-{i}", 1)).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await Receive("TOO-MANY", tooMany)).StatusCode);
        Assert.Equal(10, (await Item()).Quantity);
    }

    [Fact]
    public async Task Balance_limit_failure_rolls_back_an_entire_receipt()
    {
        for (var i = 0; i < 9; i++)
            Assert.Equal(HttpStatusCode.OK, (await Receive($"FILL-{i}", new ReceiptLine("HW-208", 100000))).StatusCode);
        var failed = await Receive("OVER-LIMIT", new("HW-104", 1), new("HW-208", 100000));
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Equal(10, (await Item()).Quantity);
        Assert.Single(await History());
        Assert.Equal(900024, (await Item("HW-208")).Quantity);
    }

    [Fact]
    public async Task Adjustments_preserve_history_and_reject_stale_or_negative_balances()
    {
        var before = await Item();
        var adjusted = await client.PostAsJsonAsync("/api/adjustments",
            new AdjustRequest(before.Sku, -2, before.Version, "Damaged during handling"));
        Assert.Equal(HttpStatusCode.NoContent, adjusted.StatusCode);
        Assert.Equal(8, (await Item()).Quantity);
        var history = await History();
        Assert.Equal(2, history.Count);
        Assert.Equal("Opening", history[1].Kind);
        Assert.Equal("Damaged during handling", history[0].Note);

        var replay = await client.PostAsJsonAsync("/api/adjustments",
            new AdjustRequest(before.Sku, -2, before.Version, "Damaged during handling"));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var negative = await client.PostAsJsonAsync("/api/adjustments",
            new AdjustRequest(before.Sku, -20, (await Item()).Version, "Stock count"));
        Assert.Equal(HttpStatusCode.Conflict, negative.StatusCode);
        Assert.Equal(8, (await Item()).Quantity);
        Assert.Equal(2, (await History()).Count);
    }

    [Theory]
    [InlineData(0, "A reason")]
    [InlineData(-100001, "A reason")]
    [InlineData(1, " ")]
    public async Task Invalid_adjustments_do_not_write_history(int delta, string reason)
    {
        var response = await client.PostAsJsonAsync("/api/adjustments", new AdjustRequest("HW-104", delta, 0, reason));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(10, (await Item()).Quantity);
        Assert.Single(await History());
    }

    [Fact]
    public async Task Concurrent_replays_apply_a_receipt_once()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => Receive("PARALLEL", new ReceiptLine("HW-104", 5)))));
        Assert.All(results, result => Assert.Equal(HttpStatusCode.OK, result.StatusCode));
        Assert.Equal(15, (await Item()).Quantity);
        Assert.Single(await History(), movement => movement.Kind == "Receipt");
    }

    [Fact]
    public async Task Concurrent_distinct_receipts_do_not_lose_stock()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            Task.Run(() => Receive($"PARALLEL-{i}", new ReceiptLine("HW-104", 5)))));
        Assert.All(results, result => Assert.Equal(HttpStatusCode.OK, result.StatusCode));
        Assert.Equal(30, (await Item()).Quantity);
        Assert.Equal(4, (await History()).Count(movement => movement.Kind == "Receipt"));
    }

    [Fact]
    public async Task Competing_adjustments_use_the_expected_version()
    {
        var before = await Item();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            client.PostAsJsonAsync("/api/adjustments", new AdjustRequest(before.Sku, -7, before.Version, "Count correction")))));
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(3, (await Item()).Quantity);
        Assert.Equal(2, (await History()).Count);
    }

    [Fact]
    public async Task Csv_quotes_text_neutralizes_formulas_and_preserves_numeric_changes()
    {
        var response = await client.PostAsJsonAsync("/api/adjustments",
            new AdjustRequest("WD-415", -1, 0, "=HYPERLINK(\"test\",\"label\")"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var csv = await client.GetAsync("/api/movements/export?sku=WD-415");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var content = await csv.Content.ReadAsStringAsync();
        Assert.Contains("\"Oak dowel, 12 mm\"", content);
        Assert.Contains("\"'=HYPERLINK(\"\"test\"\",\"\"label\"\")\"", content);
        Assert.Contains(",-1,35,", content);
    }

    [Fact]
    public async Task Csv_contains_full_history_while_the_screen_is_bounded()
    {
        for (var i = 0; i < 101; i++)
            Assert.Equal(HttpStatusCode.OK, (await Receive($"HISTORY-{i}", new ReceiptLine("HW-104", 1))).StatusCode);
        Assert.Equal(100, (await History()).Count);
        var csv = await client.GetStringAsync("/api/movements/export?sku=HW-104");
        Assert.Equal(103, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Writes_require_an_explicit_client_header_and_valid_json()
    {
        using var browser = application.CreateClient();
        var crossSite = await browser.PostAsJsonAsync("/api/receipts", new { reference = "BLOCKED", lines = new[] { new ReceiptLine("HW-104", 1) } });
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        var malformed = await client.PostAsync("/api/receipts", new StringContent("{broken", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var missingVersion = await client.PostAsJsonAsync("/api/adjustments", new { sku = "HW-104", delta = 1, reason = "Count correction" });
        Assert.Equal(HttpStatusCode.BadRequest, missingVersion.StatusCode);
        var unknownRoute = await client.GetAsync("/api/not-a-route");
        Assert.Equal(HttpStatusCode.NotFound, unknownRoute.StatusCode);
        Assert.Equal("application/problem+json", unknownRoute.Content.Headers.ContentType!.MediaType);
        Assert.Equal(10, (await Item()).Quantity);
    }

    public void Dispose()
    {
        client.Dispose();
        application.Dispose();
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private sealed class TestApplication(string databasePath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("Inventory:DatabasePath", databasePath);
    }
}
