using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GHCPSpendTray.App.Platform;
using GHCPSpendTray.Core;

namespace GHCPSpendTray.App;

internal sealed class ApplicationController : Shared.ApplicationController
{
    internal ApplicationController(string directory, bool portable, HttpClient? http = null,
        ICredentialStore? credentials = null)
        : base(directory, portable, credentials ?? new VaultCredentials(portable ? directory : null),
            http, async () => await StartupRegistration.CreateAsync(), Diagnostics.Record) { }

    protected override AppOperationException SafeError(Exception ex)
    {
        if (ex is CredentialVaultException credential)
            Diagnostics.Record($"Credential Manager {credential.Operation} failed (Win32 error {credential.NativeErrorCode}).");
        return ex is CredentialVaultException { Operation: "write", NativeErrorCode: 8 }
            ? new("Windows Credential Manager could not save the credentials (Windows error 8). Its credential store may be full. Open Control Panel > Credential Manager > Windows Credentials, remove only entries you recognize as unused, then try signing in again.")
            : base.SafeError(ex);
    }
}

internal sealed class VaultCredentials(string? portableDirectory) : ICredentialStore
{
    private readonly CredentialVault _vault = new();
    private readonly string _prefix = portableDirectory is null ? "GHCPSpendTray/v1/" :
        "GHCPSpendTray/portable/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(portableDirectory).ToUpperInvariant()))) + "/";
    internal string Target(Account account) => _prefix + GitHubOAuth.ResolveClientId(account.Host, account.OAuthClientId) + "/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Key)));
    public Task<TokenSet?> ReadAsync(Account account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = _vault.Read(Target(account));
        if (payload is null) return Task.FromResult<TokenSet?>(null);
        var value = JsonSerializer.Deserialize(payload, CoreJsonContext.Default.TokenSet);
        if (value is null || value.Version != 1 || string.IsNullOrWhiteSpace(value.AccessToken))
            throw new InvalidDataException("The stored GHCPSpendTray credential is invalid.");
        return Task.FromResult<TokenSet?>(value);
    }
    public Task WriteAsync(Account account, TokenSet tokens, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _vault.Write(Target(account), JsonSerializer.Serialize(tokens, CoreJsonContext.Default.TokenSet));
        return Task.CompletedTask;
    }
    public Task DeleteAsync(Account account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _vault.Delete(Target(account));
        return Task.CompletedTask;
    }
}
