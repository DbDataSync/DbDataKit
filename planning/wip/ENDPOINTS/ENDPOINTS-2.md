# ENDPOINTS-2 callers (wip)

Built: every caller of the Gateway that set the removed port / address now sets listen (run-flightsql-e2e.sh, run-interop.ps1, playwright.config.ts, Servers.Tests, the server.yaml text of the Playwright specs).
Open: verify by build and test runs; add ci-lessons row.
Defaults: none so far (the scheme is https, which every caller already used).
