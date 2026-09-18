using System.Text.Json;
using System.Text.Json.Serialization;

namespace GHSpend.Core;

internal sealed class DeviceWire
{
    [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
    [JsonPropertyName("user_code")] public string? UserCode { get; set; }
    [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int? Interval { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

internal sealed class TokenWire
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("refresh_token_expires_in")] public int? RefreshTokenExpiresIn { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

internal sealed class IdentityWire
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("login")] public string? Login { get; set; }
}

internal sealed class UsageWire
{
    [JsonPropertyName("quota_snapshots")] public QuotaWire? QuotaSnapshots { get; set; }
    [JsonPropertyName("quota_reset_date")] public JsonElement QuotaResetDate { get; set; }
    [JsonPropertyName("quota_reset_date_utc")] public JsonElement QuotaResetDateUtc { get; set; }
}

internal sealed class QuotaWire
{
    [JsonPropertyName("premium_interactions")] public PremiumWire? PremiumInteractions { get; set; }
}

internal sealed class PremiumWire
{
    [JsonPropertyName("credits_used")] public decimal? CreditsUsed { get; set; }
    [JsonPropertyName("entitlement")] public decimal? Entitlement { get; set; }
    [JsonPropertyName("unlimited")] public bool? Unlimited { get; set; }
    [JsonPropertyName("has_quota")] public bool? HasQuota { get; set; }
    [JsonPropertyName("token_based_billing")] public bool? TokenBasedBilling { get; set; }
    [JsonPropertyName("timestamp_utc")] public JsonElement TimestampUtc { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(TokenSet))]
[JsonSerializable(typeof(UsageSnapshot))]
[JsonSerializable(typeof(AlertLedger))]
public partial class CoreJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(DeviceWire))]
[JsonSerializable(typeof(TokenWire))]
[JsonSerializable(typeof(IdentityWire))]
[JsonSerializable(typeof(UsageWire))]
internal partial class WireJsonContext : JsonSerializerContext;
