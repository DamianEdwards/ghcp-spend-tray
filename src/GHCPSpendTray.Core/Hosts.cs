namespace GHCPSpendTray.Core;

public enum HostKind { GitHub, EnterpriseCloud, EnterpriseServer }

public sealed record ResolvedHost(string Host, HostKind Kind, Uri WebBaseUri, Uri ApiBaseUri)
{
    public Uri ApiUri(string relativePath) => Relative(ApiBaseUri, relativePath);
    public Uri AuthUri(string relativePath) => Relative(WebBaseUri, relativePath);

    public Uri ValidateVerificationUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.Equals(uri.IdnHost, WebBaseUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != WebBaseUri.Port)
            throw new ServiceException(AccountStatus.InvalidData, "The authorization verification URL has an unexpected origin.");
        return uri;
    }

    private static Uri Relative(Uri origin, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') ||
            path.Contains(':') || path.Split('/').Any(s => s is ".." or "."))
            throw new ArgumentException("An API path must be relative to its configured base.", nameof(path));
        return new Uri(origin, path);
    }
}

public static class HostResolver
{
    public static ResolvedHost Resolve(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value != value.Trim() || value.Contains('\\') || value.Any(char.IsWhiteSpace))
            throw new ArgumentException("Enter an HTTPS hostname without whitespace.", nameof(value));
        string candidate = value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value;
        int pathStart = candidate.IndexOf('/', candidate.IndexOf("://", StringComparison.Ordinal) + 3);
        if (pathStart >= 0 && candidate[pathStart..] != "/")
            throw new ArgumentException("Host input must not contain a path.", nameof(value));
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.HostNameType != UriHostNameType.Dns ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            candidate.Contains('?') || candidate.Contains('#') || uri.Host.EndsWith('.') ||
            uri.Port is < 1 or > 65535)
            throw new ArgumentException("Only an HTTPS DNS host with no credentials, path, query, or fragment is supported.", nameof(value));
        string hostname = uri.IdnHost.ToLowerInvariant();
        if (!hostname.Contains('.') || hostname.Split('.').Any(s =>
                s.Length is < 1 or > 63 || s.StartsWith('-') || s.EndsWith('-') ||
                s.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("Enter a valid fully qualified hostname.", nameof(value));
        HostKind kind = hostname == "github.com" ? HostKind.GitHub :
            hostname.EndsWith(".ghe.com", StringComparison.Ordinal) &&
            hostname.Split('.').Length == 3 ? HostKind.EnterpriseCloud : HostKind.EnterpriseServer;
        if (kind != HostKind.EnterpriseServer && !uri.IsDefaultPort)
            throw new ArgumentException("Custom ports are supported only for Enterprise Server.", nameof(value));
        if (hostname == "api.github.com" || hostname.StartsWith("api.", StringComparison.Ordinal) &&
            hostname.EndsWith(".ghe.com", StringComparison.Ordinal))
            throw new ArgumentException("Enter the web host, not its API hostname.", nameof(value));
        string host = hostname + (uri.IsDefaultPort ? "" : ":" + uri.Port);
        var web = new Uri("https://" + host + "/");
        var api = new Uri(kind switch
        {
            HostKind.GitHub => "https://api.github.com/",
            HostKind.EnterpriseCloud => "https://api." + hostname + "/",
            _ => "https://" + host + "/api/v3/"
        });
        return new(host, kind, web, api);
    }
}

internal static class OAuthValidation
{
    public static void ValidateClientId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 256 ||
            clientId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.'))
            throw new ArgumentException("The OAuth client ID must contain only supported characters and be at most 256 characters.");
    }
}
