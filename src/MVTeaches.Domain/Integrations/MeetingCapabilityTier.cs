namespace MVTeaches.Domain.Integrations;

/// <summary>
/// Provider-neutral capability tier used to decide whether MVTeaches must
/// impose its own duration cap before provisioning a meeting.
///
/// <see cref="Unknown"/> — not yet verified against the provider (or the
/// provider does not expose a reliable check, e.g. a consumer Google
/// account) — treated exactly like <see cref="Restricted"/> for enforcement
/// purposes; MVTeaches never assumes an unverified account is unrestricted
/// (owner: "do not guess that the account is paid").
///
/// <see cref="Restricted"/> — a real, provider-confirmed duration cap
/// applies (Zoom Basic's ~40 minutes; a free/consumer Google account).
///
/// <see cref="Full"/> — the provider has authoritatively confirmed no
/// MVTeaches-imposed cap is needed (a licensed Zoom host). No Google
/// account can reach this tier today because Google exposes no reliable
/// way for MVTeaches to confirm a consumer account's paid status.
/// </summary>
public enum MeetingCapabilityTier
{
    Unknown,
    Restricted,
    Full,
}
