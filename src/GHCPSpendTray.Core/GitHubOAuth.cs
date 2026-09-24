namespace GHCPSpendTray.Core;

public static class GitHubOAuth
{
    // Public github.com registration; device flow needs no client secret.
    public const string ClientId = "Ov23ctzkXY5CJhfKQo7T";

    public static string ResolveClientId(string host) => HostResolver.Resolve(host).Host switch
    {
        "github.com" => ClientId,
        "msft.ghe.com" => "Ov23ox38SoD1bIpzU9zZ",
        _ => throw new ServiceException(AccountStatus.Unsupported,
            "No GHCPSpendTray OAuth application is registered in this build for the selected host. " +
            "An approved host-specific registration must be added before signing in.")
    };
}
