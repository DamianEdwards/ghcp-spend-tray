using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

namespace GHCPSpendTray.Linux;

public sealed class SecretServiceCredentials : ICredentialStore
{
    internal static string Target(Account account) =>
        GitHubOAuth.ResolveClientId(account.Host, account.OAuthClientId) + "/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Key)));

    public async Task<TokenSet?> ReadAsync(Account account, CancellationToken cancellationToken = default)
    {
        string? payload = await RunAsync(Target(account), null, false, cancellationToken);
        if (payload is null) return null;
        try
        {
            var tokens = JsonSerializer.Deserialize(payload, CoreJsonContext.Default.TokenSet)
                ?? throw new JsonException();
            tokens.Validate();
            return tokens;
        }
        catch (Exception ex) when (ex is JsonException or ServiceException)
        {
            throw new ServiceException(AccountStatus.SignInRequired, "Saved credentials are invalid. Reconnect this account.");
        }
    }

    public async Task WriteAsync(Account account, TokenSet tokens, CancellationToken cancellationToken = default)
    {
        tokens.Validate();
        await RunAsync(Target(account), JsonSerializer.Serialize(tokens, CoreJsonContext.Default.TokenSet),
            false, cancellationToken);
    }

    public async Task DeleteAsync(Account account, CancellationToken cancellationToken = default) =>
        await RunAsync(Target(account), null, true, cancellationToken);

    private static async Task<string?> RunAsync(string target, string? payload, bool delete, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            return await Task.Run(() => SecretService.Access(target, payload, delete, linked.Token), linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new ServiceException(AccountStatus.StorageError, "Credential storage timed out. Unlock your Secret Service keyring and try again.");
        }
        catch (PlatformOperationException ex)
        {
            throw new ServiceException(AccountStatus.StorageError, ex.Message);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new ServiceException(AccountStatus.StorageError, "Install libsecret-1 and a Secret Service provider (GNOME Keyring or KWallet with Secret Service enabled). No credentials were saved to disk.");
        }
    }
}

internal static partial class SecretService
{
    private const string Secret = "libsecret-1.so.0", Glib = "libglib-2.0.so.0";
    private const string Gio = "libgio-2.0.so.0", GObject = "libgobject-2.0.so.0";
    private const int SearchAll = 1 << 1, SearchUnlock = 1 << 2, SearchLoadSecrets = 1 << 3;

    internal static string? Access(string target, string? payload, bool delete, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var attributes = new Attributes(target);
        nint cancellable = g_cancellable_new();
        try
        {
            using var registration = token.Register(() => g_cancellable_cancel(cancellable));
            // Search includes locked items: a dismissed unlock must never look like "not found".
            nint items = secret_service_search_sync(0, 0, attributes.Handle,
                SearchAll | SearchUnlock | SearchLoadSecrets, cancellable, out nint error);
            try
            {
                Check(error, token);
                int count = 0;
                string? result = null;
                for (nint node = items; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                {
                    nint item = Marshal.ReadIntPtr(node);
                    if (secret_item_get_locked(item) != 0)
                        throw new PlatformOperationException("The credential keyring is locked. Unlock it and retry; account configuration was retained.");
                    count++;
                    if (delete)
                    {
                        int removed = secret_item_delete_sync(item, cancellable, out error);
                        Check(error, token);
                        if (removed == 0) throw Failure();
                    }
                    else if (payload is null)
                    {
                        nint secret = secret_item_get_secret(item);
                        if (secret == 0) throw Failure();
                        try { result = Marshal.PtrToStringUTF8(secret_value_get_text(secret)) ?? throw Failure(); }
                        finally { secret_value_unref(secret); }
                    }
                }
                if (!delete && count > 1)
                    throw new PlatformOperationException("Multiple credentials match this account. Remove its duplicate GHCPSpendTray entries in your keyring before reconnecting.");
                if (payload is not null)
                {
                    int stored = secret_password_storev_sync(0, attributes.Handle, "default",
                        "GHCPSpendTray GitHub credentials", payload, cancellable, out error);
                    Check(error, token);
                    if (stored == 0) throw Failure();
                }
                return result;
            }
            finally
            {
                for (nint node = items; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
                    g_object_unref(Marshal.ReadIntPtr(node));
                g_list_free(items);
            }
        }
        finally { g_object_unref(cancellable); }
    }

    private static PlatformOperationException Failure() => new(
        "Secret Service could not complete the credential operation. Start and unlock GNOME Keyring or KWallet's Secret Service, then retry. There is no plaintext fallback.");

    private static void Check(nint error, CancellationToken token)
    {
        if (error == 0) return;
        g_error_free(error);
        token.ThrowIfCancellationRequested();
        // Native error strings can include provider-controlled data; never log or expose them.
        throw Failure();
    }

    private sealed class Attributes : IDisposable
    {
        private readonly List<nint> _strings = [];
        private readonly nint _library;
        internal nint Handle { get; }
        internal Attributes(string target)
        {
            _library = NativeLibrary.Load(Glib);
            Handle = g_hash_table_new(NativeLibrary.GetExport(_library, "g_str_hash"),
                NativeLibrary.GetExport(_library, "g_str_equal"));
            Add("xdg:schema", "io.github.ghcpspendtray.Credentials.v1");
            Add("target", target);
        }
        private void Add(string key, string value)
        {
            nint k = Marshal.StringToCoTaskMemUTF8(key), v = Marshal.StringToCoTaskMemUTF8(value);
            _strings.Add(k); _strings.Add(v);
            g_hash_table_insert(Handle, k, v);
        }
        public void Dispose()
        {
            g_hash_table_unref(Handle);
            foreach (nint value in _strings) Marshal.FreeCoTaskMem(value);
            NativeLibrary.Free(_library);
        }
    }

    [LibraryImport(Secret)] private static partial nint secret_service_search_sync(nint service, nint schema, nint attributes, int flags, nint cancellable, out nint error);
    [LibraryImport(Secret)] private static partial int secret_item_get_locked(nint item);
    [LibraryImport(Secret)] private static partial nint secret_item_get_secret(nint item);
    [LibraryImport(Secret)] private static partial nint secret_value_get_text(nint value);
    [LibraryImport(Secret)] private static partial void secret_value_unref(nint value);
    [LibraryImport(Secret)] private static partial int secret_item_delete_sync(nint item, nint cancellable, out nint error);
    [LibraryImport(Secret, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int secret_password_storev_sync(nint schema, nint attributes, string collection, string label, string password, nint cancellable, out nint error);
    [LibraryImport(Glib)] private static partial nint g_hash_table_new(nint hash, nint equal);
    [LibraryImport(Glib)] private static partial int g_hash_table_insert(nint table, nint key, nint value);
    [LibraryImport(Glib)] private static partial void g_hash_table_unref(nint table);
    [LibraryImport(Glib)] private static partial void g_list_free(nint list);
    [LibraryImport(Glib)] private static partial void g_error_free(nint error);
    [LibraryImport(Gio)] private static partial nint g_cancellable_new();
    [LibraryImport(Gio)] private static partial void g_cancellable_cancel(nint cancellable);
    [LibraryImport(GObject)] private static partial void g_object_unref(nint value);
}
