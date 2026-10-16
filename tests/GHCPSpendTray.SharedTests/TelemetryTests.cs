using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Azure.Core.Pipeline;
using System.Diagnostics.Tracing;
using Azure.Monitor.OpenTelemetry.Exporter;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Exporter;

internal static class TelemetryTests
{
    internal static async Task RunAsync(Action<bool, string> check, string root)
    {
        const string quota = """
            "quota_snapshots":{"premium_interactions":{"token_based_billing":true,
            "credits_used":100,"entitlement":1000,"has_quota":true,"unlimited":false}}
            """;
        foreach (var (enterprise, expected) in new (string, bool?)[]
        {
            ("", null), (""","enterprise_list":null""", null),
            (""","enterprise_list":[]""", false),
            (""","enterprise_list":[{"id":999}]""", false),
            (""","enterprise_list":[{"id":134482}]""", true),
            (""","enterprise_list":[{"id":1578}]""", true),
            (""","enterprise_list":[{"id":13213}]""", true),
            (""","enterprise_list":[{}]""", null),
            (""","enterprise_list":[{"id":"134482"}]""", null),
            (""","enterprise_list":[{},{"id":1578}]""", true)
        })
        {
            var snapshot = CopilotUsageProvider.ParseJson("{" + quota + enterprise + "}",
                "github.com:123", DateTimeOffset.UtcNow);
            check(snapshot.MicrosoftEnterprise == expected, "Enterprise metadata distinguishes matches, negatives and unknown.");
            check(!JsonSerializer.Serialize(snapshot, CoreJsonContext.Default.UsageSnapshot).Contains("microsoftEnterprise"),
                "Enterprise classification is never persisted with consumption history.");
        }

        check(!AppTelemetry.DiagnosticEndpointConfigured(_ => null), "Diagnostics are off without an explicit endpoint.");
        check(AppTelemetry.DiagnosticEndpointConfigured(name => name == "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT" ? "http://localhost:4318/v1/logs" : null),
            "Signal-specific endpoints enable diagnostics.");
        var diagnostics = new ConcurrentQueue<string>();
        var exported = new ConcurrentQueue<Dictionary<string, object?>>();
        using (var telemetry = new AppTelemetry(new("1.2.3", "synthetic"), diagnostics.Enqueue,
            () => new RecordingExporter(exported)))
        {
            Guid id = Guid.NewGuid();
            telemetry.Configure(false, id);
            telemetry.Started(0, "unknown");
            telemetry.Flush();
            check(exported.IsEmpty, "Opt-out prevents collection before the first event.");
            telemetry.Configure(true, id);
            using (var activity = new Activity("private-diagnostic-context").Start())
            using (telemetry.Logger.BeginScope(new Dictionary<string, object?> { ["private.scope"] = "not tracked" }))
            {
                telemetry.Started(2, "yes");
                telemetry.Refreshed(2, "yes", AccountStatus.Fresh);
                telemetry.Error(2, "yes", "StorageError");
                telemetry.Logger.LogWarning("Private diagnostic log must not enter tracked telemetry");
            }
            telemetry.Flush();
            var records = exported.ToArray();
            check(records.Length == 3, "Only the three explicit tracked events reach the tracked sink.");
            check(records.All(r => (string?)r["enduser.pseudo.id"] == id.ToString("D") &&
                (int?)r["account.count"] == 2), "Tracked identity and configured account count accompany each event.");
            string[] commonFields = ["microsoft.custom_event.name", "enduser.pseudo.id", "schema.version",
                "app.version", "installation.source", "os.type", "os.version", "os.architecture",
                "process.architecture", "account.count", "microsoft.internal"];
            check(records.All(r => commonFields.All(r.ContainsKey) && r.Keys.All(k =>
                commonFields.Contains(k) || k is "refresh.status" or "error.category" or "test.trace_id")),
                "Tracked events contain only the explicit schema, including OS and process architectures.");
            check(records.All(r => (ActivityTraceId)r["test.trace_id"]! == default &&
                !r.ContainsKey("private.scope")), "Tracked records do not inherit diagnostic context or scopes.");
            check(records.Select(r => (string?)r["microsoft.custom_event.name"])
                .SequenceEqual(["app.started", "data.refresh", "app.error"]), "Events have distinct stable names.");
            telemetry.Started(2, "yes");
            telemetry.Configure(false, id);
            check(exported.Count == 3, "Disabling drops already queued tracked records instead of flushing them.");
            telemetry.Configure(true, id);
            telemetry.Refreshed(2, "unknown", AccountStatus.NetworkError);
            telemetry.Flush();
            check(exported.Count == 4, "Re-enabling sends new events without replaying the old queue.");
        }

        string directory = Path.Combine(root, "telemetry-profile");
        var store = new JsonStore(directory);
        await store.SaveSettingsAsync(new() { TrackedTelemetryEnabled = false });
        Guid? savedId;
        using (var controller = new ApplicationController(directory, false, new NoCredentials(),
            telemetry: new("1.2.3", "synthetic", TrackedAllowed: false)))
        {
            await controller.InitializeAsync();
            check(!controller.Settings.TrackedTelemetryEnabled, "Controller honors persisted opt-out before startup.");
            savedId = (await store.LoadSettingsAsync()).Value.TelemetryId;
            check(savedId is not null && savedId != Guid.Empty, "Controller persists a random UUID.");
            await controller.SaveSettingsAsync(controller.Settings with { TrackedTelemetryEnabled = true });
        }
        using (var controller = new ApplicationController(directory, false, new NoCredentials(),
            telemetry: new("1.2.3", "synthetic", TrackedAllowed: false)))
        {
            await controller.InitializeAsync();
            check(controller.Settings.TrackedTelemetryEnabled &&
                (await store.LoadSettingsAsync()).Value.TelemetryId == savedId,
                "Telemetry preference and identity survive restart.");
        }
        var settingsWire = JsonSerializer.Deserialize("""
            {"method":"settings.save","settings":{"pollMinutes":10,"thresholds":"50",
            "notifications":true,"startup":false,"trackedTelemetryEnabled":false}}
            """, GHCPSpendTray.MacBridge.BridgeJsonContext.Default.Command)!;
        check(settingsWire.Settings?.TrackedTelemetryEnabled == false, "Native bridge carries the opt-out preference.");

        string demoDirectory = Path.Combine(root, "interactive-telemetry");
        var demoStore = new JsonStore(Path.Combine(demoDirectory, "telemetry-verification"));
        await demoStore.SaveSettingsAsync(new() { TrackedTelemetryEnabled = false });
        Guid? demoId;
        using (var demo = new DemoController(demoDirectory,
            telemetry: new("1.2.3", "demo", TrackedAllowed: false, SyntheticVerification: true)))
        {
            await demo.InitializeAsync();
            check(!demo.Settings.TrackedTelemetryEnabled, "Interactive verification loads opt-out before startup.");
            demoId = (await demoStore.LoadSettingsAsync()).Value.TelemetryId;
            check(demoId is not null && demoId != Guid.Empty, "Interactive verification persists its synthetic UUID.");
            await demo.SaveSettingsAsync(demo.Settings with { TrackedTelemetryEnabled = true });
        }
        using (var demo = new DemoController(demoDirectory,
            telemetry: new("1.2.3", "demo", TrackedAllowed: false, SyntheticVerification: true)))
        {
            await demo.InitializeAsync();
            check(demo.Settings.TrackedTelemetryEnabled &&
                (await demoStore.LoadSettingsAsync()).Value.TelemetryId == demoId,
                "Interactive verification keeps consent and UUID across restarts.");
            await demo.SaveSettingsAsync(demo.Settings with { TrackedTelemetryEnabled = false });
            await demo.RefreshAsync();
            check(!(await demoStore.LoadSettingsAsync()).Value.TrackedTelemetryEnabled,
                "Interactive opt-out remains saved after refresh.");
        }
        string ordinaryDemo = Path.Combine(root, "ordinary-demo-telemetry");
        using (var demo = new DemoController(ordinaryDemo, telemetry: new("1.2.3", "demo")))
            await demo.InitializeAsync();
        check(!Directory.Exists(ordinaryDemo), "Ordinary demo does not create a verification profile or enable tracking.");

        string? previousStatsbeat = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED");
        string? previousResources = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
        Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
        Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", "private.resource=synthetic-private-resource");
        try
        {
            var capture = new CaptureHttp();
            using var client = new HttpClient(capture);
            const string connection = "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://telemetry.example.test/";
            Guid id = Guid.NewGuid();
            using (var telemetry = new AppTelemetry(new("1.2.3", "synthetic", SyntheticVerification: true), diagnostics.Enqueue,
                () => new AzureMonitorLogExporter(new()
                {
                    ConnectionString = connection, Transport = new HttpClientTransport(client), DisableOfflineStorage = true
                })))
            {
                telemetry.Configure(true, id);
                telemetry.Started(2, "yes");
                telemetry.Flush();
            }
            check(capture.Bodies.Count > 0, "Actual Azure Monitor exporter produced an ingestion envelope.");
            using var envelope = JsonDocument.Parse(capture.Bodies.Single().Trim());
            var item = envelope.RootElement;
            check(item.GetProperty("tags").GetProperty("ai.user.id").GetString() == id.ToString("D"),
                "Azure Monitor maps enduser.pseudo.id to Application Insights user_Id.");
            check(item.GetProperty("data").GetProperty("baseType").GetString() == "EventData" &&
                item.GetProperty("data").GetProperty("baseData").GetProperty("name").GetString() == "app.started",
                "Tracked events map to Application Insights customEvents.");
            check(!capture.Bodies.Single().Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase),
                "Azure payload does not contain the machine name.");
            check(!capture.Bodies.Single().Contains("synthetic-private-resource"),
                "Diagnostic resource attributes never enrich tracked Azure envelopes.");
            check(item.GetProperty("tags").GetProperty("ai.operation.syntheticSource").GetString() ==
                "GHCPSpendTray.TelemetryVerification", "Live verification events are excluded from real usage reports.");

            // Explicitly opt-in only; ordinary and Native AOT test runs never contact Azure.
            if (Environment.GetEnvironmentVariable("GHCP_TELEMETRY_SMOKE_CONNECTION_STRING") is { Length: > 0 } live)
            {
                check(TelemetryConfiguration.ConnectionString == live, "Build-time connection string survives assembly metadata.");
                using var listener = new IngestionDiagnostics();
                using var telemetry = new AppTelemetry(new("1.2.3-verification", "synthetic", SyntheticVerification: true), diagnostics.Enqueue,
                    AppTelemetry.CreateAzureExporter);
                telemetry.Configure(true, id);
                telemetry.Started(2, "unknown");
                telemetry.Refreshed(2, "unknown", AccountStatus.Fresh);
                telemetry.Flush();
                Console.WriteLine($"Synthetic Azure verification user_Id: {id:D}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", previousStatsbeat);
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", previousResources);
        }
        check(diagnostics.IsEmpty, "Telemetry validation reported no export failures: " + string.Join("; ", diagnostics));
        await VerifyDiagnosticsAsync(check);
    }

