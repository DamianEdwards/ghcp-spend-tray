using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using GHSpend.App;
using GHSpend.Core;

var root = Path.Combine(Path.GetTempPath(), "GHSpend-AppTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var handler = new FixtureHttp();
    using var http = new HttpClient(handler);
    var credentials = new MemoryCredentials();
    using var app = new ApplicationController(root, true, http, credentials);
    int notifications = 0, assertions = 0;
    DashboardView? view = null;
    app.Changed += next => Volatile.Write(ref view, next);
    app.SetNotificationHandler((_, _, _) => { Interlocked.Increment(ref notifications); return Task.FromResult(true); });
    await app.InitializeAsync();
    Check(app.Settings.PollMinutes == 60, "exact one-hour default");
    Check(app.Portable && !app.Settings.Startup, "portable startup disabled");
    Check(GitHubOAuth.ClientId == "178c6fc778ccc68e1d6a", "verified GitHub CLI public client ID");
    var legacy = JsonSerializer.Deserialize(
        """{"version":1,"hostClientIds":{"github.com":"custom-registration"},"accounts":[{"host":"github.com","userId":"1","login":"test","clientId":"custom-registration"}]}""",
        CoreJsonContext.Default.AppSettings)!;
    legacy.Validate();
    Check(legacy.Accounts.Length == 1, "legacy configuration retains account identities");
    Check(!JsonSerializer.Serialize(legacy, CoreJsonContext.Default.AppSettings).Contains("custom-registration", StringComparison.Ordinal),
        "legacy client ID overrides are ignored and not rewritten");
    await Throws<AppOperationException>(() => app.SaveSettingsAsync(new(4, "50, 80", true, false)));
    Check(!File.Exists(Path.Combine(root, "config.json")), "invalid settings not saved");
    await app.SaveSettingsAsync(new(60, "100, 50, 80, 50", true, false));
    Check(app.Settings.Thresholds == "50, 80, 100", "threshold normalization");
    Check(app.ResolveHostDescription("EXAMPLE.ghe.com").Contains("https://api.example.ghe.com/"), "GHE API mapping in controller");

    await Add("github.com", "1");
    await Add("github.com", "2");
    await Add("example.ghe.com", "3");
    await app.RefreshAsync();
    await Until(() => Volatile.Read(ref view)?.Accounts.Count == 3);
    var config = await Load();
    Check(config.Accounts.Length == 3 && credentials.Values.Count == 3, "three distinct persisted credentials/accounts");
    Check(config.Accounts.Select(a => a.Key).Distinct().Count() == 3, "immutable identity keys");
    string persistedSettings = await File.ReadAllTextAsync(Path.Combine(root, "config.json"));
    Check(!persistedSettings.Contains("clientId", StringComparison.OrdinalIgnoreCase), "client ID is not configurable in persisted settings");
    Check(Volatile.Read(ref view)!.Total.Contains("$53.25"), "three-account consumption total");
    Check(notifications == 2, "independent account threshold alerts");
    await app.RefreshAsync();
    Check(notifications == 2, "refresh does not duplicate notifications");
    await Throws<AppOperationException>(() => Add("github.com", "1"));
    Check((await Load()).Accounts.Length == 3, "duplicate cannot double-count");
    handler.NextIdentity = "2";
    await Throws<AppOperationException>(() => app.AddAsync("github.com", false, "github.com:1",
        _ => { }, _ => Task.FromResult(true), default));
    Check(credentials.Values["github.com:1"].AccessToken == "fixture-1", "wrong-account reconnect preserves credential");
    handler.NextIdentity = "4";
    await Throws<OperationCanceledException>(() => app.AddAsync("github.com", false, null,
        _ => { }, _ => Task.FromResult(false), default));
    Check(credentials.Values.Count == 3, "declined identity is not persisted");
    credentials.Values["github.com:1"] = credentials.Values["github.com:1"] with
    {
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        RefreshToken = "refresh-1"
    };
    await app.RefreshAsync("github.com:1");
    Check(handler.RefreshRequests == 1, "fixed client ID also used for token refresh");
    Check(handler.OAuthRequests >= 13 && handler.OAuthClientIds.All(id => id == GitHubOAuth.ClientId),
        "device authorization, polling, and refresh use the fixed ID on all hosts");
    await app.SaveAccountAsync("github.com:2", "Work", "15, 120");
    await app.RefreshAsync("github.com:2");
    Check(notifications == 3, "new override below consumption alerts once");
    Check((await Load()).Accounts.Single(a => a.Key == "github.com:2").DisplayName == "Work", "display name persisted");
    await app.RemoveAsync("github.com:2");
    Check(!credentials.Values.ContainsKey("github.com:2") && (await Load()).Accounts.Length == 2, "local removal deletes credential and configuration");
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        Check(!(await File.ReadAllTextAsync(file)).Contains("fixture-", StringComparison.Ordinal), "no token in persisted files");
    Console.WriteLine($"PASS: {assertions} application integration assertions (synthetic HTTP and credentials only).");

    void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        assertions++;
    }
    async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    async Task Add(string host, string id)
    {
        handler.NextIdentity = id;
        await app.AddAsync(host, false, null,
            prompt => Check(prompt.VerificationUri.Host == host && prompt.Code == "TEST-CODE", "validated device prompt"),
            identity => Task.FromResult(identity.UserId > 0), default);
    }
    async Task<AppSettings> Load() => (await new JsonStore(root).LoadSettingsAsync()).Value;
    static async Task Until(Func<bool> predicate)
    {
        for (int i = 0; i < 100; i++)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Dashboard event did not arrive.");
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}

