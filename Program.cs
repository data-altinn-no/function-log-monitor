using FunctionLogMonitor;
using FunctionLogMonitor.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

        services.AddSingleton<IGitHubClientFactory, GitHubClientFactory>();

        services.AddSingleton<IGitHubIssueWriter, GitHubIssueWriter>();
        services.AddSingleton<IRedactor, Redactor>();
    })
    .ConfigureLogging(logging =>
        logging.Services.Configure<LoggerFilterOptions>(
            WorkerLogging.RemoveApplicationInsightsWarningFilter))
    .Build();

await host.RunAsync();
