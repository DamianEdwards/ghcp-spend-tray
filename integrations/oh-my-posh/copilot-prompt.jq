def amount:
  type == "number" and isfinite and . >= 0 and . <= 9007199254740991;

def money:
  . as $value |
  if ($value | round) >= 1000 then
    "$" + (($value / 100 | round) / 10 | tostring) + "K"
  else "$" + ($value | round | tostring) end;

def valid_quota:
  .quota_snapshots.premium_interactions as $q |
  type == "object" and ($q | type) == "object" and
  $q.token_based_billing == true and
  ($q.unlimited | type) == "boolean" and
  ($q.has_quota | type) == "boolean" and
  ($q.credits_used | amount) and
  ($q.entitlement == null or ($q.entitlement | amount));

def epoch:
  type == "number" and . >= 0 and . <= 253402300799 and . == floor;

def valid_cache($hostname; $key):
  .version == 1 and .hostname == $hostname and .contextKey == $key and
  (.status == "fresh" or .status == "unavailable") and
  (.nextAttemptEpoch | epoch) and (.fetchedEpoch | epoch) and
  (.freshUntil | epoch) and (.forecastUntil | epoch) and
  (.text | type) == "string" and (.text | length) <= 128 and
  (.forecast | type) == "string" and (.forecast | length) <= 64 and
  (.forecastState == "green" or .forecastState == "yellow" or
    .forecastState == "red" or .forecastState == "unknown") and
  (.diagnostic | type) == "string" and (.diagnostic | length) <= 256 and
  ([.text, .forecast, .diagnostic] | all(test("[\\x00-\\x1f\\x7f]") | not));

($ARGS.named.mode // "view") as $mode |
($ARGS.named.hostname // "") as $hostname |
($ARGS.named.key // "") as $key |
($ARGS.named.account // "") as $account |
($ARGS.named.now // 0) as $now |
($ARGS.named.interval // 3600) as $interval |
($ARGS.named.observed // -1) as $observed |
($ARGS.named.reset // -1) as $reset |
($ARGS.named.start // 0) as $start |
($ARGS.named.end // 0) as $end |
($ARGS.named.retry // 0) as $retry |
($ARGS.named.diagnostic // "-") as $diagnostic |
($ARGS.named.estimate // 0) as $estimate |
if $mode == "validate" then
  if valid_quota then true else error("Unsupported or invalid token-based quota") end
elif $mode == "snapshot" then
  .quota_snapshots.premium_interactions as $q |
  (if $observed < 0 then $now else $observed end) as $source |
  ($q.entitlement != null and $q.entitlement > 0 and ($q.unlimited | not)) as $allocated |
  (if $allocated then $q.credits_used / $q.entitlement * 100 else null end) as $percent |
  (if $allocated and $source >= $start and $source <= $now and
      $source + $interval > $now and $source - $start >= 86400 and
      ($reset < 0 or $reset == $end)
   then ($q.credits_used / 100) * ($end - $start) / ($source - $start)
   else null end) as $estimate |
  if $percent != null and ($percent | isfinite | not) then
    error("Allocation percentage exceeds supported range")
  else {
    version: 1, hostname: $hostname, contextKey: $key, accountId: $account,
    status: "fresh", fetchedEpoch: $now, nextAttemptEpoch: ($now + $interval),
    freshUntil: ([$now + $interval, (if $reset < 0 then $end else $reset end)] | min),
    forecastUntil: ([$now + $interval, $source + $interval,
      (if $reset < 0 then $end else $reset end)] | min),
    consumptionUsd: ($q.credits_used / 100),
    allocationUsd: (if $q.entitlement == null then null else $q.entitlement / 100 end),
    unlimited: $q.unlimited,
    text: (($q.credits_used / 100 | money) +
      (if $allocated then " (~" + ($percent | round | tostring) + "%)" else "" end)),
    forecast: (if $estimate == null then "?" else ($estimate | money) end),
    forecastState: (
      if $estimate == null then "unknown"
      elif $estimate <= ($q.entitlement / 100) * 0.8 then "green"
      elif $estimate <= $q.entitlement / 100 then "yellow"
      else "red" end),
    diagnostic: "-"
  } end
elif $mode == "failure" then {
  version: 1, hostname: $hostname, contextKey: $key, status: "unavailable",
  fetchedEpoch: $now, freshUntil: 0, forecastUntil: 0, nextAttemptEpoch: $retry,
  text: "unavailable", forecast: "?", forecastState: "unknown", diagnostic: $diagnostic
}
elif $mode == "view" then
  [.version, .contextKey, .status, .fetchedEpoch, .freshUntil, .forecastUntil,
    .nextAttemptEpoch, .text, .forecast, .forecastState, .diagnostic] | @tsv
elif $mode == "schedule" then
  if valid_cache($hostname; $key)
  then .nextAttemptEpoch else error("Invalid cached identity or schedule") end
elif $mode == "example" then
  {quota_snapshots: {premium_interactions: {
    token_based_billing: true, has_quota: true, unlimited: false,
    credits_used: ($estimate * 50), entitlement: 1000000
  }}}
else error("Unknown Copilot filter mode") end
