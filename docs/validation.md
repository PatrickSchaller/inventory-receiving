# Validation record

Checked on October 29, 2026, on Windows.

| Check | Result |
| --- | --- |
| API/database/documentation integration suite | 21 passed, 0 failed, 0 skipped |
| Runtime OpenAPI contract | Five routes; matches the committed generated snapshot, including required client marker, input bounds, Problem Details and Retry-After |
| Published `.http` walkthrough | All 12 requests executed against a new SQLite database; expected statuses, replay result, final quantity/version and movement counts verified |
| TypeScript check and production client build | Passed |
| Chromium workflows | Three passed across the targeted runs: receiving/correction/export, unavailable service, and local API explorer GET/POST execution |
| API explorer | Application CSP enabled; no browser console or uncaught script errors; request marker sent; desktop and 390px layout checked |
| Public static reference | Read-only explorer rendered from the same files in the portfolio browser checks; no external requests |

The React browser checks use a 1440 × 1050 desktop viewport and a 390 × 844 phone viewport. The phone checks verify page overflow and rendering; they are not real-device tests or a complete accessibility audit.

The restricted Windows runner cannot reliably complete Playwright's automatic server teardown. Local browser runs reused explicitly started API and Vite servers through a temporary runner configuration. The assertions and application code were unchanged. The repository configuration starts its own servers for normal use and hosted CI.

The Windows runner's .NET HTTPS transport could not reach NuGet. For local verification, the same public NuGet packages were downloaded over certificate-validated HTTPS into a temporary feed. Locked restores succeeded against that feed. No private package feed or credentials are required by the repository's NuGet configuration.

The current hosted Linux result is available in the repository's [Checks workflow](https://github.com/PatrickSchaller/inventory-receiving/actions/workflows/checks.yml). Firefox, WebKit, real devices, load testing, and production identity integration are not established by these results. The public explorer does not host an executable backend.
