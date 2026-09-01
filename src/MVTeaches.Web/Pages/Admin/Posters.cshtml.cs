using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.Files;
using MVTeaches.Domain.Catalog;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Admin;

/// <summary>
/// Owner decision 2026-09-01: the centre publishes offer posters to students
/// — image, title, details underneath, shown or hidden, in a chosen order,
/// optionally tied to a level or a package.
///
/// A poster advertises and nothing else. No price lives here, no eligibility
/// is granted here, and no purchase, subscription or payment path reads this
/// table. A student still buys through the pricing plans published for their
/// own level, under exactly the rules that were already in place.
///
/// Re-uploading an image replaces the old one: the poster is pointed at the
/// new file and the displaced file is deleted, so the store never fills up
/// with every version a poster has ever had. That was an explicit ask.
/// </summary>
[Authorize(Roles = RoleNames.Admin + "," + RoleNames.SystemAdmin)]
public class PostersModel : PageModel
{
    private readonly MvTeachesDbContext _db;
    private readonly IFileStorageService _files;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IClock _clock;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public PostersModel(MvTeachesDbContext db, IFileStorageService files, UserManager<ApplicationUser> userManager,
        IClock clock, IStringLocalizer<SharedResource> localizer)
    {
        _db = db;
        _files = files;
        _userManager = userManager;
        _clock = clock;
        _localizer = localizer;
    }

    public record PosterRow(long Id, string Title, string? Details, long? ImageFileId, bool IsActive,
        int SortOrder, string? LevelCode, string? PlanLabel);

    public IReadOnlyList<PosterRow> Posters { get; set; } = Array.Empty<PosterRow>();
    public IReadOnlyList<Level> Levels { get; set; } = Array.Empty<Level>();
    public IReadOnlyList<PricingPlan> Plans { get; set; } = Array.Empty<PricingPlan>();

    /// <summary>Set when the admin pressed "Edit" on a row — the form below
    /// then updates that poster instead of creating another one.</summary>
    [BindProperty(SupportsGet = true, Name = "editId")]
    public long? EditId { get; set; }

    [BindProperty]
    public PosterInput Input { get; set; } = new();

    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public class PosterInput
    {
        public long? Id { get; set; }

        [Required(ErrorMessage = "Give the poster a title.")]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [StringLength(2000)]
        public string? Details { get; set; }

        public bool IsActive { get; set; } = true;

        [Range(0, 999)]
        public int SortOrder { get; set; }

        public int? LevelId { get; set; }
        public long? PricingPlanId { get; set; }

        /// <summary>Optional on every save. Leaving it empty on an edit keeps
        /// the image the poster already has.</summary>
        public IFormFile? Image { get; set; }
    }

