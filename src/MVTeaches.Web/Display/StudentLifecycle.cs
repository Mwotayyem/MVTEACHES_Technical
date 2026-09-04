using MVTeaches.Domain.People;
using NodaTime;

namespace MVTeaches.Web.Display;

/// <summary>
/// Where a student stands right now, as one word an admin can scan a list by.
///
/// This is a DISPLAY classification and nothing else. It grants nothing,
/// blocks nothing, and is never consulted by a service: eligibility to attend
/// is still PaymentEligibilityService's decision (D-38), activation is still
/// the subscription's own status, and every rule stays exactly where it was.
/// It exists so that a register of two hundred rows can be read at a glance
/// instead of opened one profile at a time.
///
/// Deliberately built WITHOUT any invented threshold. The dashboard design
/// asks for "starting soon" and "ending soon" windows, but how many days
/// counts as soon is a business number, and business numbers on this project
/// belong to the admin in the control panel — not to me, and not hard-coded
/// here. So every state below is decided from a fact that is already true or
/// already false in the data:
///
///   • "ending soon" is not "expires within N days"; it is "what is left will
///     not cover the lesson they are already booked into", or "the package
///     expires before that lesson happens". Both are answerable from the rows
///     themselves, and both are the thing an admin would actually act on.
///   • "starting soon" is "paid and booked, but has not sat in a lesson yet".
///
/// If the owner later wants a real "expires within N days" warning, that N is
/// an admin setting to be added deliberately — see the note in the report.
/// </summary>
public enum StudentLifecycleState
{
    /// <summary>No package has ever been bought, or the account itself is not
    /// in a state where lessons can happen.</summary>
    Inactive,

    /// <summary>Money is the only thing in the way: a payment is waiting to be
    /// confirmed, or a package was bought and never paid for.</summary>
    PaymentDue,

    /// <summary>Paid and booked, but has not attended a lesson yet.</summary>
    StartingSoon,

    /// <summary>Running normally.</summary>
    Active,

    /// <summary>Running, but about to hit a wall: not enough balance left for
    /// the lesson they are already booked into, or the package expires before
    /// that lesson.</summary>
    EndingSoon,

    /// <summary>Has been through a package and has none running now.</summary>
    Completed,
}

/// <summary>
/// Owner report 2026-09-05: what this student's PACKAGE is doing, which is a
/// different question from what the student is doing. The register showed
/// "no package" beside "payment due — 60 JOD outstanding" for the same row,
/// because the package column was derived from the ACTIVE subscription alone
/// while the state chip was derived from the Draft one. Both were telling the
/// truth about different things; together they read as a contradiction.
/// </summary>
public enum StudentPackageStanding
{
    /// <summary>Never bought anything.</summary>
    None,

    /// <summary>Bought and awaiting payment - a Draft. It exists, it has a
    /// price, and it is why money is owed; it simply is not active yet.</summary>
    AwaitingPayment,

    /// <summary>Bought, paid, running.</summary>
    Active,

    /// <summary>Has had a package, has none running now.</summary>
    Finished,
}

/// <summary>The facts <see cref="StudentLifecycle.Classify"/> reads. Every one
/// of them is already loaded by the pages that call it; nothing here queries.</summary>
public readonly record struct StudentLifecycleFacts(
    StudentStatus AccountStatus,
    bool HasPaymentAwaitingConfirmation,
    bool HasUnpaidPackage,
    bool HasRunningPackage,
    bool HasEverHadPackage,
    int RemainingMinutes,
    bool HasAttendedALesson,
    int UpcomingLessonCount,
    int? NextLessonMinutes,
    LocalDate? RunningPackageExpiresOn,
    LocalDate? NextLessonDate);

public static class StudentLifecycle
{
    public static StudentLifecycleState Classify(StudentLifecycleFacts facts)
    {
        // An account that cannot sit in a lesson is not "active" whatever its
        // packages say. Checked first so nothing below can contradict it.
        if (facts.AccountStatus is StudentStatus.Suspended or StudentStatus.PaymentBlocked or StudentStatus.Migrated)
        {
            return StudentLifecycleState.Inactive;
        }

        // Money in the way outranks everything else, because it is the one
        // state where an admin has something concrete to do today.
        if (facts.HasPaymentAwaitingConfirmation || facts.HasUnpaidPackage)
        {
            return StudentLifecycleState.PaymentDue;
        }

        if (facts.HasRunningPackage)
        {
            var cannotAffordNextLesson = facts.NextLessonMinutes is { } minutes && facts.RemainingMinutes < minutes;
            var packageDiesFirst = facts.RunningPackageExpiresOn is { } expiry
                                   && facts.NextLessonDate is { } lessonDate
                                   && expiry < lessonDate;
            if (cannotAffordNextLesson || packageDiesFirst)
            {
                return StudentLifecycleState.EndingSoon;
            }

            if (!facts.HasAttendedALesson && facts.UpcomingLessonCount > 0)
            {
                return StudentLifecycleState.StartingSoon;
            }

            return StudentLifecycleState.Active;
        }

        return facts.HasEverHadPackage ? StudentLifecycleState.Completed : StudentLifecycleState.Inactive;
    }

    /// <summary>The colour family for a state, as the .app-status modifier the
    /// stylesheet already defines. Kept here so the register, the dashboard and
    /// any later screen cannot drift into showing the same state differently.</summary>
    public static string StatusClass(StudentLifecycleState state) => state switch
    {
        StudentLifecycleState.Active => "app-status is-success",
        StudentLifecycleState.StartingSoon => "app-status is-info",
        StudentLifecycleState.PaymentDue => "app-status is-danger",
        StudentLifecycleState.EndingSoon => "app-status is-warning",
        StudentLifecycleState.Completed => "app-status is-info",
        _ => "app-status is-muted",
    };

    /// <summary>Whether this state is one an admin should be doing something
    /// about. Used for the "needs attention" count on the dashboard, so that
    /// number and these badges can never disagree.</summary>
    public static bool NeedsAttention(StudentLifecycleState state) =>
        state is StudentLifecycleState.PaymentDue or StudentLifecycleState.EndingSoon;
}
