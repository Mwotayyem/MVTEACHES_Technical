using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using MVTeaches.Application.People;
using MVTeaches.Domain.Catalog;
using MVTeaches.Infrastructure.Identity;
using MVTeaches.Infrastructure.Persistence;
using MVTeaches.Web.Resources;
using NodaTime;

namespace MVTeaches.Web.Pages.Account;

/// <summary>
/// Owner decision 2026-09-04: families sign themselves up, instead of every
/// registration going through an admin typing it in from a phone call.
///
/// <para>Two paths, chosen by the person rather than guessed from a birth date:
/// a GUARDIAN registering to manage their children, or an ADULT STUDENT
/// registering to study themselves. The difference matters and is not
/// cosmetic — a student with a guardian linked may not buy their own packages
/// (owner decision 2026-09-04), so which of these two someone picks decides
/// who pays. Asking is honest; inferring it from an age would be inventing an
/// age rule the owner explicitly ruled out.</para>
///
/// <para><b>No OTP.</b> §7 documents phone + OTP over WhatsApp and that remains
/// genuinely unbuilt — no provider is configured, and nothing here pretends
/// otherwise or sends anything. A self-registered student lands in
/// PendingVerification exactly like an admin-registered one, so the centre
/// still confirms the family before they go Active. See
/// ISelfRegistrationService for why that is the honest interim position.</para>
///
/// <para>[AllowAnonymous] is the point of the page — it is the one place in the
/// application a person with no account may write anything, so every field it
/// accepts is validated server-side and the country is re-checked against the
/// active list rather than trusted from the posted id.</para>
/// </summary>
[AllowAnonymous]
public class RegisterModel : PageModel
{
    private readonly ISelfRegistrationService _selfRegistration;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly MvTeachesDbContext _db;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public RegisterModel(ISelfRegistrationService selfRegistration, SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager, MvTeachesDbContext db, IStringLocalizer<SharedResource> localizer)
    {
        _selfRegistration = selfRegistration;
        _signInManager = signInManager;
        _userManager = userManager;
        _db = db;
        _localizer = localizer;
    }

    /// <summary>Which of the two accounts is being created. Bound from the
    /// form so a failed submission returns to the same panel rather than
    /// dumping the person back at the choice.</summary>
    [BindProperty(SupportsGet = true)]
    public string? As { get; set; }

    public bool IsGuardianPath => string.Equals(As, "guardian", StringComparison.OrdinalIgnoreCase);
    public bool IsStudentPath => string.Equals(As, "student", StringComparison.OrdinalIgnoreCase);

    [BindProperty]
    public GuardianInput Guardian { get; set; } = new();

    [BindProperty]
    public StudentInput Student { get; set; } = new();

    public IReadOnlyList<Country> Countries { get; set; } = Array.Empty<Country>();

    public string? ErrorMessage { get; set; }

