# function-log-monitor

**.NET 10 isolated-worker** Azure Function with **two** timer-triggered pollers,
both on `0 */30 * * * *`. Each queries Application Insights, redacts sensitive
fields, and posts one GitHub issue per **new** signature into the **private**
landing-zone repo.

| Function | Table | Label |
| ----------------- | ------------ | ------------------------- |
| `PollExceptions` | `exceptions` | `TRIAGE_LABEL_EXCEPTIONS` |
| `PollServerErrors` | `traces` (`severityLevel > 2`) | `TRIAGE_LABEL_ERRORS` |

> **Requires the .NET 10 SDK.** The project targets `net10.0`; older SDKs
> cannot build it.

```
┌──────────────┐   KQL query    ┌────────────────────────┐   create issue   ┌──────────────────────────────┐
│ App Insights │ ◄───────────── │ Azure Function (30 min)│ ───────────────► │ data-altinn-no/log-triage   │
└──────────────┘                │  redact + fingerprint  │                  │         (private)            │
                                └────────────────────────┘                  └──────────────┬───────────────┘
                                                                                           │ webhook
                                                                                           ▼
                                                                                      ┌──────────┐
                                                                                      │ dan-agent│
                                                                                      └──────────┘
```

## Layout

| File                            | Purpose                                                |
| ------------------------------- | ------------------------------------------------------ |
| `FunctionLogMonitor.csproj`     | .NET 10 isolated-worker Functions v4 project           |
| `Program.cs`                    | Host/DI setup                                          |
| `PollExceptions.cs`             | Exceptions poller (CRON `0 */30 * * * *`)              |
| `PollServerErrors.cs`           | Traces poller (CRON `0 */30 * * * *`)                  |
| `tests/FunctionLogMonitor.Tests`| xunit tests for Redactor + Fingerprint                 |
| `MonitorOptions.cs`             | Bound configuration                                    |
| `Services/AppInsightsClient.cs` | KQL query via App Insights REST API                    |
| `Services/Redactor.cs`          | PII / secret redaction + safety assertion              |
| `Services/Fingerprint.cs`       | Stable error fingerprinting (matches the agent)        |
| `Services/GitHubIssueWriter.cs` | Octokit-based issue create + existing-fingerprint scan |
| `host.json`                     | Functions host config                                  |
| `local.settings.json.example`   | Local dev environment                                  |

## Configuration (app settings)

| Setting               | Description                                                         |
| --------------------- | ------------------------------------------------------------------- |
| `APPINSIGHTS_APP_ID`  | App Insights API "Application ID"                                   |
| `APPINSIGHTS_API_KEY` | App Insights read-only API key (or use Managed Identity — see note) |
| `GITHUB_TOKEN`        | Fine-grained PAT with `issues:write` on `log-triage`               |
| `GITHUB_INPUT_OWNER`  | `data-altinn-no`                                                    |
| `GITHUB_INPUT_REPO`   | `log-triage`                                                       |
| `TRIAGE_LABEL_EXCEPTIONS` | `auto-triage-exceptions`                                        |
| `TRIAGE_LABEL_ERRORS`     | `auto-triage-errors`                                            |
| `LOOKBACK_MINUTES`    | `30` (match the timer cadence)                                      |

**Recommended (prod):** use a **User-Assigned Managed Identity** granted
`Log Analytics Reader` on the App Insights workspace and swap the API-key path
for `Azure.Monitor.Query` + `DefaultAzureCredential`. Octokit already accepts
a GitHub App installation token if you prefer that over a PAT.

## Local dev

```bash
cp local.settings.json.example local.settings.json   # fill in values
dotnet build
func start
```

## Tests

```bash
dotnet test
```

`tests/FunctionLogMonitor.Tests` covers `Redactor` (the PII boundary) and
`Fingerprint` (which decides whether an error is "new", and therefore whether
an issue is filed at all). Several tests are named `KnownDefect_*`: they assert
current, undesired behaviour on purpose, so that fixing the underlying rule
trips the test deliberately rather than silently changing redaction or
fingerprinting semantics. CI runs `dotnet test` before deploying.

## Deploy

```bash
dotnet publish -c Release -o ./publish
cd publish
func azure functionapp publish <your-function-app>
```

Or use `az functionapp deployment source config-zip` against a Linux
Consumption plan running `dotnet-isolated`.

## Filtering

Derived from a census of all 13,403 issues this monitor filed between 2026-04-29
and 2026-08-03. The full analysis lives in `data-altinn-no/log-triage`
(`analysis/root-cause.md`).

