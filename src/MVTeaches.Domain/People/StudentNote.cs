using NodaTime;

namespace MVTeaches.Domain.People;

/// <summary>What kind of thing this note is about, so a long list can be
/// read at a glance. Free-form text is still the point — the category only
/// sorts it.</summary>
public enum StudentNoteCategory
{
    Learning,
    Financial,
    Contact,
    Behaviour,
    Other,
}

/// <summary>
/// Owner decision 2026-09-01 (approved for a schema change): a free notes
/// log on a student, written by the centre.
///
/// A log of many dated notes rather than one editable field, because the
/// owner asked for the date and the author to be visible — a single
/// overwritten field loses both the moment a second person touches it.
///
/// INTERNAL. These are the centre's own working notes about a student and
/// are shown on the admin's student file only. No student-facing or
/// guardian-facing screen reads this table, and no default anywhere makes
/// one visible; that would be a separate, explicit decision.
///
/// Notes are never edited or deleted in place — a note that turns out to be
/// wrong is answered with another note, so the record of what the centre
/// believed and when stays honest.
/// </summary>
public class StudentNote
{
    public long Id { get; private set; }

    public long StudentId { get; private set; }

    public StudentNoteCategory Category { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public long AuthorUserId { get; private set; }

    /// <summary>The author's name as it was at the time of writing. Kept on
    /// the row rather than joined at read time so an old note still says who
    /// wrote it after that person's account is renamed or closed.</summary>
    public string AuthorName { get; private set; } = string.Empty;

    public Instant CreatedAtUtc { get; private set; }

    private StudentNote() { }

    public StudentNote(long studentId, StudentNoteCategory category, string text,
        long authorUserId, string authorName, Instant createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A note needs some text.", nameof(text));
        }

        StudentId = studentId;
        Category = category;
        Text = text.Trim();
        AuthorUserId = authorUserId;
        AuthorName = string.IsNullOrWhiteSpace(authorName) ? "—" : authorName.Trim();
        CreatedAtUtc = createdAtUtc;
    }
}
