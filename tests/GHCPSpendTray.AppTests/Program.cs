using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using GHCPSpendTray.App;
using GHCPSpendTray.Core;
using GHCPSpendTray.App.Platform;
using GHCPSpendTray.App.UI;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;

var root = Path.Combine(Path.GetTempPath(), "GHCPSpendTray-AppTests-" + Guid.NewGuid().ToString("N"));
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
    using (var session = new AppSession(app, action => action()))
    {
        Check(session.Page == SettingsPage.Usage, "usage is the initial settings page");
        session.Navigate(SettingsPage.General);
        Check(session.Page == SettingsPage.General, "existing settings pages remain navigable");
        session.Navigate(SettingsPage.Usage);
        Check(session.Page == SettingsPage.Usage, "usage remains navigable after visiting other settings");
    }
    foreach (var work in new[] { new PixelRect(0, 0, 1920, 1040), new PixelRect(-1920, -100, 1920, 1080) })
    {
        var icon = new PixelRect(work.Right - 80, work.Bottom, 24, 24);
        var positioned = FlyoutPlacement.AboveIcon(icon, work, 400, 590, 10);
        Check(positioned.X >= work.X && positioned.Right <= work.Right &&
            positioned.Y >= work.Y && positioned.Bottom <= icon.Y, "flyout stays above icon and within monitor work area");
        var topIcon = icon with { Y = work.Y };
        var below = FlyoutPlacement.AboveIcon(topIcon, work, 400, 590, 10);
        Check(below.Y >= topIcon.Bottom && below.Bottom <= work.Bottom, "top-edge tray falls back below icon");
    }
    var narrow = FlyoutPlacement.AboveIcon(new(1, 20, 1, 1), new(0, 0, 15, 20), 400, 600, 30);
    Check(narrow.X >= 0 && narrow.Y >= 0 && narrow.Right <= 15 && narrow.Bottom <= 20,
        "oversized high-DPI flyout clamps safely in a constrained work area");
    Check(GitHubOAuth.ClientId == "Ov23ctzkXY5CJhfKQo7T", "project-owned GHCPSpendTray public client ID");
    Check(GitHubOAuth.ResolveClientId("https://MSFT.ghe.com/") == "Ov23ox38SoD1bIpzU9zZ",
        "enterprise registration selected after host normalization");
    Check(GitHubOAuth.ResolveClientId("https://GITHUB.COM:443/") == GitHubOAuth.ClientId,
        "github.com registration preserved after normalization");
    await Throws<ServiceException>(() => Task.FromResult(GitHubOAuth.ResolveClientId("msft.ghe.com.other.test")));
    await Throws<ArgumentException>(() => Task.FromResult(GitHubOAuth.ResolveClientId("msft.ghe.com:8443")));
    Check(GitHubOAuth.ResolveClientId("team.ghe.com", "team-registration") == "team-registration",
        "custom enterprise registration selected explicitly");
    await Throws<ArgumentException>(() => Task.FromResult(GitHubOAuth.ResolveClientId("github.com", "other-registration")));
    await Throws<ArgumentException>(() => Task.FromResult(GitHubOAuth.ResolveClientId("team.ghe.com", "bad id")));
    using (var session = new AppSession(app, action => action()))
    {
        session.AddAccount();
        Check(!session.CustomHost && session.Host == "github.com", "onboarding defaults to github.com");
        session.SelectHost(true);
        Check(session.CustomHost && session.Host == "" && session.ClientId == "", "custom host starts empty");
        session.SetHost("msft.ghe.com");
        Check(session.ClientId == GitHubOAuth.MicrosoftEnterpriseClientId, "known enterprise host prefills registration");
        session.SetClientId("override-registration");
        session.SetHost("msft.ghe.com");
        Check(session.ClientId == "override-registration", "unchanged hostname does not reset edited client ID");
        session.SetHost("team.ghe.com");
        Check(session.ClientId == "", "changing host clears a prior host's client ID");
        session.SelectHost(false);
        Check(session.Host == "github.com" && session.ClientId == "", "returning to github.com clears custom registration");
    }
    var sampleAccount = new Account { Host = "github.com", UserId = "1", Login = "fixture" };
    var installedTarget = new VaultCredentials(null).Target(sampleAccount);
    var portableTarget = new VaultCredentials(root).Target(sampleAccount);
    Check(installedTarget.StartsWith($"GHCPSpendTray/v1/{GitHubOAuth.ClientId}/", StringComparison.Ordinal),
        "installed credentials isolated from legacy CLI-issued tokens");
    Check(portableTarget.Contains($"/{GitHubOAuth.ClientId}/", StringComparison.Ordinal) && portableTarget != installedTarget,
        "portable credentials isolated by registration and data directory");
    var enterpriseAccount = sampleAccount with { Host = "msft.ghe.com" };
    var enterpriseTarget = new VaultCredentials(null).Target(enterpriseAccount);
    Check(enterpriseTarget.StartsWith("GHCPSpendTray/v1/Ov23ox38SoD1bIpzU9zZ/", StringComparison.Ordinal) &&
        enterpriseTarget != installedTarget, "same immutable ID on different hosts has separate registration-scoped credentials");
    Check(new VaultCredentials(null).Target(enterpriseAccount with { Host = "MSFT.GHE.COM" }) == enterpriseTarget,
        "credential target normalizes host casing");
    var customAccount = sampleAccount with { Host = "team.ghe.com", OAuthClientId = "team-registration" };
    Check(new VaultCredentials(null).Target(customAccount) != new VaultCredentials(null).Target(
        customAccount with { OAuthClientId = "other-registration" }), "credential targets partition OAuth registrations");
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
    using (var session = new GHCPSpendTray.App.UI.AppSession(app, action => action()))
    {
        session.SaveGlobal();
        await Until(() => !session.Busy);
        Check(session.Notice == "Settings saved.", "saving settings confirms success in settings");
        Check(GHCPSpendTray.App.UI.UI.Feedback(session) is not null, "settings displays save confirmation");
        Check(GHCPSpendTray.App.UI.UI.Feedback(session, showNotice: false) is null,
            "flyout does not display settings save confirmation");
        session.SetError("Synthetic refresh failure.");
        Check(GHCPSpendTray.App.UI.UI.Feedback(session, showNotice: false) is not null,
            "flyout still displays operation errors");
    }
    Check(app.ResolveHostDescription("MSFT.ghe.com").Contains("https://api.msft.ghe.com/"), "GHE API mapping in controller");
    await Throws<AppOperationException>(() => app.AddAsync("unregistered.ghe.com", false, null,
        _ => throw new InvalidOperationException("Unregistered host must not return a code."),
        _ => Task.FromResult(true), default));
    Check(handler.OAuthRequests == 0, "unregistered host fails visibly without a network request or github.com fallback");
    await Throws<AppOperationException>(() => app.AddAsync("github.com", false, null,
        _ => { }, _ => Task.FromResult(true), default, "other-registration"));
    Check(handler.OAuthRequests == 0, "github.com cannot use another OAuth registration");

    await Add("github.com", "1");
    await Add("github.com", "2");
    await Add("msft.ghe.com", "3");
    await app.RefreshAsync();
    await Until(() => Volatile.Read(ref view)?.Accounts.Count == 3);
    var config = await Load();
    Check(config.Accounts.Length == 3 && credentials.Values.Count == 3, "three distinct persisted credentials/accounts");
    Check(config.Accounts.Select(a => a.Key).Distinct().Count() == 3, "immutable identity keys");
    Check(config.Accounts.All(a => a.AvatarUrl is not null), "identity avatar URLs persisted for each host");
    Check(config.Accounts.Single(a => a.Key == "msft.ghe.com:3").AvatarUrl ==
        "https://avatars.msft.ghe.com/u/3", "enterprise avatar stays on its own tenant");
    string persistedSettings = await File.ReadAllTextAsync(Path.Combine(root, "config.json"));
    Check(!persistedSettings.Contains("clientId", StringComparison.OrdinalIgnoreCase), "client ID is not configurable in persisted settings");
    Check(Volatile.Read(ref view)!.Total.Contains("$53.25"), "three-account consumption total");
    var summary = Volatile.Read(ref view)!.Accounts.Single(account => account.Key == "github.com:1");
    Check(summary.AvatarUrl == "https://avatars.githubusercontent.com/u/1?v=4",
        "flyout and settings share the account avatar URL");
    Check(GHCPSpendTray.App.UI.UI.AccountPicture(summary, 36) is PersonPictureElement
        { ProfilePicture: "https://avatars.githubusercontent.com/u/1?v=4" },
        "avatar URL reaches the native person picture");
    Check(GHCPSpendTray.App.UI.UI.AccountPicture(summary with { AvatarUrl = null }, 36) is PersonPictureElement
        { ProfilePicture: null, DisplayName: "test-1" },
        "missing avatar uses the native initials fallback");
    Check(summary.ConsumptionUsd == 26.25m && summary.AllocationUsd == 25m && summary.Percent == 105m,
        "account consumption and allocation remain visible without sampled chart data");
    Check(summary.Details.CreditsUsed == 2625m && summary.Details.ObservedConsumptionUsd == 26.25m,
        "advanced account data is structured independently of summary text");
    Check(summary.Details.ResetAtUtc is not null && summary.Details.NextRefreshUtc is not null,
        "advanced billing and scheduling fields retained");
    Check(GHCPSpendTray.App.UI.UI.AccountWarning(summary) is null, "fresh account has no unnecessary status warning");
    var stale = summary with { Freshness = "Stale - last-known observation" };
    Check(GHCPSpendTray.App.UI.UI.AccountWarning(stale)!.Contains("Stale"), "stale account warning remains visible outside advanced details");
    var failed = summary with { Details = summary.Details with { Message = "Current consumption is unsaved." } };
    Check(GHCPSpendTray.App.UI.UI.AccountWarning(failed)!.Contains("unsaved"), "storage failures remain visible outside advanced details");
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
    Check(handler.RefreshRequests == 1, "github.com client ID also used for token refresh");
    credentials.Values["msft.ghe.com:3"] = credentials.Values["msft.ghe.com:3"] with
    {
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
        RefreshToken = "refresh-3"
    };
    await app.RefreshAsync("msft.ghe.com:3");
    Check(handler.RefreshRequests == 2, "enterprise token refreshed with enterprise registration");
    Check(handler.OAuthRequests >= 14 &&
        handler.OAuthClientIds.Count(id => id == "Ov23ox38SoD1bIpzU9zZ") == 3 &&
        handler.OAuthClientIds.All(id => id is "Ov23ctzkXY5CJhfKQo7T" or "Ov23ox38SoD1bIpzU9zZ"),
        "device authorization, polling, and refresh use the correct ID for each host");
    await Add("MSFT.GHE.COM", "3", "msft.ghe.com:3");
    Check((await Load()).Accounts.Length == 3 && credentials.Values.Count == 3,
        "enterprise reconnect preserves account and credential partition");
    Check((await Load()).Accounts.Single(a => a.Key == "msft.ghe.com:3").AvatarUrl ==
        "https://avatars.msft.ghe.com/u/3", "reconnect retains updated avatar");
    await app.SaveAccountAsync("github.com:2", "Work", "15, 120");
    await app.RefreshAsync("github.com:2");
    Check(notifications == 3, "new override below consumption alerts once");
    Check((await Load()).Accounts.Single(a => a.Key == "github.com:2").DisplayName == "Work", "display name persisted");
    await app.RemoveAsync("github.com:2");
    Check(!credentials.Values.ContainsKey("github.com:2") && (await Load()).Accounts.Length == 2, "local removal deletes credential and configuration");
    await app.SaveSettingsAsync(app.Settings with { SpendIncrementUsd = 50 });
    await app.SaveAccountAsync("github.com:1", "Personal", "", 25m);
    Check(app.Settings.SpendIncrementUsd == 50m && app.AccountSettings("github.com:1").SpendIncrementUsd == 25m,
        "global and per-account spend increment settings persist");
    await app.RefreshAsync("github.com:1");
    Check(notifications == 4, "new spend increment below current consumption submits once");
    await app.RefreshAsync("github.com:1");
    Check(notifications == 4, "spend increment notification is deduplicated");
    await Add("team.ghe.com", "5", clientId: "team-registration");
    var customSaved = (await Load()).Accounts.Single(a => a.Key == "team.ghe.com:5");
    Check(customSaved.OAuthClientId == "team-registration" &&
        customSaved.AvatarUrl == "https://avatars.team.ghe.com/u/5",
        "custom registration and host-scoped avatar survive settings round trip");
    await Until(() => Volatile.Read(ref view)?.Accounts.Any(a =>
        a.Key == "team.ghe.com:5" && a.AvatarUrl == customSaved.AvatarUrl) == true);
    Check(app.AccountClientId("team.ghe.com:5") == "team-registration", "reconnect reads saved registration");
    var teamAccount = (await Load()).Accounts.Single(a => a.Key == "team.ghe.com:5");
    var teamTarget = new VaultCredentials(null).Target(teamAccount);
    Check(teamTarget.Contains("/team-registration/", StringComparison.Ordinal), "custom credentials use selected registration");
    await Throws<AppOperationException>(() => app.AddAsync("team.ghe.com", false, "team.ghe.com:5",
        _ => { }, _ => Task.FromResult(true), default, "other-registration"));
    Check(credentials.Values["team.ghe.com:5"].AccessToken == "fixture-5",
        "reconnect with different registration cannot overwrite existing credentials");
    credentials.Values["team.ghe.com:5"] = credentials.Values["team.ghe.com:5"] with
    { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1), RefreshToken = "refresh-5" };
    await app.RefreshAsync("team.ghe.com:5");
    Check(handler.OAuthClientIds.Last() == "team-registration", "custom host refresh uses saved registration");
    await Add("team.ghe.com", "5", "team.ghe.com:5", "team-registration");
    await app.RemoveAsync("team.ghe.com:5");
    Check(!(await Load()).Accounts.Any(a => a.Key == "team.ghe.com:5") &&
        !credentials.Values.ContainsKey("team.ghe.com:5"), "custom account removal clears its credentials");
    string legacyRoot = Path.Combine(root, "legacy");
    var legacyStore = new JsonStore(legacyRoot);
    await legacyStore.SaveSettingsAsync(new AppSettings { Accounts =
    [
        new Account { Host = "team.ghe.com", UserId = "5", Login = "synthetic" },
        new Account { Host = "team.ghe.com", UserId = "6", Login = "synthetic" }
    ] });
    using (var legacyApp = new ApplicationController(legacyRoot, true, http, new MemoryCredentials()))
    {
        await legacyApp.InitializeAsync();
        Check(legacyApp.AccountClientId("team.ghe.com:5") is null,
            "legacy unsupported host remains loadable without guessing a registration");
        await legacyApp.RemoveAsync("team.ghe.com:6");
        Check((await legacyStore.LoadSettingsAsync()).Value.Accounts.Length == 1,
            "legacy unsupported account can be removed without an unknown credential target");
        handler.NextIdentity = "5";
        await legacyApp.AddAsync("team.ghe.com", false, "team.ghe.com:5",
            _ => { }, _ => Task.FromResult(true), default, "team-registration");
        Check((await legacyStore.LoadSettingsAsync()).Value.Accounts.Single().OAuthClientId == "team-registration",
            "legacy unsupported account reconnect saves its explicit host registration");
    }
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
    async Task Add(string host, string id, string? reconnectKey = null, string? clientId = null)
    {
        handler.NextIdentity = id;
        await app.AddAsync(host, false, reconnectKey,
            prompt => Check(prompt.VerificationUri.Host == host.ToLowerInvariant() && prompt.Code == "TEST-CODE", "validated device prompt"),
            identity => Task.FromResult(identity.UserId > 0), default, clientId);
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
            var expectedClient = uri.Host switch
            {
                "github.com" => "Ov23ctzkXY5CJhfKQo7T",
                "msft.ghe.com" => "Ov23ox38SoD1bIpzU9zZ",
                "team.ghe.com" => "team-registration",
                _ => throw new InvalidOperationException("Unexpected OAuth host.")
            };
            if (form["client_id"] != expectedClient || form.ContainsKey("client_secret"))
                throw new InvalidOperationException("OAuth must use its host-specific public ID without a client secret.");
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
            if (id switch { "3" => uri.Host != "msft.ghe.com",
                "5" => uri.Host != "team.ghe.com", _ => uri.Host != "github.com" })
                throw new InvalidOperationException("Device or refresh token crossed host boundary.");
            json = $$"""{"access_token":"fixture-{{id}}","token_type":"bearer","scope":"read:user"}""";
        }
        else
        {
            string id = request.Headers.Authorization!.Parameter!["fixture-".Length..];
            if (id switch { "3" => uri.Host != "api.msft.ghe.com",
                "5" => uri.Host != "api.team.ghe.com", _ => uri.Host != "api.github.com" })
                throw new InvalidOperationException("Credential crossed host boundary.");
            if (uri.AbsolutePath == "/user")
            {
                var avatar = id switch
                {
                    "3" => "https://avatars.msft.ghe.com/u/3",
                    "5" => "https://avatars.team.ghe.com/u/5",
                    _ => $"https://avatars.githubusercontent.com/u/{id}?v=4"
                };
                json = $$"""{"id":{{id}},"login":"test-{{id}}","avatar_url":"{{avatar}}"}""";
            }
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
