namespace GHCPSpendTray.Core;

public static class GitHubOAuth
{
    // Public github.com registration; device flow needs no client secret.
    public const string ClientId = "Ov23ctzkXY5CJhfKQo7T";
    public const string MicrosoftEnterpriseClientId = "Ov23ox38SoD1bIpzU9zZ";

    public static string ResolveClientId(string host, string? configuredClientId = null)
    {
        string canonical = HostResolver.Resolve(host).Host;
        if (canonical == "github.com")
        {
            if (configuredClientId is not null && configuredClientId != ClientId)
                throw new ArgumentException("github.com uses the built-in OAuth registration.", nameof(configuredClientId));
            return ClientId;
        }
        if (configuredClientId is not null)
        {
            OAuthValidation.ValidateClientId(configuredClientId);
            return configuredClientId;
        }
        return canonical switch
        {
            "msft.ghe.com" => MicrosoftEnterpriseClientId,
            _ => throw new ServiceException(AccountStatus.Unsupported,
                "Enter an OAuth client ID registered for the selected host before signing in.")
        };
    }
}
