using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

namespace GHCPSpendTray.MacBridge;

// Platform requests stay in-process. Secrets never cross a socket, file, or command line.
internal sealed class NativePlatform(Action<BridgeEvent> publish) : ICredentialStore, IStartupRegistration, IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PlatformReply>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    public bool Enabled { get; private set; }
    public bool CanChange { get; private set; }
    public string Description { get; private set; } = "Loading login item...";

    internal async Task<IStartupRegistration> InitializeAsync()
    {
        ApplyStartup(await RequestAsync(new() { Kind = "platform", Operation = "startup.read" }));
        return this;
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        ApplyStartup(await RequestAsync(new() { Kind = "platform", Operation = "startup.write", Enabled = enabled }));
        if (enabled != Enabled)
            throw new PlatformOperationException(Description);
    }

    private void ApplyStartup(PlatformReply reply)
    {
        Enabled = reply.Enabled;
        CanChange = reply.CanChange;
        Description = reply.Description;
    }

    public async Task<TokenSet?> ReadAsync(Account account, CancellationToken cancellationToken = default)
    {
        var reply = await RequestAsync(new() { Kind = "platform", Operation = "credential.read", Target = Target(account) }, cancellationToken);
        reply.Tokens?.Validate();
        return reply.Tokens;
    }

    public async Task WriteAsync(Account account, TokenSet tokens, CancellationToken cancellationToken = default)
    {
        tokens.Validate();
        await RequestAsync(new() { Kind = "platform", Operation = "credential.write", Target = Target(account), Tokens = tokens }, cancellationToken);
    }

    public async Task DeleteAsync(Account account, CancellationToken cancellationToken = default) =>
        await RequestAsync(new() { Kind = "platform", Operation = "credential.delete", Target = Target(account) }, cancellationToken);

    internal async Task<bool> NotifyAsync(string key, string title, string message)
    {
        var reply = await RequestAsync(new() { Kind = "platform", Operation = "notification", Key = key, Title = title, Message = message });
        return reply.Accepted;
    }

    internal static string Target(Account account) => "GHCPSpendTray/v1/" +
        GitHubOAuth.ResolveClientId(account.Host, account.OAuthClientId) + "/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Key)));

    private async Task<PlatformReply> RequestAsync(BridgeEvent request, CancellationToken cancellationToken = default)
    {
        string id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<PlatformReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _stop.Token);
        _pending[id] = completion;
        try
        {
            publish(request with { Id = id });
            var reply = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            if (reply.Error is not null) throw new PlatformOperationException(reply.Error);
            return reply;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    internal void Complete(string id, PlatformReply reply)
    {
        // Replies can arrive after cancellation (e.g. a dismissed Keychain prompt).
        if (_pending.TryGetValue(id, out var completion)) completion.TrySetResult(reply);
    }

    public void Dispose() => _stop.Cancel();
}
