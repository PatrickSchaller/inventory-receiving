using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Receiving.Api;

public sealed class InventoryStore(string databasePath)
{
    private const int MaximumQuantity = 1_000_000;
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath), ForeignKeys = true,
        Pooling = false, DefaultTimeout = 5
    }.ToString();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        using var connection = Open();
        using (var pragma = Command(connection, null, "PRAGMA journal_mode=WAL;"))
            pragma.ExecuteScalar();
        using var transaction = connection.BeginTransaction();
        using (var schema = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS Items (
                Sku TEXT PRIMARY KEY, Name TEXT NOT NULL,
                Quantity INTEGER NOT NULL CHECK(Quantity BETWEEN 0 AND 1000000),
                ReorderPoint INTEGER NOT NULL, Version INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Receipts (
                Reference TEXT PRIMARY KEY, Payload TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Movements (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Sku TEXT NOT NULL REFERENCES Items(Sku), Kind TEXT NOT NULL,
                Delta INTEGER NOT NULL, Balance INTEGER NOT NULL,
                Reference TEXT NOT NULL, Note TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Movements_Sku_Id ON Movements(Sku, Id DESC);
            """))
            schema.ExecuteNonQuery();

        Item[] seeds = [new("HW-104", "Brass cabinet handle", 10, 12, 0),
            new("HW-208", "Steel corner bracket", 24, 10, 0),
            new("WD-310", "Birch plywood panel", 8, 4, 0),
            new("WD-415", "Oak dowel, 12 mm", 36, 12, 0)];
        foreach (var item in seeds)
        {
            using var insert = Command(connection, transaction,
                "INSERT OR IGNORE INTO Items(Sku, Name, Quantity, ReorderPoint) VALUES($sku,$name,$qty,$point)",
                ("$sku", item.Sku), ("$name", item.Name), ("$qty", item.Quantity), ("$point", item.ReorderPoint));
            if (insert.ExecuteNonQuery() == 1)
                AddMovement(connection, transaction, item.Sku, "Opening", item.Quantity, item.Quantity,
                    "DEMO-OPENING", "Invented opening balance", DateTimeOffset.UtcNow.ToString("O"));
        }
        transaction.Commit();
    }

    public List<Item> GetItems()
    {
        using var connection = Open();
        using var command = Command(connection, null,
            "SELECT Sku, Name, Quantity, ReorderPoint, Version FROM Items ORDER BY Sku");
        using var reader = command.ExecuteReader();
        var items = new List<Item>();
        while (reader.Read())
            items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetInt64(4)));
        return items;
    }

    public ReceiptResult Receive(ReceiveRequest request)
    {
        var reference = Text(request.Reference, "Receipt reference", 80).ToUpperInvariant();
        if (request.Lines is not { Count: > 0 and <= 20 })
            throw new InventoryException(400, "Include between 1 and 20 receipt lines.");
        var lines = request.Lines.Select(line =>
        {
            if (line is null || line.Quantity is <= 0 or > 100_000)
                throw new InventoryException(400, "Each receipt quantity must be a whole number between 1 and 100,000.");
            return new ReceiptLine(Text(line.Sku, "Item code", 32).ToUpperInvariant(), line.Quantity);
        }).OrderBy(line => line.Sku, StringComparer.Ordinal).ToArray();
        if (lines.Select(line => line.Sku).Distinct(StringComparer.Ordinal).Count() != lines.Length)
            throw new InventoryException(400, "An item may appear only once in a receipt.");
        var payload = JsonSerializer.Serialize(lines);

        using var connection = Open();
        // Acquire the writer lock before checking receipt identity, including across processes.
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var existing = Command(connection, transaction,
            "SELECT Payload, CreatedAt FROM Receipts WHERE Reference=$ref", ("$ref", reference)))
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != payload)
                    throw new InventoryException(409, "This receipt reference was already used with different items or quantities.");
                return new(reference, reader.GetString(1), true);
            }
        }

        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        using (var receipt = Command(connection, transaction,
            "INSERT INTO Receipts(Reference,Payload,CreatedAt) VALUES($ref,$payload,$at)",
            ("$ref", reference), ("$payload", payload), ("$at", createdAt)))
            receipt.ExecuteNonQuery();
        foreach (var line in lines)
            ChangeStock(connection, transaction, line.Sku!, line.Quantity, null,
                "Receipt", reference, "", createdAt);
        transaction.Commit();
        return new(reference, createdAt, false);
    }

    public void Adjust(AdjustRequest request)
    {
        var sku = Text(request.Sku, "Item code", 32).ToUpperInvariant();
        var reason = Text(request.Reason, "Adjustment reason", 240);
        if (request.Delta is 0 or < -100_000 or > 100_000 || request.ExpectedVersion < 0)
            throw new InventoryException(400, "Use a nonzero whole-number change between -100,000 and 100,000 and a valid stock version.");
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        ChangeStock(connection, transaction, sku, request.Delta, request.ExpectedVersion,
            "Adjustment", "", reason, DateTimeOffset.UtcNow.ToString("O"));
        transaction.Commit();
    }

    private static void ChangeStock(SqliteConnection connection, SqliteTransaction transaction,
        string sku, int delta, long? expectedVersion, string kind, string reference, string note, string at)
    {
        int quantity;
        long version;
        using (var lookup = Command(connection, transaction,
            "SELECT Quantity,Version FROM Items WHERE Sku=$sku", ("$sku", sku)))
        using (var reader = lookup.ExecuteReader())
        {
            if (!reader.Read())
                throw new InventoryException(404, $"Item '{sku}' does not exist.");
            quantity = reader.GetInt32(0);
            version = reader.GetInt64(1);
        }
        if (expectedVersion is not null && expectedVersion != version)
            throw new InventoryException(409, "Stock changed since you loaded it. Refresh stock, review the history, and try again.");
        var balance = quantity + delta;
        if (balance is < 0 or > MaximumQuantity)
            throw new InventoryException(409, $"The change would put '{sku}' outside the allowed balance of 0 to 1,000,000.");
        using var update = Command(connection, transaction,
            "UPDATE Items SET Quantity=$qty,Version=Version+1 WHERE Sku=$sku AND Version=$version",
            ("$qty", balance), ("$sku", sku), ("$version", version));
        if (update.ExecuteNonQuery() != 1)
            throw new InventoryException(409, "Stock changed. Refresh before trying again.");
        AddMovement(connection, transaction, sku, kind, delta, balance, reference, note, at);
    }

    private static void AddMovement(SqliteConnection connection, SqliteTransaction transaction,
        string sku, string kind, int delta, int balance, string reference, string note, string at)
    {
        using var command = Command(connection, transaction, """
            INSERT INTO Movements(Sku,Kind,Delta,Balance,Reference,Note,CreatedAt)
            VALUES($sku,$kind,$delta,$balance,$ref,$note,$at)
            """, ("$sku", sku), ("$kind", kind), ("$delta", delta), ("$balance", balance),
            ("$ref", reference), ("$note", note), ("$at", at));
        command.ExecuteNonQuery();
    }

    public List<Movement> GetMovements(string? sku, bool all = false)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT m.Id,m.Sku,i.Name,m.Kind,m.Delta,m.Balance,m.Reference,m.Note,m.CreatedAt
            FROM Movements m JOIN Items i ON i.Sku=m.Sku
            WHERE $sku='' OR m.Sku=$sku ORDER BY m.Id DESC
            """ + (all ? "" : " LIMIT 100"), ("$sku", sku?.Trim().ToUpperInvariant() ?? ""));
        using var reader = command.ExecuteReader();
        var movements = new List<Movement>();
        while (reader.Read())
            movements.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7), reader.GetString(8)));
        return movements;
    }

    private static string Text(string? value, string label, int maximumLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > maximumLength || text.Any(char.IsControl))
            throw new InventoryException(400, $"{label} is required, must be at most {maximumLength} characters, and cannot contain control characters.");
        return text;
    }
}
