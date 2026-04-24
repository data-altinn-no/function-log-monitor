using FunctionLogMonitor;
using FunctionLogMonitor.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Octokit;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services
            .AddOptions<MonitorOptions>()
            .Configure(opts => opts.LoadFromEnvironment())
            .ValidateDataAnnotations();

        services.AddHttpClient<IAppInsightsClient, AppInsightsClient>();

        services.AddSingleton<IGitHubClient>(_ =>
        {
            var token =
                Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                ?? throw new InvalidOperationException("GITHUB_TOKEN is not configured");
            var client = new GitHubClient(new ProductHeaderValue("dan-agent-log-monitor"))
            {
                Credentials = new Credentials(token),
            };
            return client;
        });

        services.AddSingleton<IGitHubIssueWriter, GitHubIssueWriter>();
        services.AddSingleton<IRedactor, Redactor>();
    })
    .Build();

await host.RunAsync();
