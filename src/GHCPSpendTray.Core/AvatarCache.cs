using System.Security.Cryptography;
using System.Text;

namespace GHCPSpendTray.Core;

public sealed class AvatarCache(HttpClient httpClient, string dataDirectory)
{
    private readonly string _root = Path.Combine(Path.GetFullPath(dataDirectory), "avatars");
    private readonly SemaphoreSlim _updates = new(1, 1);

    private string DirectoryFor(Account account) =>
        Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.Key))).ToLowerInvariant());

    public string? GetPath(Account account)
    {
        string directory = DirectoryFor(account);
        string pointer = Path.Combine(directory, "current.txt");
        if (!File.Exists(pointer)) return null;
        string name = File.ReadAllText(pointer);
        if (name.Length is < 68 or > 69 || name[..64].Any(c => !char.IsAsciiHexDigit(c)) ||
            name[64] != '.' || name[65..] is not ("png" or "jpg" or "gif" or "webp"))
            throw new InvalidDataException("The cached avatar index is invalid.");
        string path = Path.Combine(directory, name);
        if (!File.Exists(path)) throw new InvalidDataException("The cached avatar image is missing.");
        return path;
    }

    public async Task<string?> UpdateAsync(Account account, string? avatarUrl, CancellationToken cancellationToken = default)
    {
        var host = HostResolver.Resolve(account.Host);
        string? validated = AccountAvatar.Validate(host, avatarUrl);
        if (validated is null && avatarUrl is not null)
            throw new InvalidDataException("The avatar URL is not permitted for this account's host.");

        await _updates.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (validated is null)
            {
                Remove(account);
                return null;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, validated);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new HttpRequestException("Avatar redirects are not permitted.");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("The avatar request failed (HTTP " + (int)response.StatusCode + ").");
            string? extension = response.Content.Headers.ContentType?.MediaType switch
            {
                "image/png" => "png",
                "image/jpeg" => "jpg",
                "image/gif" => "gif",
                "image/webp" => "webp",
                _ => null
            };
            if (extension is null)
                throw new InvalidDataException("The avatar response is not a supported image.");
            const int limit = 1024 * 1024;
            if (response.Content.Headers.ContentLength > limit)
                throw new InvalidDataException("The avatar response exceeded the size limit.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > limit)
                    throw new InvalidDataException("The avatar response exceeded the size limit.");
                buffer.Write(chunk, 0, count);
            }
            if (buffer.Length == 0) throw new InvalidDataException("The avatar response was empty.");
            ReadOnlySpan<byte> bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
            bool image = extension switch
            {
                "png" => bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "jpg" => bytes.StartsWith(new byte[] { 255, 216, 255 }),
                "gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
                "webp" => bytes.StartsWith("RIFF"u8) && bytes.Length >= 12 && bytes[8..].StartsWith("WEBP"u8),
                _ => false
            };
            if (!image) throw new InvalidDataException("The avatar response did not contain a valid image header.");

            string directory = DirectoryFor(account);
            Directory.CreateDirectory(directory);
            string name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + "." + extension;
            string path = Path.Combine(directory, name);
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                if (!File.Exists(path))
                {
                    await File.WriteAllBytesAsync(temporary, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, path);
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await File.WriteAllTextAsync(temporary, name, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, Path.Combine(directory, "current.txt"), overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }
        finally { _updates.Release(); }
    }

    public void Remove(Account account)
    {
        string directory = DirectoryFor(account);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
