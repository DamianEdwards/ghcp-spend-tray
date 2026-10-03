using System.Text.Json.Serialization;
using GHCPSpendTray.Core;
using GHCPSpendTray.Shared;

namespace GHCPSpendTray.MacBridge;

public sealed record Command
{
    public string Id { get; init; } = "";
    public string Method { get; init; } = "";
    public string? Directory { get; init; }
    public bool Demo { get; init; }
    public bool Empty { get; init; }
    public string? Key { get; init; }
    public string? Host { get; init; }
    public string? ClientId { get; init; }
    public bool OfflineAccess { get; init; }
    public string? DisplayName { get; init; }
    public string? Thresholds { get; init; }
    public decimal? SpendIncrementUsd { get; init; }
    public bool? ShowPeriodEstimate { get; init; }
    public SettingsView? Settings { get; init; }
    public string? TargetId { get; init; }
    public PlatformReply? Reply { get; init; }
}

public sealed record PlatformReply
{
    public string? Error { get; init; }
    public TokenSet? Tokens { get; init; }
    public bool Accepted { get; init; }
    public bool Enabled { get; init; }
    public bool CanChange { get; init; }
    public string Description { get; init; } = "";
}

public sealed record AccountPreferences(string DisplayName, string Thresholds, decimal? SpendIncrementUsd,
    string? ClientId, bool ShowPeriodEstimate = false);

public sealed record BridgeEvent
{
    public required string Kind { get; init; }
    public string? Id { get; init; }
    public string? Error { get; init; }
    public string? Text { get; init; }
    public DashboardView? Dashboard { get; init; }
    public SettingsView? Settings { get; init; }
    public AccountPreferences? Preferences { get; init; }
    public TrayPresentation? Tray { get; init; }
    public DevicePrompt? Prompt { get; init; }
    public bool Cancelled { get; init; }
    public string? Operation { get; init; }
    public string? Target { get; init; }
    public TokenSet? Tokens { get; init; }
    public bool Enabled { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }
    public string? Key { get; init; }
}

public sealed record Receipt(string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Command))]
[JsonSerializable(typeof(BridgeEvent[]))]
[JsonSerializable(typeof(Receipt))]
public partial class BridgeJsonContext : JsonSerializerContext;
