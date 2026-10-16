using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Azure.Monitor.OpenTelemetry.Exporter;
using GHCPSpendTray.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace GHCPSpendTray.Shared;

public sealed record TelemetryConfiguration(string AppVersion, string InstallationSource, bool TrackedAllowed = true,
    string? OsVersion = null)
{
    public static string? ConnectionString => typeof(TelemetryConfiguration).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(a => a.Key == "ApplicationInsightsConnectionString")?.Value;
}

internal sealed class AppTelemetry : IDisposable
{
    private readonly object _sync = new();
    private readonly TelemetryConfiguration _configuration;
    private readonly Func<BaseExporter<LogRecord>> _createExporter;
    private readonly ILoggerFactory _diagnosticLogs;
    private readonly TracerProvider? _traces;
    private readonly MeterProvider? _metrics;
    private readonly ActivityListener? _httpExceptions;
    private readonly Action<string> _diagnostic;
    private readonly TraceListener _traceListener;
    private readonly bool _canTrack;
    private readonly BaseProcessor<LogRecord>? _trackedProcessor;
    private ILoggerFactory? _trackedLogs;
    private ConsentExporter? _consent;
    private BatchLogRecordExportProcessor? _batch;
    private Guid _id;
    private bool _disposed;
    internal ILogger Logger { get; }

