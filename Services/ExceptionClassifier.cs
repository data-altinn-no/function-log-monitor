namespace FunctionLogMonitor.Services;

public static class ExceptionClassifier
{
    // InvalidJmesPath is a real defect despite the prefix: broken config.
    public static bool IsExpectedBusiness(string type) =>
        type.StartsWith("Dan.", StringComparison.Ordinal)
        && !type.EndsWith("InvalidJmesPathExpressionException", StringComparison.Ordinal);

    private static readonly HashSet<string> TransientTypes = new(StringComparer.Ordinal)
    {
        "System.Threading.Tasks.TaskCanceledException",
        "System.OperationCanceledException",
        "System.TimeoutException",
        "Microsoft.Azure.WebJobs.Host.FunctionTimeoutException",
        "System.Net.Sockets.SocketException",
        "System.Net.Http.HttpRequestException",
        "System.Net.Http.HttpIOException",
        "System.Net.WebException",
        "System.IO.IOException",
        "System.ObjectDisposedException",
        "StackExchange.Redis.RedisTimeoutException",
        "StackExchange.Redis.RedisConnectionException",
        "Altinn.ApiClients.Maskinporten.Models.TokenRequestException",
        "Microsoft.IdentityModel.Tokens.SecurityTokenMalformedException",
        "Microsoft.Azure.WebJobs.Script.Workers.Rpc.RpcException",
        "Microsoft.Azure.WebJobs.Script.HostInitializationException",
        "Microsoft.Azure.WebJobs.Host.Executors.FunctionListenerException",
        "System.ServiceModel.ServerTooBusyException",
        "System.ServiceModel.Security.MessageSecurityException",
        "Azure.RequestFailedException",
    };

    public static bool IsTransientInfrastructure(string type)
    {
        if (TransientTypes.Contains(type)) return true;
        // Polly appends the generic arg, so exact match never fires.
        return type.StartsWith("Polly.CircuitBreaker.BrokenCircuitException", StringComparison.Ordinal)
            || type.StartsWith("Polly.Timeout.TimeoutRejectedException", StringComparison.Ordinal);
    }

    // 4xx is a real defect, 5xx is platform noise.
    public static bool IsPlatformCosmos(string type, string? message) =>
        type == "Microsoft.Azure.Cosmos.CosmosException"
        && message is not null
        && System.Text.RegularExpressions.Regex.IsMatch(message, @"success: \w+ \(5\d\d\)");

    public static bool ShouldSkip(string type, string? message) =>
        IsExpectedBusiness(type)
        || IsTransientInfrastructure(type)
        || IsPlatformCosmos(type, message);
}
