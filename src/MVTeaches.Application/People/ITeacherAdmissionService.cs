namespace MVTeaches.Application.People;

public enum RegisterTeacherOutcome { Registered, LoginFailed }
public record RegisterTeacherResult(RegisterTeacherOutcome Outcome, long? TeacherId = null, IReadOnlyList<string>? Errors = null);

/// <summary>
/// §9.1 (D-28: "created only by an Admin, no self-registration"). Mirrors
/// IStudentAdmissionService's guardian-registration path exactly (real
/// Identity login + role + domain entity) — split into its own interface
/// rather than folded into IStudentAdmissionService because a Teacher is a
/// distinct persona with its own required field (an IANA time zone, §14.4
/// rule 5), not a variant of student/guardian admission.
/// </summary>
public interface ITeacherAdmissionService
{
    Task<RegisterTeacherResult> RegisterTeacherAsync(string email, string password, string fullName,
        string timeZoneId, CancellationToken cancellationToken);

    Task DeactivateAsync(long teacherId, CancellationToken cancellationToken);

    Task ReactivateAsync(long teacherId, CancellationToken cancellationToken);
}
