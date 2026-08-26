using Microsoft.AspNetCore.Identity;

namespace MVTeaches.Infrastructure.Identity;

/// <summary>
/// Technical Study §7.1: users(id, email, phone, password_hash, phone_verified,
/// mfa, ..., country_id). <see cref="CountryId"/> is a SERVER-SIDE state, never
/// read from the request (D-07) — see §11 of the master engineering prompt:
/// "Never trust ... posted country IDs". It is set once at OTP-verified
/// registration and never accepted as client input afterward.
///
/// Uses a `long` key (not the Identity default GUID/string) because every
/// foreign key throughout the schema (guardian_id, teacher_id references,
/// audit_logs.performed_by, ...) is documented as `bigint`.
/// </summary>
public class ApplicationUser : IdentityUser<long>
{
    /// <summary>D-07: derived once from the verified phone's country code at
    /// registration; a server-side fact, immutable by any client request.</summary>
    public int? CountryId { get; set; }
}

public class ApplicationRole : IdentityRole<long>
{
    public ApplicationRole() { }
    public ApplicationRole(string roleName) : base(roleName) { }
}