**The problem it was built to fix:** one condition — a consent-reminder
notification rejected with `422 NOT-00001` — produced **9,184 issues, 68.5% of
the entire repo**. The traces query summarised `by cloud_RoleName, message`, and
the message embeds an `aid=<guid>` plus a W3C traceparent, so every occurrence
formed its own group. The summarize collapsed nothing.

### Layers, and what each is worth

| Layer | Where | Measured effect |
| --- | --- | --- |
| Group traces by normalised template, not raw message | `AppInsightsClient.KqlTemplateServerErrors` | 12,403 → ~79 issues; the 9,184 cluster becomes **1 row** with `occurrences=9184` |
| Drop the host's `Executed '...' (Failed…)` wrapper | same | −1,568 occurrences; it duplicates every real failure |
| Exception-type triage | `ExceptionClassifier` | 1,000 → 200 issues |
| Fingerprint normalisation (traceparent, hex, query, version) | `Fingerprint` | 12,689 → ~3,100 distinct fingerprints; the 9,184 cluster: 9,186 → **3** |
| Occurrence threshold, traces only | KQL `MinOcc` | the only volume control for traces; 792 signatures → 2 |
| Per-run cap | `MAX_ISSUES_PER_RUN` | bounds blast radius during an incident |

All **15** recurring defect sites in the census survive every layer.

### Replaying the whole filter over the census

Simulated with the real `Fingerprint` / `ExceptionClassifier` / `StackFlattener`
classes over all 13,403 filed issues:

```
                        actual    with this filter
exception path            1,000      170
trace path               12,403        2
                        -------   --------
total                    13,403      172        (78x fewer)
defects preserved                  15/15
```

**Why there is no occurrence threshold on exceptions.** There was one, set to 3,
and it dropped 3 of the 15 real defects — Trad `Main.cs:180`/`:187` and Brreg
`AnnualFinancialReport.cs:321` each occurred only 2–4 times in 97 days. A rare
defect is still a defect, and dedup means it files exactly one issue. The
threshold's actual job, suppressing one-off transients, is already done by
`ExceptionClassifier`. Measured:

| minOcc | exception issues | defects kept |
| --- | --- | --- |
| 1 | 170 | **15/15** |
| 2 | 116 | 14/15 |
| 3 | 95 | 12/15 |
| 5 | 71 | 11/15 |
| 25 | 0 | 0/15 |

The knob still exists (`MIN_OCCURRENCES_EXCEPTIONS`, default `1` = no-op), but
note its window is the 30-minute poll, not 24h, so any value above 1 is far
stricter than the table suggests.

### The trap

The expected-business exceptions (`InvalidSubjectException`,
`AuthorizationFailedException`, `UnknownEvidenceCodeException`,
`EvidenceSource*Exception`) all have *perfect* first-party stack frames.
Filtering on "has a first-party frame" still leaves ~390 unactionable issues.
Type triage is what does the real work — see `ExceptionClassifier`.

`ExceptionClassifier.IsExpectedBusiness` supersedes a bare
`StartsWith("Dan.")`: that check was directionally right but also swallowed
`InvalidJmesPathExpressionException`, which is a real defect (a deployed
evidence-code definition with invalid JMESPath).

### Stack frames

`PollExceptions` now emits flattened text frames plus an explicit
`### Top frame` section. Previously the raw App Insights `parsedStack` JSON was
written, which the downstream agent's locator could not parse — it matched
**0 of 13,403** issues, even though 743 of them contained a usable first-party
frame all along. `StackFlattener` recovers frames by pattern when the payload is
truncated (507 of 1,000 archived payloads are cut mid-object at the 8,000-char
cap).

The emitted frame format is method + file + line only, deliberately without the
assembly: the assembly carries `Version=1.0.0.0`, which `Redactor`'s IPv4 rule
rewrites to `Version=<ip>`. See
`RedactorTests.AssemblyVersionIsMangled_WhichIsWhyFramesOmitTheAssembly`.

### Rolling it out

`DRY_RUN=true` logs `poll.would_create` per candidate without filing anything.
It defaults to `false`, so the filter is live unless the app setting is present;
`local.settings.json.example` sets it true so local runs never file issues.

Note the parse is `== "true"` exactly - `1`, `yes` and `on` all evaluate to
false and will silently write issues.

## Dedup in the Function (vs. in the agent)

The Function dedupes against recent **private** issues (by fingerprint stored
in an HTML comment) to avoid spamming the private repo when the same error
fires every 30 min. The agent handles dedup against the **public** repo
downstream.