    public bool IsArabic => System.Globalization.CultureInfo.CurrentUICulture
        .TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);

    public string DisplayCountry(Country country) => IsArabic ? country.NameAr : country.NameEn;

    public class GuardianInput
    {
        [Required(ErrorMessage = "Enter the full name.")]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter an email address."), EmailAddress(ErrorMessage = "This is not a valid email address.")]
        public string Email { get; set; } = string.Empty;

        /// <summary>Mandatory: the centre must be able to reach whoever is
        /// responsible for a child. See ISelfRegistrationService.</summary>
        [Required(ErrorMessage = "Enter a phone number."), Phone(ErrorMessage = "This is not a valid phone number.")]
        public string PhoneNumber { get; set; } = string.Empty;

        [Required(ErrorMessage = "Choose a country.")]
        public int? CountryId { get; set; }

        // Length is stated here only so the person is told before submitting;
        // Identity's own configured policy is what actually decides, and its
        // errors are surfaced verbatim rather than being second-guessed.
        [Required(ErrorMessage = "Enter a password."), DataType(DataType.Password)]
        [MinLength(10, ErrorMessage = "The password must be at least 10 characters.")]
        public string Password { get; set; } = string.Empty;
    }

    public class StudentInput
    {
        [Required(ErrorMessage = "Enter the full name.")]
        public string FullName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter an email address."), EmailAddress(ErrorMessage = "This is not a valid email address.")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter a phone number."), Phone(ErrorMessage = "This is not a valid phone number.")]
        public string PhoneNumber { get; set; } = string.Empty;

        /// <summary>Nullable so an untouched date picker fails [Required]
        /// rather than passing as 0001-01-01 — the same binding trap already
        /// documented on the admin registration forms.</summary>
        [Required(ErrorMessage = "Enter the date of birth.")]
        public DateOnly? DateOfBirth { get; set; }

        [Required(ErrorMessage = "Choose a country.")]
        public int? CountryId { get; set; }

        [Required(ErrorMessage = "Enter a password."), DataType(DataType.Password)]
        [MinLength(10, ErrorMessage = "The password must be at least 10 characters.")]
        public string Password { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        // Somebody already signed in has no business creating a second account
        // from here — send them to their own landing page instead.
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Index");
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostGuardianAsync()
    {
        As = "guardian";
        ModelState.Clear();
        if (!TryValidateModel(Guardian, nameof(Guardian)))
        {
            await LoadAsync();
            return Page();
        }

        var result = await _selfRegistration.RegisterGuardianAsync(Guardian.Email, Guardian.Password,
            Guardian.FullName, Guardian.PhoneNumber, Guardian.CountryId!.Value, HttpContext.RequestAborted);

        if (result.Outcome != SelfRegisterOutcome.Registered)
        {
            ErrorMessage = DescribeFailure(result);
            await LoadAsync();
            return Page();
        }

        // Signed in immediately: the next thing a guardian must do is add their
        // children, and making them find the sign-in page first would be a step
        // with no purpose.
        return await SignInAndRedirectAsync(Guardian.Email, "/Guardian/MyChildren");
    }

    public async Task<IActionResult> OnPostStudentAsync()
    {
        As = "student";
        ModelState.Clear();
        if (!TryValidateModel(Student, nameof(Student)))
        {
            await LoadAsync();
            return Page();
        }

        var dob = Student.DateOfBirth!.Value;
        var result = await _selfRegistration.RegisterAdultStudentAsync(Student.Email, Student.Password,
            Student.FullName, new LocalDate(dob.Year, dob.Month, dob.Day), Student.PhoneNumber,
            Student.CountryId!.Value, HttpContext.RequestAborted);

        if (result.Outcome != SelfRegisterOutcome.Registered)
        {
            ErrorMessage = DescribeFailure(result);
            await LoadAsync();
            return Page();
        }

        // Straight to their own sessions page, which is where the "you need a
        // level before you can buy or book" message lives.
        return await SignInAndRedirectAsync(Student.Email, "/Student/MySessions");
    }

    private string DescribeFailure(SelfRegisterResult result) => result.Outcome switch
    {
        // Identity's own reasons, verbatim: "that email is already taken" is
        // something the person can act on, and paraphrasing it into a generic
        // failure would leave them retyping the same address forever.
        SelfRegisterOutcome.LoginFailed =>
            _localizer["Could not create the account: {0}", string.Join("; ", result.Errors ?? Array.Empty<string>())].Value,
        SelfRegisterOutcome.CountryNotAvailable =>
            _localizer["The centre does not currently operate in that country."].Value,
        SelfRegisterOutcome.PhoneRequired =>
            _localizer["Enter a phone number."].Value,
        _ => _localizer["Could not create the account."].Value,
    };

    private async Task<IActionResult> SignInAndRedirectAsync(string email, string destination)
    {
        var user = await _userManager.FindByEmailAsync(email);
        if (user is not null)
        {
            await _signInManager.SignInAsync(user, isPersistent: false);
        }

        return RedirectToPage(destination);
    }

    private async Task LoadAsync() =>
        Countries = await _db.Countries.Where(c => c.IsActive).OrderBy(c => c.NameEn).ToListAsync();
}
