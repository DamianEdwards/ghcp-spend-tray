using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.Tests;

internal static class Program
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Account Account = new() { UserId = "42", Login = "fixture-user" };
    private static int _passed, _failed;
    private static readonly string TestRoot = Path.Combine(Directory.GetCurrentDirectory(), "tests", "GHCPSpendTray.Tests",
        "artifacts", "run-" + Guid.NewGuid().ToString("N"));

    public static async Task<int> Main()
    {
        Directory.CreateDirectory(TestRoot);
        try
        {
            await DomainTests();
            await HttpTests();
            await PersistenceTests();
            await AlertTests();
            await SpendIncrementTests();
            await SchedulerTests();
        }
        finally { Directory.Delete(TestRoot, recursive: true); }
        Console.WriteLine($"{_passed} passed; {_failed} failed. No live credentials or network were used.");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task Test(string name, Func<Task> action)
    {
        try { await action(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    private static Task Test(string name, Action action) => Test(name, () => { action(); return Task.CompletedTask; });
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
    private static void True(bool value, string message = "Assertion failed")
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static string Json(decimal credits = 12345m, decimal? entitlement = 100000m,
        bool unlimited = false, bool tokenBased = true, bool hasQuota = true) =>
        """{"quota_reset_date":"2026-10-01","quota_snapshots":{"premium_interactions":{""" +
        "\"credits_used\":" + credits.ToString(CultureInfo.InvariantCulture) +
        (entitlement is { } e ? ",\"entitlement\":" + e.ToString(CultureInfo.InvariantCulture) : "") +
        ",\"unlimited\":" + unlimited.ToString().ToLowerInvariant() +
        ",\"has_quota\":" + hasQuota.ToString().ToLowerInvariant() +
        ",\"token_based_billing\":" + tokenBased.ToString().ToLowerInvariant() +
        ""","timestamp_utc":"2026-09-17T11:59:00Z"}}}""";

    private static UsageSnapshot Sample(decimal credits = 5000m, decimal? entitlement = 10000m,
        DateTimeOffset? at = null, Account? account = null, bool unlimited = false)
    {
        DateTimeOffset when = at ?? Now;
        return new()
        {
            AccountKey = (account ?? Account).Key, FetchedAtUtc = when,
            PeriodId = BillingPeriods.Resolve(when, null), CreditsUsed = credits, Entitlement = entitlement,
            Unlimited = unlimited, ConsumptionUsd = CreditPolicy.ToUsd(credits),
            AllocationUsd = entitlement is { } e ? CreditPolicy.ToUsd(e) : null,
            PercentConsumed = CreditPolicy.Percentage(credits, entitlement, unlimited)
        };
    }

    private static AppSettings Settings(params Account[] accounts) => new() { Accounts = accounts };
    private static JsonStore Store() => new(Path.Combine(TestRoot, Guid.NewGuid().ToString("N")));

    private static async Task DomainTests()
    {
        await Test("defaults exactly hourly and 50/80/100", () =>
        {
            var s = new AppSettings();
            Equal(60, s.PollIntervalMinutes);
            True(s.AlertThresholds.SequenceEqual([50m, 80m, 100m]));
            True(!s.RequestOfflineAccess);
            s.Validate();
        });
        foreach (int value in new[] { 0, 4, 1441 })
            await Test("reject interval " + value, () => Throws<ArgumentException>(() => (new AppSettings { PollIntervalMinutes = value }).Validate()));
        foreach (decimal[] thresholds in new decimal[][] { [0], [-1], [80, 50], [50, 50] })
            await Test("reject invalid thresholds " + string.Join(',', thresholds), () => Throws<ArgumentException>(() => AppSettings.ValidateThresholds(thresholds)));
        await Test("allow above 100 and disabled thresholds", () =>
        {
            AppSettings.ValidateThresholds([50, 100, 200]);
            AppSettings.ValidateThresholds([]);
        });
        await Test("avatar URLs are host-scoped with an existing-account fallback", () =>
        {
            Equal("https://avatars.githubusercontent.com/u/42", AccountAvatar.Resolve(Account));
            Equal("https://avatars.githubusercontent.com/u/42",
                AccountAvatar.Resolve(Account with { AvatarUrl = "https://other.test/u/42" }));
            Equal("https://avatars.githubusercontent.com/u/42?v=4",
                AccountAvatar.Resolve(Account with { AvatarUrl = "https://avatars.githubusercontent.com/u/42?v=4" }));
            Equal<string?>(null, AccountAvatar.Resolve(Account with { Host = "tenant.ghe.com" }));
            Equal("https://tenant.ghe.com/avatars/u/42",
                AccountAvatar.Resolve(Account with { Host = "tenant.ghe.com",
                    AvatarUrl = "https://tenant.ghe.com/avatars/u/42" }));
            Equal("https://avatars.tenant.ghe.com/u/42",
                AccountAvatar.Resolve(Account with { Host = "tenant.ghe.com",
                    AvatarUrl = "https://avatars.tenant.ghe.com/u/42" }));
            Equal<string?>(null, AccountAvatar.Resolve(Account with { Host = "tenant.ghe.com",
                AvatarUrl = "https://avatars.githubusercontent.com/u/42" }));
            Equal<string?>(null, AccountAvatar.Resolve(Account with { Host = "tenant.ghe.com",
                AvatarUrl = "https://avatars.other.ghe.com/u/42" }));
            Equal<string?>(null, AccountAvatar.Resolve(Account with { Host = "git.example.test:8443",
                AvatarUrl = "https://git.example.test/avatars/u/42" }));
            foreach (string bad in new[] { "http://tenant.ghe.com/u/42", "file:///C:/private",
                "https://tenant.ghe.com.evil.test/u/42", "https://user@tenant.ghe.com/u/42",
                "https://tenant.ghe.com/u/42#fragment" })
                Equal<string?>(null, AccountAvatar.Resolve(Account with { Host = "tenant.ghe.com", AvatarUrl = bad }));
        });
        foreach ((string host, string expected, HostKind kind) in new[]
        {
            ("GitHub.Com", "https://api.github.com/copilot_internal/user", HostKind.GitHub),
            ("https://TENANT.ghe.com/", "https://api.tenant.ghe.com/copilot_internal/user", HostKind.EnterpriseCloud),
            ("https://git.company.test:8443", "https://git.company.test:8443/api/v3/copilot_internal/user", HostKind.EnterpriseServer)
        })
            await Test("host mapping " + host, () =>
            {
                ResolvedHost resolved = HostResolver.Resolve(host);
                Equal(expected, resolved.ApiUri("copilot_internal/user").AbsoluteUri);
                Equal(kind, resolved.Kind);
            });
        foreach (string host in new[]
        {
            "http://github.com", "https://a:b@github.com", "github.com/path", "github.com?x=y",
            "github.com#fragment", "github.com?", "github.com#", " github.com", "github.com\\evil",
            "github.com:8443", "tenant.ghe.com:8443", "localhost", "127.0.0.1", "https://[::1]",
            "api.github.com", "api.tenant.ghe.com", "https://github.com./", "https://bad_.example/",
            "https://github.com/foo/../", "https://github.com/%2e/", "https://github.com//"
        })
            await Test("reject host " + host, () => Throws<ArgumentException>(() => HostResolver.Resolve(host)));
        await Test("verification origin checked", () =>
        {
            ResolvedHost host = HostResolver.Resolve("github.com");
            Equal("https://github.com/login/device", host.ValidateVerificationUri("https://github.com/login/device").AbsoluteUri);
            foreach (string uri in new[] { "http://github.com/login/device", "https://evil.test/", "https://github.com:444/", "https://x@github.com/" })
                Throws<ServiceException>(() => host.ValidateVerificationUri(uri));
            Throws<ArgumentException>(() => host.ApiUri("/user"));
            Throws<ArgumentException>(() => host.ApiUri("../user"));
        });
        await Test("host and immutable ID partition accounts", () =>
        {
            Equal(Account.Key, (Account with { Host = "GITHUB.COM", Login = "renamed" }).Key);
            True(Account.Key != (Account with { Host = "tenant.ghe.com" }).Key);
            Throws<ArgumentException>(() => Settings(Account, Account with { Login = "renamed" }).Validate());
            Settings(Account, Account with { UserId = "43" },
                Account with { Host = "tenant.ghe.com", OAuthClientId = "tenant-registration" }).Validate();
            (Account with { Host = "tenant.ghe.com" }).Validate();
            Throws<ServiceException>(() => GitHubOAuth.ResolveClientId("tenant.ghe.com"));
            Throws<ArgumentException>(() => (Account with { Host = "tenant.ghe.com", OAuthClientId = "bad id" }).Validate());
        });
        await Test("strict decimal conversion fixture", () =>
        {
            UsageSnapshot result = CopilotUsageProvider.ParseJson(Json(), Account.Key, Now);
            Equal(123.45m, result.ConsumptionUsd);
            Equal<decimal?>(12.345m, result.PercentConsumed);
            Equal<decimal?>(1000m, result.AllocationUsd);
            Equal("reset:2026-10-01T00:00:00.0000000+00:00", result.PeriodId);
        });
        await Test("retains source precision and above 100", () =>
        {
            UsageSnapshot result = CopilotUsageProvider.ParseJson(Json(12345.678901m, 100m), Account.Key, Now);
            Equal(123.45678901m, result.ConsumptionUsd);
            Equal<decimal?>(12345.678901m, result.PercentConsumed);
        });
        foreach ((decimal? allocation, bool unlimited) in new (decimal?, bool)[] { (0m, false), (null, false), (100m, true) })
            await Test($"N/A allocation {allocation}, unlimited {unlimited}", () =>
                Equal<decimal?>(null, CopilotUsageProvider.ParseJson(Json(0, allocation, unlimited), Account.Key, Now).PercentConsumed));
        foreach (string field in new[] { "\"credits_used\":12345,", "\"unlimited\":false,", "\"has_quota\":true,", "\"token_based_billing\":true," })
            await Test("missing required " + field, () =>
            {
                ServiceException error = Throws<ServiceException>(() => CopilotUsageProvider.ParseJson(Json().Replace(field, ""), Account.Key, Now));
                Equal(AccountStatus.InvalidData, error.Status);
            });
        await Test("non token accounting unsupported", () =>
            Equal(AccountStatus.Unsupported, Throws<ServiceException>(() => CopilotUsageProvider.ParseJson(Json(tokenBased: false), Account.Key, Now)).Status));
        await Test("exhausted quota still preserves observed consumption", () =>
            Equal<decimal?>(105m, CopilotUsageProvider.ParseJson(Json(10500, 10000, hasQuota: false), Account.Key, Now).PercentConsumed));
        foreach (string json in new[]
        {
            "{}", "null", "{", Json(-1), Json(1, -1), Json().Replace("12345", "\"12345\""),
            Json().Replace("2026-10-01", "nonsense"), Json().Replace("2026-10-01", "2026-09-01"),
            Json().Replace("12345", "79228162514264337593543950336"),
            Json(decimal.MaxValue, .0000000000000000000000000001m)
        })
            await Test("invalid schema rejected " + json.Length, () => Throws<ServiceException>(() => CopilotUsageProvider.ParseJson(json, Account.Key, Now)));
        await Test("ignore inconsistent remaining fields", () =>
        {
            string json = Json().Replace("\"credits_used\":12345", "\"credits_used\":12345,\"remaining\":0,\"percent_remaining\":99");
            Equal(123.45m, CopilotUsageProvider.ParseJson(json, Account.Key, Now).ConsumptionUsd);
        });
        await Test("totals are current, partial, and last known", () =>
        {
            var a = new AccountState { Account = Account, Status = AccountStatus.Fresh, Snapshot = Sample() };
            var b = new AccountState { Account = Account with { UserId = "43" }, Status = AccountStatus.NetworkError };
            UsageTotal total = UsageAggregation.Total([a, b], Now, TimeSpan.FromHours(1));
            Equal(50m, total.ConsumptionUsd); True(!total.IsComplete); Equal(1, total.IncludedAccounts);
            total = UsageAggregation.Total([a with { Status = AccountStatus.NetworkError }], Now, TimeSpan.FromHours(1));
            True(total.IsLastKnown); True(!total.IsComplete);
            total = UsageAggregation.Total([a], Now.AddMonths(1), TimeSpan.FromHours(1));
            Equal(0m, total.ConsumptionUsd); Equal(0, total.IncludedAccounts);
            True(!total.IsComplete);
        });
        await Test("reset date takes priority over calendar", () =>
        {
            UsageSnapshot s = Sample() with { ResetAtUtc = Now.AddMonths(1) };
            s = s with { PeriodId = BillingPeriods.Resolve(s.FetchedAtUtc, s.ResetAtUtc) };
            s.Validate();
            True(BillingPeriods.IsCurrent(s, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));
            True(!BillingPeriods.IsCurrent(s, s.ResetAtUtc!.Value));
        });
        await Test("tokens stringify redacted and source generation works", () =>
        {
            var tokens = new TokenSet { AccessToken = "fake-secret", RefreshToken = "fake-refresh" };
            True(!tokens.ToString().Contains("fake-secret"));
            string json = JsonSerializer.Serialize(tokens, CoreJsonContext.Default.TokenSet);
            Equal(tokens, JsonSerializer.Deserialize(json, CoreJsonContext.Default.TokenSet));
        });
        await Test("reject ambiguous timestamps and token header injection", () =>
        {
            Throws<ServiceException>(() => CopilotUsageProvider.ParseJson(Json().Replace("2026-10-01", "10/01/2026"), Account.Key, Now));
            Throws<ServiceException>(() => (new TokenSet { AccessToken = "fake\r\ninjected" }).Validate());
            Throws<ArgumentException>(() => (Account with { UserId = "042" }).Validate());
        });
    }

    private static async Task HttpTests()
    {
        await Test("device scopes conservative; offline access opt in", async () =>
        {
            var handler = new FakeHttp(_ => Response("""{"device_code":"fake-code","user_code":"AAAA","verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}"""));
            using var client = new HttpClient(handler);
            var flow = new DeviceFlowClient(client, new Clock(Now));
            DeviceAuthorization device = await flow.BeginAsync(HostResolver.Resolve("github.com"), "owned-client");
            True(handler.Requests[0].Body.Contains("scope=read%3Auser"));
            True(!handler.Requests[0].Body.Contains("offline_access"));
            True(!handler.Requests[0].Body.Contains("client_secret"));
            Equal(Now.AddSeconds(900), device.ExpiresAtUtc);
            await flow.BeginAsync(HostResolver.Resolve("github.com"), "owned-client", true);
            True(handler.Requests[1].Body.Contains("offline_access"));
        });
        await Test("device malicious verification URL rejected", async () =>
        {
            using var client = new HttpClient(new FakeHttp(_ => Response("""{"device_code":"x","user_code":"y","verification_uri":"https://evil.test/","expires_in":900}""")));
            await ThrowsAsync<ServiceException>(() => new DeviceFlowClient(client).BeginAsync(HostResolver.Resolve("github.com"), "owned-client"));
        });
        await Test("device pending slow_down success respects intervals", async () =>
        {
            var clock = new Clock(Now);
            int requests = 0;
            using var client = new HttpClient(new FakeHttp(_ => Response(Interlocked.Increment(ref requests) switch
            {
                1 => """{"error":"authorization_pending"}""",
                2 => """{"error":"slow_down"}""",
                _ => """{"access_token":"fixture-access","token_type":"bearer"}"""
            })));
            var flow = new DeviceFlowClient(client, clock);
            Task<TokenSet> polling = flow.PollAsync(HostResolver.Resolve("github.com"), "owned-client",
                new("fixture-device", "CODE", new Uri("https://github.com/login/device"), Now.AddMinutes(10), 5));
            await Until(() => clock.TimerCount > 0); clock.Advance(TimeSpan.FromSeconds(5));
            await Until(() => requests == 1 && clock.TimerCount > 0); clock.Advance(TimeSpan.FromSeconds(5));
            await Until(() => requests == 2 && clock.TimerCount > 0); clock.Advance(TimeSpan.FromSeconds(9));
            Equal(2, requests); clock.Advance(TimeSpan.FromSeconds(1));
            Equal("fixture-access", (await polling.WaitAsync(TimeSpan.FromSeconds(5))).AccessToken);
        });
        await Test("device expiry and cancellation", async () =>
        {
            var clock = new Clock(Now);
            var handler = new FakeHttp(_ => throw new InvalidOperationException("Should not send"));
            using var client = new HttpClient(handler);
            var flow = new DeviceFlowClient(client, clock);
            var device = new DeviceAuthorization("fake", "CODE", new Uri("https://github.com/login/device"), Now.AddSeconds(2), 5);
            Task<TokenSet> expired = flow.PollAsync(HostResolver.Resolve("github.com"), "owned-client", device);
            await Until(() => clock.TimerCount > 0); clock.Advance(TimeSpan.FromSeconds(2));
            await ThrowsAsync<ServiceException>(() => expired);
            using var cancel = new CancellationTokenSource();
            Task<TokenSet> cancelled = flow.PollAsync(HostResolver.Resolve("github.com"), "owned-client",
                device with { ExpiresAtUtc = Now.AddMinutes(10) }, cancel.Token);
            cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => cancelled);
            Equal(0, handler.Requests.Count);
        });
        foreach ((HttpStatusCode code, AccountStatus expected) in new[]
        {
            (HttpStatusCode.Unauthorized, AccountStatus.SignInRequired),
            (HttpStatusCode.Forbidden, AccountStatus.Forbidden),
            (HttpStatusCode.NotFound, AccountStatus.Unsupported),
            (HttpStatusCode.Redirect, AccountStatus.Forbidden),
            (HttpStatusCode.InternalServerError, AccountStatus.NetworkError)
        })
            await Test("HTTP state " + code, async () =>
            {
                using var client = new HttpClient(new FakeHttp(_ => Response("do not echo fake-secret", code)));
                var flow = new DeviceFlowClient(client, new Clock(Now));
                var provider = new CopilotUsageProvider(client, new(new MemoryCredentials(), flow), new Clock(Now));
                ServiceException error = await ThrowsAsync<ServiceException>(() => provider.FetchWithTokenAsync(Account, new() { AccessToken = "fixture" }));
                Equal(expected, error.Status); True(!error.Message.Contains("fake-secret"));
            });
        foreach (HttpStatusCode status in new[] { HttpStatusCode.NotFound, HttpStatusCode.NotImplemented })
        {
            await Test("enterprise device failure identifies authorization stage " + status, async () =>
            {
                var handler = new FakeHttp(_ => Response("private-server-body fixture-secret", status));
                using var client = new HttpClient(handler);
                var error = await ThrowsAsync<ServiceException>(() => new DeviceFlowClient(client, new Clock(Now))
                    .BeginAsync(HostResolver.Resolve("tenant.ghe.com"), GitHubOAuth.ClientId));
                Equal(AccountStatus.Unsupported, error.Status);
                True(error.Message.Contains("POST /login/device/code"));
                True(error.Message.Contains("registered and approved"));
                True(error.Message.Contains("Copilot consumption has not been checked"));
                True(error.Message.Contains($"HTTP {(int)status}"));
                True(!error.Message.Contains("fixture-secret"));
                Equal(1, handler.Requests.Count);
                Equal("https://tenant.ghe.com/login/device/code", handler.Requests[0].Uri.AbsoluteUri);
            });
            await Test("unavailable identity is not a quota failure " + status, async () =>
            {
                using var client = new HttpClient(new FakeHttp(_ => Response("private-server-body", status)));
                var error = await ThrowsAsync<ServiceException>(() => new DeviceFlowClient(client, new Clock(Now))
                    .GetIdentityAsync(HostResolver.Resolve("tenant.ghe.com"), new() { AccessToken = "fixture" }));
                True(error.Message.Contains("GET /user"));
                True(error.Message.Contains("Copilot consumption has not been checked"));
                True(!error.Message.Contains("private-server-body"));
            });
            await Test("unavailable quota identifies consumption endpoint " + status, async () =>
            {
                using var client = new HttpClient(new FakeHttp(_ => Response("private-server-body", status)));
                var flow = new DeviceFlowClient(client, new Clock(Now));
                var provider = new CopilotUsageProvider(client, new(new MemoryCredentials(), flow), new Clock(Now));
                var error = await ThrowsAsync<ServiceException>(() => provider.FetchWithTokenAsync(Account, new() { AccessToken = "fixture" }));
                True(error.Message.Contains("GET /copilot_internal/user"));
                True(!error.Message.Contains("consumption has not been checked"));
                True(!error.Message.Contains("private-server-body"));
            });
            await Test("unavailable refresh identifies token stage " + status, async () =>
            {
                using var client = new HttpClient(new FakeHttp(_ => Response("private-server-body", status)));
                var error = await ThrowsAsync<ServiceException>(() => new DeviceFlowClient(client, new Clock(Now))
                    .RefreshAsync(HostResolver.Resolve("tenant.ghe.com"), GitHubOAuth.ClientId,
                        new() { AccessToken = "fixture", RefreshToken = "fixture-refresh" }));
                True(error.Message.Contains("OAuth token refresh"));
                True(error.Message.Contains("POST /login/oauth/access_token"));
                True(!error.Message.Contains("private-server-body"));
            });
            await Test("unavailable token exchange identifies polling stage " + status, async () =>
            {
                var clock = new Clock(Now);
                using var client = new HttpClient(new FakeHttp(_ => Response("private-server-body", status)));
                var polling = new DeviceFlowClient(client, clock).PollAsync(HostResolver.Resolve("tenant.ghe.com"),
                    GitHubOAuth.ClientId, new("fixture-device", "CODE", new Uri("https://tenant.ghe.com/login/device"),
                        Now.AddMinutes(10), 5));
                await Until(() => clock.TimerCount > 0);
                clock.Advance(TimeSpan.FromSeconds(5));
                var error = await ThrowsAsync<ServiceException>(() => polling.WaitAsync(TimeSpan.FromSeconds(5)));
                True(error.Message.Contains("OAuth token exchange"));
                True(error.Message.Contains("Copilot consumption has not been checked"));
                True(!error.Message.Contains("private-server-body"));
            });
        }
        await Test("rate limit reset and Retry-After honored", async () =>
        {
            using var client = new HttpClient(new FakeHttp(_ =>
            {
                var r = Response("{}", HttpStatusCode.Forbidden);
                r.Headers.Add("X-RateLimit-Remaining", "0");
                r.Headers.Add("X-RateLimit-Reset", Now.AddMinutes(10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                r.Headers.RetryAfter = new(TimeSpan.FromMinutes(2));
                return r;
            }));
            var flow = new DeviceFlowClient(client, new Clock(Now));
            ServiceException error = await ThrowsAsync<ServiceException>(() => flow.GetIdentityAsync(HostResolver.Resolve("github.com"), new() { AccessToken = "fixture" }));
            Equal(AccountStatus.RateLimited, error.Status); Equal<DateTimeOffset?>(Now.AddMinutes(10), error.RetryAtUtc);
        });
        await Test("SSO diagnostic stays useful and redacted", async () =>
        {
            using var client = new HttpClient(new FakeHttp(_ =>
            {
                var r = Response("secret", HttpStatusCode.Forbidden);
                r.Headers.Add("X-GitHub-SSO", "required; url=https://github.com/?secret=anything");
                return r;
            }));
            ServiceException error = await ThrowsAsync<ServiceException>(() => new DeviceFlowClient(client).GetIdentityAsync(HostResolver.Resolve("github.com"), new() { AccessToken = "fixture" }));
            True(error.Message.Contains("SSO")); True(!error.Message.Contains("secret"));
        });
        await Test("host credentials isolated without shared headers", async () =>
        {
            var handler = new FakeHttp(_ => Response(Json()));
            using var client = new HttpClient(handler);
            var provider = new CopilotUsageProvider(client, new(new MemoryCredentials(), new(client)), new Clock(Now));
            await Task.WhenAll(provider.FetchWithTokenAsync(Account, new() { AccessToken = "fixture-A" }),
                provider.FetchWithTokenAsync(Account with { Host = "tenant.ghe.com", OAuthClientId = "tenant-registration" },
                    new() { AccessToken = "fixture-B" }));
            True(handler.Requests.Any(r => r.Uri.Host == "api.github.com" && r.Authorization == "Bearer fixture-A"));
            True(handler.Requests.Any(r => r.Uri.Host == "api.tenant.ghe.com" && r.Authorization == "Bearer fixture-B"));
            Equal<System.Net.Http.Headers.AuthenticationHeaderValue?>(null, client.DefaultRequestHeaders.Authorization);
        });
        await Test("refresh serialized and rotated tokens durable before return", async () =>
        {
            var credentials = new MemoryCredentials { Tokens = new() { AccessToken = "expired", RefreshToken = "old-refresh", ExpiresAtUtc = Now } };
            var handler = new FakeHttp(_ => Response("""{"access_token":"rotated","refresh_token":"new-refresh","token_type":"bearer","expires_in":3600,"refresh_token_expires_in":7200}"""));
            using var client = new HttpClient(handler);
            var manager = new TokenManager(credentials, new(client, new Clock(Now)), new Clock(Now));
            TokenSet[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => manager.GetAsync(Account)));
            Equal(1, handler.Requests.Count); True(results.All(t => t.AccessToken == "rotated"));
            Equal("new-refresh", credentials.Tokens!.RefreshToken); Equal(1, credentials.Writes);
            True(!handler.Requests[0].Body.Contains("client_secret"));
        });
        await Test("refresh storage failure prevents token use", async () =>
        {
            var credentials = new MemoryCredentials { FailWrite = true, Tokens = new() { AccessToken = "expired", RefreshToken = "refresh", ExpiresAtUtc = Now } };
            using var client = new HttpClient(new FakeHttp(_ => Response("""{"access_token":"new","token_type":"bearer"}""")));
            var manager = new TokenManager(credentials, new(client, new Clock(Now)), new Clock(Now));
            Equal(AccountStatus.StorageError, (await ThrowsAsync<ServiceException>(() => manager.GetAsync(Account))).Status);
        });
        await Test("non-expiring credentials unchanged; expired without refresh requires sign in", async () =>
        {
            using var client = new HttpClient(new FakeHttp(_ => throw new InvalidOperationException()));
            var credentials = new MemoryCredentials { Tokens = new() { AccessToken = "fixture" } };
            var manager = new TokenManager(credentials, new(client), new Clock(Now));
            Equal("fixture", (await manager.GetAsync(Account)).AccessToken);
            credentials.Tokens = credentials.Tokens with { ExpiresAtUtc = Now };
            Equal(AccountStatus.SignInRequired, (await ThrowsAsync<ServiceException>(() => manager.GetAsync(Account))).Status);
        });
        await Test("401 makes one refresh then retries with rotated token", async () =>
        {
            var credentials = new MemoryCredentials { Tokens = new() { AccessToken = "old", RefreshToken = "refresh" } };
            var handler = new FakeHttp(r => r.Uri.AbsolutePath == "/login/oauth/access_token"
                ? Response("""{"access_token":"new","refresh_token":"rotated","token_type":"bearer"}""")
                : r.Authorization == "Bearer old" ? Response("{}", HttpStatusCode.Unauthorized) : Response(Json()));
            using var client = new HttpClient(handler);
            var provider = new CopilotUsageProvider(client, new(credentials, new(client, new Clock(Now)), new Clock(Now)), new Clock(Now));
            Equal(123.45m, (await provider.FetchAsync(Account)).ConsumptionUsd);
            Equal(3, handler.Requests.Count); Equal(1, credentials.Writes);
        });
        await Test("identity uses immutable numeric ID and correct GHES path", async () =>
        {
            var handler = new FakeHttp(_ => Response("""{"id":987654321,"login":"verified-login","avatar_url":"https://git.example.test:8443/avatars/u/987654321"}"""));
            using var client = new HttpClient(handler);
            GitHubIdentity identity = await new DeviceFlowClient(client).GetIdentityAsync(HostResolver.Resolve("git.example.test:8443"), new() { AccessToken = "fixture" });
            Equal("987654321", identity.UserId);
            Equal("https://git.example.test:8443/avatars/u/987654321", identity.AvatarUrl);
            Equal("/api/v3/user", handler.Requests[0].Uri.AbsolutePath);
        });
        await Test("unsafe or missing avatar does not block identity", async () =>
        {
            foreach (string json in new[]
            {
                """{"id":42,"login":"fixture-user"}""",
                """{"id":42,"login":"fixture-user","avatar_url":"https://other.test/collect"}"""
            })
            {
                using var client = new HttpClient(new FakeHttp(_ => Response(json)));
                var identity = await new DeviceFlowClient(client).GetIdentityAsync(HostResolver.Resolve("github.com"),
                    new() { AccessToken = "fixture" });
                Equal("42", identity.UserId);
                Equal<string?>(null, identity.AvatarUrl);
            }
        });
        await Test("oversized and malformed HTTP body rejected", async () =>
        {
            using var client = new HttpClient(new FakeHttp(_ => Response(new string('x', 1024 * 1024 + 1))));
            Equal(AccountStatus.InvalidData, (await ThrowsAsync<ServiceException>(() => new DeviceFlowClient(client).GetIdentityAsync(HostResolver.Resolve("github.com"), new() { AccessToken = "fixture" }))).Status);
        });
        foreach (string code in new[] { "access_denied", "incorrect_client_credentials", "device_flow_disabled", "unrecognized-private-secret" })
            await Test("OAuth errors redacted " + code.Split('-')[0], async () =>
            {
                using var client = new HttpClient(new FakeHttp(_ => Response("{\"error\":\"" + code + "\"}")));
                ServiceException error = await ThrowsAsync<ServiceException>(() =>
                    new DeviceFlowClient(client).BeginAsync(HostResolver.Resolve("github.com"), "owned-client"));
                Equal(AccountStatus.SignInRequired, error.Status);
                True(!error.Message.Contains("private-secret"));
            });
        await Test("repeated 401 terminates after one refresh", async () =>
        {
            var credentials = new MemoryCredentials { Tokens = new() { AccessToken = "old", RefreshToken = "refresh" } };
            var handler = new FakeHttp(r => r.Uri.AbsolutePath == "/login/oauth/access_token"
                ? Response("""{"access_token":"new","token_type":"bearer"}""") : Response("{}", HttpStatusCode.Unauthorized));
            using var client = new HttpClient(handler);
            var provider = new CopilotUsageProvider(client, new(credentials, new(client)));
            Equal(AccountStatus.SignInRequired, (await ThrowsAsync<ServiceException>(() => provider.FetchAsync(Account))).Status);
            Equal(3, handler.Requests.Count);
        });
        await Test("HTTP400 OAuth errors retain actionable safe meaning", async () =>
        {
            using var client = new HttpClient(new FakeHttp(_ => Response("""{"error":"device_flow_disabled","error_description":"fake-secret"}""", HttpStatusCode.BadRequest)));
            ServiceException error = await ThrowsAsync<ServiceException>(() =>
                new DeviceFlowClient(client).BeginAsync(HostResolver.Resolve("github.com"), "owned-client"));
            Equal(AccountStatus.SignInRequired, error.Status);
            True(error.Message.Contains("disabled")); True(!error.Message.Contains("fake-secret"));
        });
        await Test("HTTP cancellation reaches fake transport without sending", async () =>
        {
            var handler = new FakeHttp(_ => throw new InvalidOperationException());
            using var client = new HttpClient(handler);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => new DeviceFlowClient(client).GetIdentityAsync(
                HostResolver.Resolve("github.com"), new() { AccessToken = "fixture" }, cancel.Token));
            Equal(0, handler.Requests.Count);
        });
    }

    private static async Task PersistenceTests()
    {
        await Test("settings atomic save load and recovery copy", async () =>
        {
            JsonStore store = Store();
            Equal(60, (await store.LoadSettingsAsync()).Value.PollIntervalMinutes);
            await store.SaveSettingsAsync(Settings(Account));
            await store.SaveSettingsAsync(Settings(Account) with { PollIntervalMinutes = 30 });
            Equal(30, (await store.LoadSettingsAsync()).Value.PollIntervalMinutes);
            await File.WriteAllTextAsync(Path.Combine(store.RootPath, "config.json"), "{broken");
            StoreLoadResult<AppSettings> recovered = await store.LoadSettingsAsync();
            Equal(60, recovered.Value.PollIntervalMinutes); Equal(1, recovered.Diagnostics.Length);
            True(File.Exists(Path.Combine(store.RootPath, "config.json.corrupt")));
            Equal(60, (await store.LoadSettingsAsync()).Value.PollIntervalMinutes);
        });
        await Test("avatar URL survives settings restart and older settings load", async () =>
        {
            JsonStore store = Store();
            var withAvatar = Account with { AvatarUrl = "https://avatars.githubusercontent.com/u/42?v=4" };
            await store.SaveSettingsAsync(Settings(withAvatar));
            Equal(withAvatar.AvatarUrl, (await new JsonStore(store.RootPath).LoadSettingsAsync()).Value.Accounts[0].AvatarUrl);
            await store.SaveSettingsAsync(Settings(Account));
            Equal<string?>(null, (await store.LoadSettingsAsync()).Value.Accounts[0].AvatarUrl);
        });
        await Test("old registrations and custom account survive settings round trip", async () =>
        {
            JsonStore store = Store();
            await File.WriteAllTextAsync(Path.Combine(store.RootPath, "config.json"),
                """{"version":1,"accounts":[{"host":"github.com","userId":"42","login":"synthetic"},{"host":"msft.ghe.com","userId":"43","login":"synthetic"}]}""");
            AppSettings old = (await store.LoadSettingsAsync()).Value;
            Equal(GitHubOAuth.ClientId, GitHubOAuth.ResolveClientId(old.Accounts[0].Host, old.Accounts[0].OAuthClientId));
            Equal(GitHubOAuth.MicrosoftEnterpriseClientId,
                GitHubOAuth.ResolveClientId(old.Accounts[1].Host, old.Accounts[1].OAuthClientId));
            await store.SaveSettingsAsync(old with { Accounts = old.Accounts.Append(
                Account with { Host = "tenant.ghe.com", OAuthClientId = "tenant-registration" }).ToArray() });
            AppSettings reloaded = (await store.LoadSettingsAsync()).Value;
            Equal("tenant-registration", reloaded.Accounts[2].OAuthClientId);
            Equal<string?>(null, reloaded.Accounts[0].OAuthClientId);
            (old with { Accounts = [Account with { Host = "legacy.ghe.com" }] }).Validate();
        });
        await Test("both corrupt settings copies fail rather than reset", async () =>
        {
            JsonStore store = Store(); await store.SaveSettingsAsync(Settings(Account));
            await File.WriteAllTextAsync(Path.Combine(store.RootPath, "config.json"), "null");
            await File.WriteAllTextAsync(Path.Combine(store.RootPath, "config.json.bak"), "{}no");
            await ThrowsAsync<InvalidDataException>(() => store.LoadSettingsAsync());
        });
        await Test("future schema and invalid values recover from valid backup", async () =>
        {
            JsonStore store = Store(); await store.SaveSettingsAsync(Settings(Account));
            await File.WriteAllTextAsync(Path.Combine(store.RootPath, "config.json"), """{"version":2,"pollIntervalMinutes":1}""");
            Equal(1, (await store.LoadSettingsAsync()).Diagnostics.Length);
        });
        await Test("snapshot history per month and restart", async () =>
        {
            JsonStore store = Store();
            await store.AppendHistoryAsync(Sample());
            await store.AppendHistoryAsync(Sample(6000, at: Now.AddHours(1)));
            await store.AppendHistoryAsync(Sample(7000, at: Now.AddMonths(1)));
            var restart = new JsonStore(store.RootPath);
            var read = await restart.LoadHistoryAsync(Account.Key, Now.AddDays(-1));
            Equal(3, read.Value.Length); Equal(0, read.Diagnostics.Length);
            Equal(2, Directory.GetFiles(store.HistoryDirectory(Account.Key), "*.jsonl").Length);
            Equal(0, (await restart.LoadHistoryAsync((Account with { UserId = "43" }).Key, Now.AddDays(-1))).Value.Length);
        });
        await Test("truncated tail quarantined before append", async () =>
        {
            JsonStore store = Store(); await store.AppendHistoryAsync(Sample());
            string path = Directory.GetFiles(store.HistoryDirectory(Account.Key), "*.jsonl")[0];
            await File.AppendAllTextAsync(path, """{"creditsUsed":""");
            var damaged = await store.LoadHistoryAsync(Account.Key, Now.AddDays(-1));
            Equal(1, damaged.Value.Length); True(damaged.Diagnostics[0].Contains("truncated"));
            await store.AppendHistoryAsync(Sample(6000, at: Now.AddHours(1)));
            Equal(2, (await store.LoadHistoryAsync(Account.Key, Now.AddDays(-1))).Value.Length);
            True(File.Exists(path + ".truncated"));
        });
        await Test("middle corruption surfaced and valid records retained", async () =>
        {
            JsonStore store = Store(); await store.AppendHistoryAsync(Sample());
            string path = Directory.GetFiles(store.HistoryDirectory(Account.Key), "*.jsonl")[0];
            await File.AppendAllTextAsync(path, "{bad}\n");
            await store.AppendHistoryAsync(Sample(6000, at: Now.AddHours(1)));
            var read = await store.LoadHistoryAsync(Account.Key, Now.AddDays(-1));
            Equal(2, read.Value.Length); True(read.Diagnostics[0].Contains("line 2"));
        });
        await Test("valid record missing newline is preserved", async () =>
        {
            JsonStore store = Store(); await store.AppendHistoryAsync(Sample());
            string path = Directory.GetFiles(store.HistoryDirectory(Account.Key), "*.jsonl")[0];
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).TrimEnd('\n'));
            await store.AppendHistoryAsync(Sample(6000, at: Now.AddHours(1)));
            var read = await store.LoadHistoryAsync(Account.Key, Now.AddDays(-1));
            Equal(2, read.Value.Length); Equal(0, read.Diagnostics.Length);
        });
        await Test("retention removes old months and compacts boundary", async () =>
        {
            JsonStore store = Store();
            await store.AppendHistoryAsync(Sample(at: Now.AddDays(-120)));
            await store.AppendHistoryAsync(Sample(at: Now.AddDays(-91)));
            await store.AppendHistoryAsync(Sample(at: Now.AddDays(-89)));
            await store.AppendHistoryAsync(Sample());
            await store.MaintainHistoryAsync(Now);
            var read = await store.LoadHistoryAsync(Account.Key, Now.AddDays(-200));
            Equal(2, read.Value.Length); True(read.Value.All(s => s.FetchedAtUtc >= Now.AddDays(-90)));
        });
        await Test("concurrent appends do not interleave", async () =>
        {
            JsonStore store = Store();
            await Task.WhenAll(Enumerable.Range(0, 30).Select(i => store.AppendHistoryAsync(Sample(i, at: Now.AddSeconds(i)))));
            var read = await store.LoadHistoryAsync(Account.Key, Now.AddDays(-1));
            Equal(30, read.Value.Length); Equal(0, read.Diagnostics.Length);
        });
        await Test("delete only selected account history", async () =>
        {
            JsonStore store = Store(); Account other = Account with { UserId = "43" };
            await store.AppendHistoryAsync(Sample()); await store.AppendHistoryAsync(Sample(account: other));
            await store.DeleteAccountHistoryAsync(Account.Key);
            Equal(0, (await store.LoadHistoryAsync(Account.Key, Now.AddDays(-1))).Value.Length);
            Equal(1, (await store.LoadHistoryAsync(other.Key, Now.AddDays(-1))).Value.Length);
        });
        await Test("missing primary restored from backup", async () =>
        {
            JsonStore store = Store(); await store.SaveSettingsAsync(Settings(Account));
            File.Delete(Path.Combine(store.RootPath, "config.json"));
            var read = await store.LoadSettingsAsync();
            Equal(1, read.Value.Accounts.Length); Equal(1, read.Diagnostics.Length);
        });
        await Test("missing schema version and null accounts recover visibly", async () =>
        {
            JsonStore store = Store(); await store.SaveSettingsAsync(Settings(Account));
            foreach (string invalid in new[] { "{}", """{"version":1,"accounts":[null]}""" })
            {
                await File.WriteAllTextAsync(Path.Combine(store.RootPath, "config.json"), invalid);
                Equal(1, (await store.LoadSettingsAsync()).Diagnostics.Length);
            }
        });
        await Test("history required zero-valued fields cannot disappear", async () =>
        {
            JsonStore store = Store(); await store.AppendHistoryAsync(Sample(0));
            string path = Directory.GetFiles(store.HistoryDirectory(Account.Key), "*.jsonl")[0];
            string content = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, content.Replace("\"creditsUsed\":0,", ""));
            var read = await store.LoadHistoryAsync(Account.Key, Now.AddDays(-1));
            Equal(0, read.Value.Length); Equal(1, read.Diagnostics.Length);
        });
        await Test("invalid UTF8 surfaced as corruption", async () =>
        {
            JsonStore store = Store(); await store.AppendHistoryAsync(Sample());
            string path = Directory.GetFiles(store.HistoryDirectory(Account.Key), "*.jsonl")[0];
            await using (var stream = new FileStream(path, FileMode.Append))
                await stream.WriteAsync(new byte[] { 0xFF, 0xFE, 0xFF, 10 });
            True((await store.LoadHistoryAsync(Account.Key, Now.AddDays(-1))).Diagnostics.Any(d => d.Contains("UTF-8")));
        });
        await Test("diagnostic logs are bounded and identity redacted", async () =>
        {
            JsonStore store = Store();
            string directory = Path.Combine(store.RootPath, "logs"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "diagnostics.log");
            await File.WriteAllTextAsync(path, new string('x', 128 * 1024));
            await store.RecordDiagnosticAsync(DiagnosticCode.NetworkFailure, Now, "fake-sensitive-identity");
            True(File.Exists(path + ".1")); True(new FileInfo(path).Length < 200);
            True(!(await File.ReadAllTextAsync(path)).Contains("fake-sensitive-identity"));
        });
    }

    private static async Task AlertTests()
    {
        await Test("jump to 105 coalesces all reached thresholds and persists", async () =>
        {
            JsonStore store = Store(); var sink = new Sink();
            var alerts = new AlertService(store, sink);
            True(!await alerts.EvaluateAsync(Account, Sample(4500), Settings(Account)));
            True(await alerts.EvaluateAsync(Account, Sample(10500, at: Now.AddMinutes(1)), Settings(Account)));
            Equal(1, sink.Alerts.Count); Equal(100m, sink.Alerts[0].HighestThreshold);
            True(sink.Alerts[0].ReachedThresholds.SequenceEqual([50m, 80m, 100m]));
            var restart = new AlertService(new JsonStore(store.RootPath), sink);
            True(!await restart.EvaluateAsync(Account, Sample(10500, at: Now.AddMinutes(2)), Settings(Account)));
            Equal(1, sink.Alerts.Count);
        });
        await Test("failed submission not marked durable and retries", async () =>
        {
            JsonStore store = Store(); var sink = new Sink { Accept = false };
            var alerts = new AlertService(store, sink);
            True(!await alerts.EvaluateAsync(Account, Sample(), Settings(Account)));
            Equal(0, (await store.LoadAlertLedgerAsync()).Value.Accounts.Count);
            sink.Accept = true;
            True(!await alerts.EvaluateAsync(Account, Sample(), Settings(Account)));
            Equal(1, sink.Alerts.Count);
            True(await alerts.EvaluateAsync(Account, Sample(at: Now.AddMinutes(1)), Settings(Account)));
            Equal(2, sink.Alerts.Count);
        });
        await Test("decrease allocation change and restart never rearm old thresholds", async () =>
        {
            JsonStore store = Store(); var sink = new Sink(); var alerts = new AlertService(store, sink);
            await alerts.EvaluateAsync(Account, Sample(8000), Settings(Account));
            await alerts.EvaluateAsync(Account, Sample(4000, at: Now.AddMinutes(1)), Settings(Account));
            await alerts.EvaluateAsync(Account, Sample(4000, 5000, Now.AddMinutes(2)), Settings(Account));
            Equal(1, sink.Alerts.Count);
            await alerts.EvaluateAsync(Account, Sample(5500, 5000, Now.AddMinutes(3)), Settings(Account));
            Equal(2, sink.Alerts.Count); Equal(100m, sink.Alerts[1].HighestThreshold);
        });
        await Test("new period rearms and old observation cannot roll it back", async () =>
        {
            JsonStore store = Store(); var sink = new Sink(); var alerts = new AlertService(store, sink);
            await alerts.EvaluateAsync(Account, Sample(), Settings(Account));
            await alerts.EvaluateAsync(Account, Sample(at: Now.AddMonths(1)), Settings(Account));
            await alerts.EvaluateAsync(Account, Sample(9000), Settings(Account));
            Equal(2, sink.Alerts.Count);
        });
        await Test("new threshold below existing usage alerts on next sample", async () =>
        {
            JsonStore store = Store(); var sink = new Sink(); var alerts = new AlertService(store, sink);
            await alerts.EvaluateAsync(Account, Sample(8000), Settings(Account));
            await alerts.EvaluateAsync(Account, Sample(8000, at: Now.AddMinutes(1)),
                Settings(Account) with { AlertThresholds = [50, 75, 80, 100] });
            Equal(2, sink.Alerts.Count); Equal(75m, sink.Alerts[1].HighestThreshold);
        });
        await Test("unknown unlimited disabled and account override", async () =>
        {
            JsonStore store = Store(); var sink = new Sink(); var alerts = new AlertService(store, sink);
            await alerts.EvaluateAsync(Account, Sample(unlimited: true), Settings(Account));
            await alerts.EvaluateAsync(Account, Sample(entitlement: null), Settings(Account));
            await alerts.EvaluateAsync(Account, Sample(), Settings(Account) with { NotificationsEnabled = false });
            Equal(0, sink.Alerts.Count);
            Account overrideAccount = Account with { ThresholdOverrides = [25m] };
            await alerts.EvaluateAsync(overrideAccount, Sample(2500), Settings(overrideAccount));
            Equal(25m, sink.Alerts[0].HighestThreshold);
        });
        await Test("concurrent alert evaluations produce one submission", async () =>
        {
            JsonStore store = Store(); var sink = new Sink(); var alerts = new AlertService(store, sink);
            await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => alerts.EvaluateAsync(Account, Sample(), Settings(Account))));
            Equal(1, sink.Alerts.Count);
        });
        await Test("ledger disk failure retries persistence without resubmission", async () =>
        {
            JsonStore store = Store(); var sink = new Sink(); var alerts = new AlertService(store, sink);
            Directory.CreateDirectory(Path.Combine(store.RootPath, "state.json.new"));
            await ThrowsAsync<UnauthorizedAccessException>(() => alerts.EvaluateAsync(Account, Sample(), Settings(Account)));
            Directory.Delete(Path.Combine(store.RootPath, "state.json.new"));
            True(!await alerts.EvaluateAsync(Account, Sample(), Settings(Account)));
            Equal(1, sink.Alerts.Count);
            Equal(1, (await store.LoadAlertLedgerAsync()).Value.Accounts.Count);
        });
    }

    private static async Task SpendIncrementTests()
    {
        await Test("spend increment defaults off and validates decimal cents", () =>
        {
            Equal<decimal?>(null, new AppSettings().SpendIncrementUsd);
            foreach (var amount in new[] { -1m, .001m, 50.001m })
                Throws<ArgumentException>(() => (Settings(Account) with { SpendIncrementUsd = amount }).Validate());
            foreach (var amount in new[] { 0m, .01m, 12.50m, 50m })
                (Settings(Account) with { SpendIncrementUsd = amount }).Validate();
            Throws<ArgumentException>(() => (Account with { SpendIncrementUsd = -10 }).Validate());
        });
        await Test("spend alerts trigger at exact boundaries and coalesce jumps", async () =>
        {
            var store = Store(); var sink = new Sink(); var alerts = new AlertService(store, sink);
            var settings = Settings(Account) with { AlertThresholds = [], SpendIncrementUsd = 50 };
            True(!await alerts.EvaluateAsync(Account, Sample(4999), settings));
            True(await alerts.EvaluateAsync(Account, Sample(5000, at: Now.AddMinutes(1)), settings));
            Equal<decimal?>(50m, sink.Alerts[0].SpendMilestoneUsd);
            True(await alerts.EvaluateAsync(Account, Sample(17499, at: Now.AddMinutes(2)), settings));
            Equal(2, sink.Alerts.Count); Equal<decimal?>(150m, sink.Alerts[1].SpendMilestoneUsd);
            Equal(150m, (await store.LoadAlertLedgerAsync()).Value.Accounts[Account.Key].SubmittedSpendUsd);
        });
        await Test("spend dedup survives restart corrections and resets per period", async () =>
        {
            var store = Store(); var sink = new Sink();
            var settings = Settings(Account) with { AlertThresholds = [], SpendIncrementUsd = 50 };
            var alerts = new AlertService(store, sink);
            await alerts.EvaluateAsync(Account, Sample(16000), settings);
            var restarted = new AlertService(new JsonStore(store.RootPath), sink);
            True(!await restarted.EvaluateAsync(Account, Sample(4000, at: Now.AddMinutes(1)), settings));
            True(!await restarted.EvaluateAsync(Account, Sample(16000, at: Now.AddMinutes(2)), settings));
            True(await restarted.EvaluateAsync(Account, Sample(20000, at: Now.AddMinutes(3)), settings));
            True(await restarted.EvaluateAsync(Account, Sample(5000, at: Now.AddMonths(1)), settings));
            True(!await restarted.EvaluateAsync(Account, Sample(30000, at: Now.AddMinutes(4)), settings));
            Equal(3, sink.Alerts.Count);
        });
        await Test("spend alerts are independent of missing unlimited allocations", async () =>
        {
            foreach (bool unlimited in new[] { false, true })
            {
                var sink = new Sink(); var alerts = new AlertService(Store(), sink);
                await alerts.EvaluateAsync(Account, Sample(7500, entitlement: null, unlimited: unlimited),
                    Settings(Account) with { SpendIncrementUsd = 50 });
                Equal(1, sink.Alerts.Count);
                Equal<decimal?>(50m, sink.Alerts[0].SpendMilestoneUsd);
                Equal(0, sink.Alerts[0].ReachedThresholds.Length);
            }
        });
        await Test("percentage and spend alerts share one accepted submission", async () =>
        {
            var sink = new Sink(); var alerts = new AlertService(Store(), sink);
            await alerts.EvaluateAsync(Account, Sample(10500), Settings(Account) with { SpendIncrementUsd = 50 });
            Equal(1, sink.Alerts.Count); Equal(100m, sink.Alerts[0].HighestThreshold);
            Equal<decimal?>(100m, sink.Alerts[0].SpendMilestoneUsd);
        });
        await Test("per account increment overrides inherit and disable explicitly", async () =>
        {
            var sink = new Sink(); var alerts = new AlertService(Store(), sink);
            var inherited = Account;
            var overridden = Account with { UserId = "43", SpendIncrementUsd = 25 };
            var disabled = Account with { UserId = "44", SpendIncrementUsd = 0 };
            var settings = Settings(inherited, overridden, disabled) with { AlertThresholds = [], SpendIncrementUsd = 50 };
            foreach (var account in settings.Accounts)
                await alerts.EvaluateAsync(account, Sample(2500, account: account), settings);
            Equal(1, sink.Alerts.Count);
            Equal(overridden.Key, sink.Alerts[0].Account.Key);
            await alerts.EvaluateAsync(inherited, Sample(5000), settings);
            Equal(2, sink.Alerts.Count);
        });
        await Test("changing increments does not rearm an already reported dollar level", async () =>
        {
            var sink = new Sink(); var alerts = new AlertService(Store(), sink);
            var settings = Settings(Account) with { AlertThresholds = [], SpendIncrementUsd = 50 };
            await alerts.EvaluateAsync(Account, Sample(10500), settings);
            True(!await alerts.EvaluateAsync(Account, Sample(10500, at: Now.AddMinutes(1)),
                settings with { SpendIncrementUsd = 25 }));
            True(await alerts.EvaluateAsync(Account, Sample(12500, at: Now.AddMinutes(2)),
                settings with { SpendIncrementUsd = 25 }));
            Equal<decimal?>(125m, sink.Alerts[^1].SpendMilestoneUsd);
        });
        await Test("failed spend notification retries without marking milestone", async () =>
        {
            var store = Store(); var sink = new Sink { Accept = false }; var alerts = new AlertService(store, sink);
            var settings = Settings(Account) with { AlertThresholds = [], SpendIncrementUsd = 50 };
            True(!await alerts.EvaluateAsync(Account, Sample(), settings));
            Equal(0, (await store.LoadAlertLedgerAsync()).Value.Accounts.Count);
            sink.Accept = true;
            True(await alerts.EvaluateAsync(Account, Sample(at: Now.AddMinutes(1)), settings));
            Equal(50m, (await store.LoadAlertLedgerAsync()).Value.Accounts[Account.Key].SubmittedSpendUsd);
        });
        await Test("spend settings round-trip and old ledgers remain compatible", async () =>
        {
            var store = Store();
            var account = Account with { SpendIncrementUsd = 12.50m };
            await store.SaveSettingsAsync(Settings(account) with { SpendIncrementUsd = 50 });
            var loaded = (await store.LoadSettingsAsync()).Value;
            Equal<decimal?>(50m, loaded.SpendIncrementUsd);
            Equal<decimal?>(12.50m, loaded.Accounts[0].SpendIncrementUsd);
            var oldLedger = System.Text.Json.JsonSerializer.Deserialize(
                """{"version":1,"accounts":{"github.com:42":{"periodId":"calendar:2026-09","submittedThresholds":[50],"lastSubmittedUtc":"2026-09-17T12:00:00Z"}}}""",
                CoreJsonContext.Default.AlertLedger)!;
            oldLedger.Validate();
            Equal(0m, oldLedger.Accounts[Account.Key].SubmittedSpendUsd);
        });
        await Test("disabled notifications and tiny decimal increment are exact", async () =>
        {
            var sink = new Sink(); var alerts = new AlertService(Store(), sink);
            var settings = Settings(Account) with { AlertThresholds = [], SpendIncrementUsd = .01m };
            True(!await alerts.EvaluateAsync(Account, Sample(1), settings with { NotificationsEnabled = false }));
            True(await alerts.EvaluateAsync(Account, Sample(1, at: Now.AddMinutes(1)), settings));
            Equal<decimal?>(.01m, sink.Alerts[0].SpendMilestoneUsd);
        });
    }

    private static async Task SchedulerTests()
    {
        await Test("manual refresh single flight across callers", async () =>
        {
            var provider = new ControlledProvider(new Clock(Now)); JsonStore store = Store();
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(Account), provider.Clock);
            Task[] calls = Enumerable.Range(0, 20).Select(_ => monitor.RefreshAsync()).ToArray();
            await Until(() => provider.Calls == 1);
            provider.Release();
            await Task.WhenAll(calls);
            Equal(1, provider.Calls); Equal(AccountStatus.Fresh, monitor.States[0].Status);
        });
        await Test("parallel account work bounded", async () =>
        {
            var provider = new ControlledProvider(new Clock(Now)); JsonStore store = Store();
            Account[] accounts = Enumerable.Range(1, 8).Select(i => Account with { UserId = i.ToString(CultureInfo.InvariantCulture) }).ToArray();
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(accounts), provider.Clock, 2);
            Task work = monitor.RefreshAsync();
            await Until(() => provider.Calls == 2); Equal(2, provider.MaximumActive);
            provider.Release(); await work;
            Equal(8, provider.Calls); Equal(2, provider.MaximumActive);
        });
        await Test("caller cancellation does not cancel shared refresh", async () =>
        {
            var provider = new ControlledProvider(new Clock(Now)); JsonStore store = Store();
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(Account), provider.Clock);
            using var cancel = new CancellationTokenSource();
            Task first = monitor.RefreshAsync(cancellationToken: cancel.Token);
            Task second = monitor.RefreshAsync();
            await Until(() => provider.Calls == 1); cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => first);
            provider.Release(); await second;
            Equal(AccountStatus.Fresh, monitor.States[0].Status);
        });
        await Test("interval change and resume cause one catchup", async () =>
        {
            var clock = new Clock(Now); var provider = new ControlledProvider(clock); provider.Release();
            JsonStore store = Store();
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(Account), clock);
            await monitor.StartAsync(); await Until(() => monitor.States[0].Status == AccountStatus.Fresh);
            Equal<DateTimeOffset?>(Now.AddHours(1), monitor.States[0].NextRefreshUtc);
            clock.Advance(TimeSpan.FromMinutes(10));
            monitor.UpdateSettings(Settings(Account) with { PollIntervalMinutes = 5 });
            await Until(() => provider.Calls == 2 && monitor.States[0].NextRefreshUtc == clock.GetUtcNow().AddMinutes(5));
            clock.Advance(TimeSpan.FromHours(6)); monitor.NotifyResume();
            await Until(() => provider.Calls == 3 && monitor.States[0].NextRefreshUtc == clock.GetUtcNow().AddMinutes(5));
            Equal(3, provider.Calls);
        });
        await Test("account removal cancels operation and prevents persistence", async () =>
        {
            var provider = new ControlledProvider(new Clock(Now)); JsonStore store = Store();
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(Account), provider.Clock);
            Task work = monitor.RefreshAsync(); await Until(() => provider.Calls == 1);
            monitor.UpdateSettings(Settings()); await work;
            Equal(0, monitor.States.Count);
            Equal(0, (await store.LoadHistoryAsync(Account.Key, Now.AddDays(-1))).Value.Length);
        });
        await Test("failed refresh preserves saved last known values", async () =>
        {
            var clock = new Clock(Now); var provider = new ControlledProvider(clock); provider.Release();
            JsonStore store = Store();
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(Account), clock);
            await monitor.RefreshAsync(); provider.Error = new HttpRequestException("fake-secret");
            await monitor.RefreshAsync();
            Equal(AccountStatus.NetworkError, monitor.States[0].Status);
            Equal(50m, monitor.States[0].Snapshot!.ConsumptionUsd);
            True(!monitor.States[0].Diagnostic!.Contains("fake-secret"));
            True(monitor.States[0].NextRefreshUtc > Now);
        });
        await Test("manual refresh respects rate-limit not-before", async () =>
        {
            var clock = new Clock(Now); var provider = new ControlledProvider(clock); provider.Release();
            provider.Error = new ServiceException(AccountStatus.RateLimited, "Rate limited", Now.AddHours(1));
            JsonStore store = Store();
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(Account), clock);
            await monitor.RefreshAsync(); await monitor.RefreshAsync();
            Equal(1, provider.Calls); Equal<DateTimeOffset?>(Now.AddHours(1), monitor.States[0].NextRefreshUtc);
        });
        await Test("safe removal joins before credential deletion", async () =>
        {
            var provider = new ControlledProvider(new Clock(Now)); JsonStore store = Store();
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(Account), provider.Clock);
            Task work = monitor.RefreshAsync(); await Until(() => provider.Calls == 1);
            await monitor.RemoveAccountAsync(Account.Key);
            await work;
            True(work.IsCompleted); Equal(0, monitor.States.Count); Equal(0, provider.Active);
        });
        await Test("pause drains reconnect work and settings resume monitoring", async () =>
        {
            var provider = new ControlledProvider(new Clock(Now)); JsonStore store = Store();
            await store.SaveSettingsAsync(Settings(Account));
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(Account), provider.Clock);
            Task oldWork = monitor.RefreshAsync(); await Until(() => provider.Calls == 1);
            await monitor.PauseAccountAsync(Account.Key); await oldWork;
            Equal(0, provider.Active); Equal(0, monitor.States.Count);
            Equal(1, (await store.LoadSettingsAsync()).Value.Accounts.Length);
            provider.Release();
            monitor.UpdateSettings(Settings(Account));
            await monitor.RefreshAsync(Account.Key);
            Equal(2, provider.Calls); Equal(AccountStatus.Fresh, monitor.States[0].Status);
        });
        await Test("snapshot persistence failures visibly marked unsaved", async () =>
        {
            var provider = new ControlledProvider(new Clock(Now)); provider.Release(); JsonStore store = Store();
            string history = store.HistoryDirectory(Account.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(history)!);
            await File.WriteAllTextAsync(history, "not a directory");
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(Account), provider.Clock);
            await monitor.RefreshAsync();
            Equal(AccountStatus.StorageError, monitor.States[0].Status);
            Equal(50m, monitor.States[0].Snapshot!.ConsumptionUsd);
            True(monitor.States[0].Diagnostic!.Contains("unsaved"));
        });
        await Test("external state mutation does not change monitoring settings", async () =>
        {
            var provider = new ControlledProvider(new Clock(Now)); provider.Release(); JsonStore store = Store();
            Account account = Account with { ThresholdOverrides = [25m] };
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(account), provider.Clock);
            monitor.States[0].Account.ThresholdOverrides![0] = -1m;
            monitor.StateChanged += state => state.Account.ThresholdOverrides![0] = -2m;
            await monitor.RefreshAsync();
            Equal(AccountStatus.Fresh, monitor.States[0].Status);
            Equal(25m, monitor.States[0].Account.ThresholdOverrides![0]);
        });
        await Test("network recovery jitters overdue retry within bounds", async () =>
        {
            var clock = new Clock(Now); var provider = new ControlledProvider(clock); provider.Release();
            provider.Error = new HttpRequestException(); JsonStore store = Store();
            await using var monitor = new MonitorService(provider, store, new(store, new Sink()), Settings(Account), clock);
            await monitor.RefreshAsync();
            clock.Advance(TimeSpan.FromMinutes(3));
            monitor.NotifyNetworkRecovery();
            True(monitor.States[0].NextRefreshUtc >= clock.GetUtcNow());
            True(monitor.States[0].NextRefreshUtc <= clock.GetUtcNow().AddSeconds(30));
            Equal(1, provider.Calls);
        });
        await Test("maintenance failures surface through monitor diagnostics", async () =>
        {
            var clock = new Clock(Now); JsonStore store = Store();
            await store.AppendHistoryAsync(Sample(at: Now.AddDays(-91)));
            string path = Directory.GetFiles(store.HistoryDirectory(Account.Key), "*.jsonl")[0];
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await using var monitor = new MonitorService(new ControlledProvider(clock), store,
                new(store, new Sink()), Settings(), clock);
            var diagnostics = new ConcurrentQueue<string>();
            monitor.DiagnosticReported += diagnostics.Enqueue;
            await monitor.StartAsync();
            await Until(() => !diagnostics.IsEmpty);
            True(diagnostics.Any(d => d.Contains("retention failed")));
        });
        await Test("Shell rejection surfaces through monitor diagnostics", async () =>
        {
            var clock = new Clock(Now); var provider = new ControlledProvider(clock); provider.Release();
            JsonStore store = Store(); var sink = new Sink { Accept = false };
            await using var monitor = new MonitorService(provider, store, new(store, sink), Settings(Account), clock);
            var diagnostics = new ConcurrentQueue<string>();
            monitor.DiagnosticReported += diagnostics.Enqueue;
            await monitor.RefreshAsync();
            True(diagnostics.Any(d => d.Contains("did not accept")));
            await monitor.RefreshAsync();
            Equal(1, sink.Alerts.Count);
            Equal(0, (await store.LoadAlertLedgerAsync()).Value.Accounts.Count);
        });
    }

    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }

    private sealed record RequestRecord(Uri Uri, string? Authorization, string Body);
    private sealed class FakeHttp(Func<RequestRecord, HttpResponseMessage> response) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RequestRecord> _requests = new();
        public IReadOnlyList<RequestRecord> Requests => _requests.ToArray();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captured = new RequestRecord(request.RequestUri!, request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            _requests.Enqueue(captured);
            return response(captured);
        }
    }
    private sealed class MemoryCredentials : ICredentialStore
    {
        public TokenSet? Tokens;
        public int Writes;
        public bool FailWrite;
        public Task<TokenSet?> ReadAsync(Account account, CancellationToken cancellationToken = default) => Task.FromResult(Tokens);
        public Task WriteAsync(Account account, TokenSet tokens, CancellationToken cancellationToken = default)
        {
            if (FailWrite) throw new IOException("fake secret must not escape");
            Writes++; Tokens = tokens; return Task.CompletedTask;
        }
        public Task DeleteAsync(Account account, CancellationToken cancellationToken = default) { Tokens = null; return Task.CompletedTask; }
    }
    private sealed class Sink : INotificationSink
    {
        public bool Accept = true;
        public List<UsageAlert> Alerts { get; } = [];
        public Task<bool> SubmitAsync(UsageAlert alert, CancellationToken cancellationToken = default)
        {
            Alerts.Add(alert); return Task.FromResult(Accept);
        }
    }
    private sealed class ControlledProvider(Clock clock) : ICopilotUsageProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Clock Clock => clock;
        public int Calls, Active, MaximumActive;
        public Exception? Error;
        public void Release() => _release.TrySetResult();
        public async Task<UsageSnapshot> FetchAsync(Account account, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            int active = Interlocked.Increment(ref Active);
            int previous;
            do { previous = MaximumActive; } while (active > previous && Interlocked.CompareExchange(ref MaximumActive, active, previous) != previous);
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                if (Error is { } error) throw error;
                return Sample(account: account, at: clock.GetUtcNow());
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    private sealed class Clock(DateTimeOffset initial) : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _now = initial;
        private readonly List<ClockTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public override long GetTimestamp() => GetUtcNow().UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public int TimerCount { get { lock (_sync) return _timers.Count(t => t.Due is not null); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (_sync) { _timers.Add(timer); timer.Change(dueTime, period); }
            return timer;
        }
        public void Advance(TimeSpan by)
        {
            List<ClockTimer> firing;
            lock (_sync)
            {
                _now += by;
                firing = _timers.Where(t => t.Due <= _now).ToList();
                foreach (ClockTimer timer in firing) timer.Due = timer.Period > TimeSpan.Zero ? _now + timer.Period : null;
            }
            foreach (ClockTimer timer in firing) timer.Callback(timer.State);
        }
        private sealed class ClockTimer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback => callback;
            public object? State => state;
            public DateTimeOffset? Due;
            public TimeSpan Period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    Period = period;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                    return true;
                }
            }
            public void Dispose() { lock (owner._sync) { Due = null; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
