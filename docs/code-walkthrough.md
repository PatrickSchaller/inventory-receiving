# Code walkthrough

Start with the business behavior, then follow one request through the code.

## Receive five handles

1. In [the React client](../client/src/main.tsx), `save` posts the reference and receipt lines. The button is disabled while the request is pending, but correctness does not depend on that button state.
2. [Program.cs](../src/Receiving.Api/Program.cs) checks request intent, binds JSON, and calls the store. Business errors become Problem Details with a suitable status code.
3. In [InventoryStore.cs](../src/Receiving.Api/InventoryStore.cs), `Receive` validates and normalizes the input, then opens an immediate write transaction.
4. A receipt lookup decides whether this is a replay, a conflicting reference, or a new receipt. The database primary key also enforces reference uniqueness.
5. `ChangeStock` validates the resulting balance, updates the item version, and adds a movement. Only after all lines succeed does the transaction commit.
6. The client reloads stock and history. An unknown network outcome explains that the user should refresh and inspect history before retrying.

## Why the transaction matters

The rollback test deliberately supplies a valid item followed by an unknown item. The earlier item's stock update and movement have already been attempted when the later line fails. Disposing the uncommitted transaction rolls both back, including the receipt identity, so the corrected receipt can be submitted again.

The concurrency tests use separate requests and database connections. They cover several identical receipts, several distinct receipts, and two corrections based on the same stock version.

## Corrections and retries

Additive receipts operate on the balance inside the serialized transaction. A manual correction has a different risk: the user may be deciding from stale information. `ExpectedVersion` makes that assumption explicit. After any accepted stock change, the earlier version is no longer valid.

## A small trust boundary

[CsvExport.cs](../src/Receiving.Api/CsvExport.cs) treats textual cells as potentially unsafe spreadsheet content. Quoting solves CSV structure; an apostrophe prefix addresses formula-like text. The numeric delta column is handled separately so a valid negative stock movement stays numeric.

## Questions worth answering before presenting the project

- What would happen if duplicate protection existed only in the browser or in a process-level lock?
- Which writes would survive if the second line of a receipt failed without a transaction?
- Why do manual corrections carry a version while additive receipt lines do not?
- What can a client infer from a timeout, and how does it avoid applying an uncertain write twice?
- Why is the custom request header not a substitute for user authentication?
- What would need to change for fractional units, multiple locations, or a different database engine?

Use [InventoryTests.cs](../tests/Receiving.Tests/InventoryTests.cs) to find executable evidence for the answers. Change a rule, add a relevant check, and explain the resulting behavior as you review the project.
