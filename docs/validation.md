# Validation record

Checked on October 28, 2026, on Windows.

| Check | Result |
| --- | --- |
| API/database integration suite | 18 passed, 0 failed, 0 skipped |
| Fresh local checkout, locked .NET restore, Release tests | Passed; 18 tests |
| Fresh local checkout, `npm ci`, TypeScript check, production build | Passed |
| Chromium workflows, including desktop and phone layout | 2 passed; no uncaught page errors in the main flow |
| Built client served by ASP.NET Core with its Content Security Policy enabled | Receiving, replay, conflicting payload, and correction verified |
| Recorded walkthrough and screenshots | Captured from the running built application |
| Hosted Linux CI | [Passed](https://github.com/PatrickSchaller/inventory-receiving/actions/runs/40804575882), including all 18 API/database tests and both browser tests |

The browser checks used a 1440 × 1050 desktop viewport and a 390 × 844 phone viewport in Chromium. The phone check verifies page overflow and rendering; it is not a real-device test or a complete accessibility audit.

The restricted Windows runner could not complete Playwright's automatic server teardown. The final successful local browser run reused explicitly started local servers through a temporary runner configuration; the assertions and application code were unchanged. The repository configuration starts its own servers for normal use and hosted CI.

The Windows runner's .NET HTTPS transport could not reach NuGet. For local verification, the same public NuGet packages were downloaded over certificate-validated HTTPS into a temporary feed. Locked restores succeeded against that feed. No private package feed or credentials are required by the repository's NuGet configuration.

The hosted Linux result is recorded separately above. Firefox, WebKit, real devices, load testing, and a full security review are not established by these results.
