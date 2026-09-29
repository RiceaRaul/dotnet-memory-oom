using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryDiagnostics;

public static class MemoryDiagnosticsExtensions
{
    /// <summary>
    /// Registers memory diagnostics only when MemoryDiagnostics:Enabled is "1" or "true"
    /// (env var MemoryDiagnostics__Enabled=1). Any other value, or missing, registers nothing.
    /// </summary>
    public static IServiceCollection AddMemoryDiagnostics(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(MemoryDiagnosticsOptions.SectionName);
        if (!IsEnabled(section["Enabled"]))
            return services;

        var options = section.Get<MemoryDiagnosticsOptions>() ?? new MemoryDiagnosticsOptions();

        services.Configure<MemoryDiagnosticsOptions>(section);
        services.AddHostedService<MemoryMonitor>();

        if (options.FailFastOnOutOfMemory)
            InstallFailFastOnOutOfMemory();

        return services;
    }

    private static bool IsEnabled(string? value) =>
        value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // In the pod, OOMKilled terminates the process. Locally, OutOfMemoryException is caught by Kestrel
    // and turned into a 500, so no crash dump is written. FailFast makes the process die the same way
    // and triggers createdump (DOTNET_DbgEnableMiniDump).
    private static void InstallFailFastOnOutOfMemory()
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is OutOfMemoryException)
                Environment.FailFast("OutOfMemoryException - GC heap limit reached", e.Exception);
        };
    }
}