sealed class MemoryCredentials : ICredentialStore
{
    internal ConcurrentDictionary<string, TokenSet> Values { get; } = new(StringComparer.Ordinal);
    public Task<TokenSet?> ReadAsync(Account account, CancellationToken cancellationToken = default) =>
        Task.FromResult(Values.GetValueOrDefault(account.Key));
    public Task WriteAsync(Account account, TokenSet tokens, CancellationToken cancellationToken = default)
    { Values[account.Key] = tokens; return Task.CompletedTask; }
    public Task DeleteAsync(Account account, CancellationToken cancellationToken = default)
    { Values.TryRemove(account.Key, out _); return Task.CompletedTask; }
}
sealed class FixtureHttp : HttpMessageHandler
{
    internal string NextIdentity { get; set; } = "1";
    internal int RefreshRequests { get; private set; }
    internal int OAuthRequests => OAuthClientIds.Count;
    internal List<string> OAuthClientIds { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Dictionary<string, string> form = [];
        if (request.Method == HttpMethod.Post)
        {
            var encoded = await request.Content!.ReadAsStringAsync(cancellationToken);
            form = encoded.Split('&').Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
            if (form["client_id"] != GitHubOAuth.ClientId || form.ContainsKey("client_secret"))
                throw new InvalidOperationException("OAuth must use the fixed public ID without a client secret.");
            OAuthClientIds.Add(form["client_id"]);
        }
        string json;
        if (uri.AbsolutePath == "/login/device/code")
        {
            if (form["scope"] != "read:user")
                throw new InvalidOperationException("Changing the client ID must not add GitHub CLI repository scopes.");
            json = $$"""{"device_code":"synthetic-device-{{NextIdentity}}","user_code":"TEST-CODE","verification_uri":"https://{{uri.Host}}/login/device","expires_in":60,"interval":1}""";
        }
        else if (uri.AbsolutePath == "/login/oauth/access_token")
        {
            bool refresh = form["grant_type"] == "refresh_token";
            if (refresh) RefreshRequests++;
            string id = refresh ? form["refresh_token"]["refresh-".Length..] :
                form["device_code"]["synthetic-device-".Length..];
            json = $$"""{"access_token":"fixture-{{id}}","token_type":"bearer","scope":"read:user"}""";
        }
        else
        {
            string id = request.Headers.Authorization!.Parameter!["fixture-".Length..];
            if (id == "3" && uri.Host != "api.example.ghe.com" ||
                id != "3" && uri.Host != "api.github.com")
                throw new InvalidOperationException("Credential crossed host boundary.");
            if (uri.AbsolutePath == "/user") json = $$"""{"id":{{id}},"login":"test-{{id}}"}""";
            else if (uri.AbsolutePath == "/copilot_internal/user")
            {
                int credits = id switch { "1" => 2625, "2" => 1650, _ => 1050 };
                int entitlement = id switch { "1" => 2500, "2" => 10000, _ => 2000 };
                var reset = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
                json = $$"""{"quota_reset_date_utc":"{{reset:O}}","quota_snapshots":{"premium_interactions":{"token_based_billing":true,"credits_used":{{credits}},"entitlement":{{entitlement}},"has_quota":true,"unlimited":false""" + "}}}";
            }
            else throw new InvalidOperationException("Unexpected fixture endpoint.");
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