    internal AppTelemetry(TelemetryConfiguration configuration, Action<string> diagnostic,
        Func<BaseExporter<LogRecord>>? createExporter = null,
        Action<OtlpExporterOptions>? configureDiagnosticExporter = null,
        BaseProcessor<LogRecord>? trackedProcessor = null)
    {
        _configuration = configuration;
        _diagnostic = diagnostic;
        _createExporter = createExporter ?? CreateAzureExporter;
        _trackedProcessor = trackedProcessor;
        _canTrack = configuration.TrackedAllowed &&
            (createExporter is not null || !string.IsNullOrWhiteSpace(TelemetryConfiguration.ConnectionString));
        bool enabled = DiagnosticEndpointConfigured(Environment.GetEnvironmentVariable) &&
            !string.Equals(Environment.GetEnvironmentVariable("OTEL_SDK_DISABLED"), "true", StringComparison.OrdinalIgnoreCase);
        var resource = ResourceBuilder.CreateDefault().AddService("GHCPSpendTray", serviceVersion: configuration.AppVersion);
        bool SignalEnabled(string signal) => enabled &&
            (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")) ||
             !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT")));
        _diagnosticLogs = LoggerFactory.Create(builder =>
        {
            if (SignalEnabled("LOGS")) builder.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(resource);
                options.IncludeScopes = true;
                options.ParseStateValues = true;
                options.AddOtlpExporter(options => configureDiagnosticExporter?.Invoke(options));
            });
        });
        Logger = _diagnosticLogs.CreateLogger("GHCPSpendTray.Diagnostics");
        if (SignalEnabled("TRACES"))
        {
            _traces = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(resource)
                .AddSource(AppInstrumentation.SourceName, "System.Net.Http")
                .AddProcessor(new SafeHttpProcessor())
                .AddOtlpExporter(options => configureDiagnosticExporter?.Invoke(options)).Build();
        }
        if (SignalEnabled("METRICS"))
            _metrics = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(resource)
                .AddMeter("System.Net.Http", "System.Runtime")
                .AddOtlpExporter(options => configureDiagnosticExporter?.Invoke(options)).Build();
        if (_traces is not null)
        {
            _httpExceptions = new()
            {
                ShouldListenTo = static source => source.Name == "System.Net.Http",
                // Null overrides prevent .NET 10 from adding exception messages and stack traces.
                ExceptionRecorder = static (Activity activity, Exception exception, ref TagList tags) =>
                    tags = new() { { "exception.message", null }, { "exception.stacktrace", null } }
            };
            ActivitySource.AddActivityListener(_httpExceptions);
        }
        _traceListener = new DiagnosticTraceListener(Logger);
        Trace.Listeners.Add(_traceListener);
    }

    internal static bool DiagnosticEndpointConfigured(Func<string, string?> read) =>
        new[] { "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
            "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT" }
            .Any(name => !string.IsNullOrWhiteSpace(read(name)));

    internal static BaseExporter<LogRecord> CreateAzureExporter()
    {
        // Exporter SDK diagnostics are not part of our tracked data contract.
        Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
        return new AzureMonitorLogExporter(new()
        {
            ConnectionString = TelemetryConfiguration.ConnectionString,
            DisableOfflineStorage = true,
            EnableLiveMetrics = false,
            EnableStandardMetrics = false,
            EnablePerformanceCounters = false,
            EnableTraceBasedLogsSampler = false,
            Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) }
        });
    }

    internal void Configure(bool enabled, Guid id)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _id = id;
            if (!enabled)
            {
                // Close the export gate before disposing/flushing the batch processor.
                _consent?.Disable();
                _trackedLogs?.Dispose();
                _trackedLogs = null;
                _consent = null;
                _batch = null;
            }
            else if (_canTrack && _trackedLogs is null)
            {
                _consent = new(_createExporter(), _diagnostic);
                _trackedLogs = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
                {
                    // Never import OTEL_RESOURCE_ATTRIBUTES, machine names, or diagnostic scopes.
                    options.SetResourceBuilder(ResourceBuilder.CreateEmpty());
                    if (_trackedProcessor is not null) options.AddProcessor(_trackedProcessor);
                    options.AddProcessor(new UncorrelatedLogProcessor());
                    _batch = new BatchLogRecordExportProcessor(_consent,
                        maxQueueSize: 256, scheduledDelayMilliseconds: 1000, exporterTimeoutMilliseconds: 4000,
                        maxExportBatchSize: 64);
                    options.AddProcessor(_batch);
                }));
            }
        }
    }

    internal void Started(int count, string affiliation) => Event("app.started", count, affiliation);
    internal void Refreshed(int count, string affiliation, AccountStatus status) =>
        Event("data.refresh", count, affiliation, "refresh.status", status.ToString());
    internal void Error(int count, string affiliation, string category) =>
        Event("app.error", count, affiliation, "error.category", category);

    private void Event(string name, int count, string affiliation, string? key = null, string? value = null)
    {
        lock (_sync)
        {
            if (_disposed || _trackedLogs is null) return;
            List<KeyValuePair<string, object?>> attributes =
            [
                new("microsoft.custom_event.name", name),
                new("enduser.pseudo.id", _id.ToString("D")),
                new("schema.version", 1),
                new("app.version", _configuration.AppVersion),
                new("installation.source", _configuration.InstallationSource),
                new("os.type", OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "other"),
                new("os.version", _configuration.OsVersion ?? Environment.OSVersion.Version.ToString()),
                new("os.architecture", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()),
                new("process.architecture", RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()),
                new("account.count", count),
                new("microsoft.internal", affiliation)
            ];
            if (key is not null) attributes.Add(new(key, value));
            _trackedLogs.CreateLogger("GHCPSpendTray.Tracked").Log(
                name == "app.error" ? LogLevel.Error : LogLevel.Information, default, attributes, null,
                static (_, _) => "");
        }
    }

    internal void Flush()
    {
        lock (_sync)
        {
            if (_batch is not null && !_batch.ForceFlush(5000))
                _diagnostic("Tracked telemetry flush did not complete.");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _trackedLogs?.Dispose();
        }
        _traces?.Dispose();
        _httpExceptions?.Dispose();
        _metrics?.Dispose();
        Trace.Listeners.Remove(_traceListener);
        _traceListener.Dispose();
        _diagnosticLogs.Dispose();
    }

    private sealed class DiagnosticTraceListener(ILogger logger) : TraceListener
    {
        public override void Write(string? message) => logger.LogWarning("{Diagnostic}", message);
        public override void WriteLine(string? message) => logger.LogWarning("{Diagnostic}", message);
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType,
            int id, string? message) => logger.Log(
                eventType is TraceEventType.Error or TraceEventType.Critical ? LogLevel.Error : LogLevel.Warning,
                "{Diagnostic}", message);
    }

    private sealed class UncorrelatedLogProcessor : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            data.TraceId = default;
            data.SpanId = default;
            data.TraceFlags = default;
        }
    }

    internal sealed class ConsentExporter(BaseExporter<LogRecord> inner, Action<string> diagnostic) : BaseExporter<LogRecord>
    {
        private readonly object _gate = new();
        private bool _enabled = true;
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            lock (_gate)
            {
                if (!_enabled) return ExportResult.Success;
                var result = inner.Export(batch);
                if (result == ExportResult.Failure) diagnostic("Tracked telemetry export failed.");
                return result;
            }
        }
        internal void Disable() { lock (_gate) _enabled = false; }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); }
    }

    private sealed class SafeHttpProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)
        {
            if (activity.Source.Name != "System.Net.Http") return;
            // Full URLs can contain identities, signed avatar URLs, and authorization parameters.
            activity.SetTag("url.full", null);
            activity.SetTag("url.path", null);
            activity.SetTag("url.query", null);
            activity.SetTag("http.url", null);
            activity.SetTag("exception.message", null);
            activity.SetTag("exception.stacktrace", null);
            activity.SetStatus(activity.Status);
        }
    }
}