    private static async Task VerifyDiagnosticsAsync(Action<bool, string> check)
    {
        string[] names = ["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT",
            "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_SDK_DISABLED",
            "OTEL_TRACES_SAMPLER"];
        var previous = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        var requests = new ConcurrentQueue<(string Path, byte[] Body)>();
        try
        {
            foreach (string name in names) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://localhost:4318/v1/traces");
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "http://localhost:4318/v1/logs");
            Environment.SetEnvironmentVariable("OTEL_TRACES_SAMPLER", "always_on");
            using var client = new HttpClient(new OtlpCapture(requests));
            ActivityTraceId traceId;
            string? exceptionMessage = null;
            string? exceptionFrame = null;
            using (var telemetry = new AppTelemetry(new("1.2.3", "synthetic", TrackedAllowed: false), _ => { },
                configureDiagnosticExporter: options =>
                {
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.HttpClientFactory = () => client;
                }))
            {
                using var root = AppInstrumentation.Activities.StartActivity("ui.synthetic");
                check(root is not null, "Configured OTel tracing listens to UI operation activities.");
                traceId = root!.TraceId;
                await Task.Yield();
                using var child = AppInstrumentation.Activities.StartActivity("consumption.synthetic");
                telemetry.Logger.LogInformation("Synthetic structured log {Detail}", "synthetic-detail");
                Trace.TraceWarning("Synthetic existing diagnostic");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var server = new TcpListener(IPAddress.Loopback, 0);
                server.Start();
                int port = ((IPEndPoint)server.LocalEndpoint).Port;
                using var http = HttpTransport.CreateClient();
                var pending = http.GetAsync(
                    $"http://127.0.0.1:{port}/synthetic-private-path?code=synthetic-private-query",
                    timeout.Token);
                using var socket = await server.AcceptTcpClientAsync(timeout.Token);
                await using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var headers = new List<string>();
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } header) headers.Add(header);
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                    timeout.Token);
                using var response = await pending;
                check(response.IsSuccessStatusCode && headers.All(h => !h.StartsWith("traceparent:", StringComparison.OrdinalIgnoreCase)),
                    "Real HTTP calls succeed without propagating diagnostic trace IDs to account hosts.");
                server.Stop();
                bool failed = false;
                try
                {
                    using var unexpected = await http.GetAsync(
                        $"http://127.0.0.1:{port}/synthetic-private-path?code=synthetic-private-query", timeout.Token);
                }
                catch (HttpRequestException ex)
                {
                    failed = true;
                    exceptionMessage = ex.Message;
                    exceptionFrame = ex.StackTrace?.Split('\n')[0].Trim();
                }
                check(failed, "A closed loopback endpoint exercises actual HTTP exception telemetry.");
                telemetry.Configure(true, Guid.NewGuid());
                telemetry.Started(0, "no");
            }
            byte[] traceBytes = new byte[16];
            traceId.CopyTo(traceBytes);
            var traces = requests.Where(r => r.Path == "/v1/traces").Select(r => r.Body).ToArray();
            var logs = requests.Where(r => r.Path == "/v1/logs").Select(r => r.Body).ToArray();
            check(traces.Any(b => Encoding.UTF8.GetString(b).Contains("ui.synthetic")) &&
                traces.Any(b => Encoding.UTF8.GetString(b).Contains("consumption.synthetic")),
                "Diagnostic OTLP exports full parent/child operation spans. Paths: " + string.Join(", ", requests.Select(r => r.Path)));
            check(logs.Any(b => Encoding.UTF8.GetString(b).Contains("synthetic-detail")) &&
                logs.Any(b => Encoding.UTF8.GetString(b).Contains("Synthetic existing diagnostic")),
                "ILogger structured properties and existing Trace diagnostics reach OTLP logs.");
            check(traces.Any(b => b.AsSpan().IndexOf(traceBytes) >= 0) &&
                logs.Any(b => b.AsSpan().IndexOf(traceBytes) >= 0),
                "OTLP logs and traces share the same distributed trace ID.");
            check(traces.Any(b => Encoding.UTF8.GetString(b).Contains("System.Net.Http")),
                "Actual HttpClient spans reach OTLP.");
            check(traces.All(b => !Encoding.UTF8.GetString(b).Contains("synthetic-private-path") &&
                    !Encoding.UTF8.GetString(b).Contains("synthetic-private-query")),
                "HttpClient spans exclude account URL paths and authorization queries.");
            check(traces.Any(b => Encoding.UTF8.GetString(b).Contains("exception.type")),
                "Native HTTP exceptions retain their type.");
            check(exceptionMessage is { Length: > 0 } message && exceptionFrame is { Length: > 0 } frame &&
                traces.All(b => !Encoding.UTF8.GetString(b).Contains(frame) &&
                    !Encoding.UTF8.GetString(b).Contains(message)),
                "Native HTTP exceptions exclude their original messages and stack traces.");
            check(requests.All(r => r.Path != "/v1/metrics") &&
                requests.All(r => !Encoding.UTF8.GetString(r.Body).Contains("enduser.pseudo.id")),
                "Unconfigured signals stay off and diagnostic payloads do not contain the tracked identifier.");
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private sealed class RecordingExporter(ConcurrentQueue<Dictionary<string, object?>> records) : BaseExporter<LogRecord>
    {
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var log in batch)
            {
                var attributes = log.Attributes!.ToDictionary(p => p.Key, p => p.Value);
                attributes["test.trace_id"] = log.TraceId;
                records.Enqueue(attributes);
            }
            return ExportResult.Success;
        }
    }

    private sealed class IngestionDiagnostics : EventListener
    {
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "OpenTelemetry-AzureMonitor-Exporter")
                EnableEvents(source, EventLevel.Verbose);
        }
        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (args.EventId == 8 && args.Payload is { Count: >= 2 } payload)
                Console.Error.WriteLine($"Synthetic ingestion status {payload[0]}: {payload[1]}");
            else if (args.EventId is 6 or 7 or 14)
                Console.Error.WriteLine($"Synthetic ingestion {args.EventName}: {args.Payload?.LastOrDefault()}");
        }
    }

    private sealed class CaptureHttp : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"itemsReceived":1,"itemsAccepted":1,"errors":[]}""", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class OtlpCapture(ConcurrentQueue<(string Path, byte[] Body)> requests) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue((request.RequestUri!.AbsolutePath,
                request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult()));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        }
    }

    private sealed class NoCredentials : ICredentialStore
    {
        public Task<TokenSet?> ReadAsync(Account account, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task WriteAsync(Account account, TokenSet tokens, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task DeleteAsync(Account account, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
