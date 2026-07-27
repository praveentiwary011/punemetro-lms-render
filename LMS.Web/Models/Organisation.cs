using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LMS.Web.Models;

/// <summary>A tenant organisation onboarded onto the platform. Users and courses
/// belong to an organisation; a null OrganisationId means platform-wide/shared.</summary>
public class Organisation
{
    public int Id { get; set; }
    [Required, MaxLength(150)]
    public string Name { get; set; } = "";
    /// <summary>Short unique code used as the tenant identifier (e.g. PUNEMETRO).</summary>
    [Required, MaxLength(20)]
    public string Code { get; set; } = "";
    [MaxLength(300)]
    public string? Address { get; set; }
    [MaxLength(150)]
    public string? ContactEmail { get; set; }
    [MaxLength(30)]
    public string? ContactPhone { get; set; }
    /// <summary>Tenant logo shown in the sidebar/header brand for this organisation's
    /// users (path under wwwroot, e.g. /uploads/logos/… ). Null = platform default logo.</summary>
    [MaxLength(300)]
    public string? LogoUrl { get; set; }
    /// <summary>The staff member selected to sign this organisation's certificates
    /// (Training Director slot). Their uploaded signature image is rendered on the
    /// certificate; placeholders are used until a signatory/signature is set.</summary>
    public string? CertificateSignatoryId { get; set; }
    public ApplicationUser? CertificateSignatory { get; set; }
    /// <summary>Deactivated organisations keep their data but their users cannot sign in.</summary>
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ApplicationUser> Users { get; set; } = new List<ApplicationUser>();
    public ICollection<Course> Courses { get; set; } = new List<Course>();
    public ICollection<OrganisationRole> Roles { get; set; } = new List<OrganisationRole>();
    public ICollection<TrainingLocation> Locations { get; set; } = new List<TrainingLocation>();
    public ICollection<SubscriptionLicense> Licenses { get; set; } = new List<SubscriptionLicense>();
}

/// <summary>How a subscription license's validity was entered — exactly one option
/// per license: an explicit date range, a number of months, a number of days
/// (months/days run from the start date), or a perpetual license that never expires.</summary>
public enum LicenseValidityType { DateRange = 0, Months = 1, Days = 2, NeverExpires = 3 }

/// <summary>A subscription licensing period for a tenant organisation. An organisation
/// is operational only while a license covers the current date (the platform owner's
/// organisation is exempt). Rows are immutable — renewals append new periods, giving a
/// full licensing history; creation is audit-logged.</summary>
public class SubscriptionLicense
{
    /// <summary>Sentinel end date used for perpetual ("never expires") licenses so
    /// that ordinary "does a license cover today?" date comparisons keep working
    /// unchanged — a perpetual license always has the latest end date.</summary>
    public static readonly DateTime PerpetualEndDate = new DateTime(9999, 12, 31);

    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public Organisation? Organisation { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    /// <summary>Which of the entry options was used.</summary>
    public LicenseValidityType ValidityType { get; set; }
    /// <summary>True when this is a perpetual license that never expires.</summary>
    [NotMapped]
    public bool IsPerpetual => ValidityType == LicenseValidityType.NeverExpires;
    /// <summary>The entered months/days count (null for date-range entry).</summary>
    public int? ValidityValue { get; set; }
    /// <summary>Optional commercial reference (PO / invoice / contract number).</summary>
    [MaxLength(100)]
    public string? Reference { get; set; }
    public string CreatedById { get; set; } = "";
    public ApplicationUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>When the expiry reminder was last sent to the tenant's Admins —
    /// first at two months before expiry, then weekly (see LicenseExpiryNotifier).</summary>
    public DateTime? LastExpiryNotifiedAt { get; set; }
}

/// <summary>A training venue + room defined for an organisation (captured at client
/// onboarding). Offered as suggestions wherever training venues are entered,
/// e.g. the Batch Set-up form.</summary>
public class TrainingLocation
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public Organisation? Organisation { get; set; }
    /// <summary>Venue/centre, e.g. "Range Hills Training Centre".</summary>
    [Required, MaxLength(200)]
    public string Name { get; set; } = "";
    /// <summary>Room details within the venue, e.g. "Room 2 · projector · 30 seats".</summary>
    [MaxLength(200)]
    public string? Room { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A custom role defined for one organisation (by the Super User or the
/// organisation's own Admin). Each is backed by an ASP.NET Identity role of the same
/// name so it can be assigned to that organisation's users, and is MAPPED to one of
/// the four platform roles (Student/Instructor/Principal/Admin): a user holding the
/// custom role receives the mapped platform role's capabilities at authorisation time
/// (see MappedRolesClaimsFactory), while the custom name carries the organisation's
/// own terminology.</summary>
public class OrganisationRole
{
    public int Id { get; set; }
    public int OrganisationId { get; set; }
    public Organisation? Organisation { get; set; }
    [Required, MaxLength(80)]
    public string Name { get; set; } = "";
    [MaxLength(200)]
    public string? Description { get; set; }
    /// <summary>The platform role this custom role is tagged to — one of
    /// Student (Trainee), Instructor (Trainer), Principal or Admin.</summary>
    [Required, MaxLength(20)]
    public string MapsToRole { get; set; } = "Student";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
