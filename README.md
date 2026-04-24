# function-log-monitor

Timer-triggered **.NET 8 isolated-worker** Azure Function. Runs every 30 min,
queries Application Insights for recent exceptions, redacts sensitive fields,
and posts one GitHub issue per **new** signature into the **private**
landing-zone repo.

```
┌──────────────┐   KQL query    ┌────────────────────────┐   create issue   ┌──────────────────────────────┐
│ App Insights │ ◄───────────── │ Azure Function (30 min)│ ───────────────► │ data-altinn-no/core-triage   │
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
| `FunctionLogMonitor.csproj`     | .NET 8 isolated-worker Functions v4 project            |
| `Program.cs`                    | Host/DI setup                                          |
| `PollExceptions.cs`             | Timer-triggered function (CRON `0 */30 * * * *`)       |
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
| `GITHUB_TOKEN`        | Fine-grained PAT with `issues:write` on `core-triage`               |
| `GITHUB_INPUT_OWNER`  | `data-altinn-no`                                                    |
| `GITHUB_INPUT_REPO`   | `core-triage`                                                       |
| `TRIAGE_LABEL`        | `auto-triage`                                                       |
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

## Deploy

```bash
dotnet publish -c Release -o ./publish
cd publish
func azure functionapp publish <your-function-app>
```

Or use `az functionapp deployment source config-zip` against a Linux
Consumption plan running `dotnet-isolated`.

## Dedup in the Function (vs. in the agent)

The Function dedupes against recent **private** issues (by fingerprint stored
in an HTML comment) to avoid spamming the private repo when the same error
fires every 30 min. The agent handles dedup against the **public** repo
downstream.