    public async Task OnGetAsync()
    {
        await LoadAsync();

        if (EditId is null)
        {
            Input.SortOrder = Posters.Count == 0 ? 0 : Posters.Max(p => p.SortOrder) + 1;
            return;
        }

        var poster = await _db.PromotionalPosters.AsNoTracking().FirstOrDefaultAsync(p => p.Id == EditId.Value);
        if (poster is null)
        {
            EditId = null;
            return;
        }

        Input = new PosterInput
        {
            Id = poster.Id,
            Title = poster.Title,
            Details = poster.Details,
            IsActive = poster.IsActive,
            SortOrder = poster.SortOrder,
            LevelId = poster.LevelId,
            PricingPlanId = poster.PricingPlanId,
        };
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        ModelState.Clear();
        if (!TryValidateModel(Input, nameof(Input)))
        {
            await LoadAsync();
            return Page();
        }

        var actingUserId = long.Parse(_userManager.GetUserId(User)!);
        var now = _clock.GetCurrentInstant();

        PromotionalPoster poster;
        if (Input.Id is null)
        {
            poster = new PromotionalPoster(Input.Title, Input.Details, Input.IsActive, Input.SortOrder,
                Input.LevelId, Input.PricingPlanId, actingUserId, now);
            _db.PromotionalPosters.Add(poster);
        }
        else
        {
            var existing = await _db.PromotionalPosters.FirstOrDefaultAsync(p => p.Id == Input.Id.Value);
            if (existing is null)
            {
                ErrorMessage = _localizer["That poster no longer exists."].Value;
                await LoadAsync();
                return Page();
            }

            existing.Update(Input.Title, Input.Details, Input.IsActive, Input.SortOrder,
                Input.LevelId, Input.PricingPlanId, now);
            poster = existing;
        }

        long? displacedImageId = null;
        if (Input.Image is not null && Input.Image.Length > 0)
        {
            await using var stream = Input.Image.OpenReadStream();
            var upload = await _files.SaveAsync(stream, nameof(MVTeaches.Domain.Files.FilePurpose.PromotionalPoster),
                Input.Image.FileName, actingUserId, HttpContext.RequestAborted);

            if (upload.Outcome != SaveUploadOutcome.Saved)
            {
                ErrorMessage = upload.Outcome switch
                {
                    SaveUploadOutcome.RejectedContentType => _localizer["The image must be a JPEG or PNG file."].Value,
                    SaveUploadOutcome.RejectedTooLarge => _localizer["The image file is too large."].Value,
                    _ => _localizer["The image could not be uploaded."].Value,
                };
                await LoadAsync();
                return Page();
            }

            displacedImageId = poster.ReplaceImage(upload.DocumentId!.Value, now);
        }

        await _db.SaveChangesAsync(HttpContext.RequestAborted);

        // Only after the poster row safely points at the new file. Deleting
        // first would leave a poster pointing at bytes that no longer exist if
        // the save then failed.
        if (displacedImageId is not null)
        {
            await _files.DeleteAsync(displacedImageId.Value, HttpContext.RequestAborted);
        }

        StatusMessage = Input.Id is null
            ? _localizer["Poster created."].Value
            : _localizer["Poster updated."].Value;

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(long posterId)
    {
        var poster = await _db.PromotionalPosters.FirstOrDefaultAsync(p => p.Id == posterId);
        if (poster is not null)
        {
            poster.Update(poster.Title, poster.Details, !poster.IsActive, poster.SortOrder,
                poster.LevelId, poster.PricingPlanId, _clock.GetCurrentInstant());
            await _db.SaveChangesAsync(HttpContext.RequestAborted);
            StatusMessage = poster.IsActive
                ? _localizer["The poster is now visible to students."].Value
                : _localizer["The poster is hidden from students."].Value;
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Levels = await _db.Levels.Where(l => l.IsActive).OrderBy(l => l.SortOrder).ToListAsync();
        Plans = await _db.PricingPlans.Where(p => p.IsActive).OrderBy(p => p.LevelId).ToListAsync();

        var levelCodes = await _db.Levels.ToDictionaryAsync(l => l.Id, l => l.Code);
        var courseNames = await _db.Courses.ToDictionaryAsync(c => c.Id, c => c.NameEn);
        // Built in memory, not in the query: the label needs two lookup
        // dictionaries that only exist on this side of the wire.
        var planLabels = (await _db.PricingPlans.AsNoTracking().ToListAsync())
            .ToDictionary(
                plan => plan.Id,
                plan => courseNames.GetValueOrDefault(plan.CourseId, "?") + " / "
                    + (plan.LevelId is null ? "—" : levelCodes.GetValueOrDefault(plan.LevelId.Value, "?")));

        Posters = (await _db.PromotionalPosters.AsNoTracking()
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync())
            .Select(p => new PosterRow(p.Id, p.Title, p.Details, p.ImageFileId, p.IsActive, p.SortOrder,
                p.LevelId is null ? null : levelCodes.GetValueOrDefault(p.LevelId.Value),
                p.PricingPlanId is null ? null : planLabels.GetValueOrDefault(p.PricingPlanId.Value)))
            .ToList();
    }

    public string PlanLabel(PricingPlan plan) =>
        $"{plan.SessionsCount} × {plan.MinutesTotal / Math.Max(1, plan.SessionsCount)} min — {plan.Amount.Amount} {plan.Amount.Currency}";
}
