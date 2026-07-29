using LMS.Web.Data;
using LMS.Web.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Email;

/// <summary>SMTP configuration, held in SiteSettings so a customer configures mail
/// from the admin screen rather than by editing files on the server. The password is
/// encrypted with the Data Protection API — the same treatment as the SSO client
/// secret — and is never returned to the browser.</summary>
public class MailSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";

    /// <summary>Absolute base URL used to build links in emails (e.g. https://lms.example.com).
    /// Without it a recipient gets relative links that go nowhere from their inbox.</summary>
    public string BaseUrl { get; set; } = "";

    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public class MailSettingsStore
{
    private const string Purpose = "LMS.smtp.password.v1";
    private readonly AppDbContext _db;
    private readonly IDataProtectionProvider _dp;

    public MailSettingsStore(AppDbContext db, IDataProtectionProvider dp) { _db = db; _dp = dp; }

    public const string KeyEnabled = "SmtpEnabled";
    public const string KeyHost = "SmtpHost";
    public const string KeyPort = "SmtpPort";
    public const string KeyStartTls = "SmtpUseStartTls";
    public const string KeyUser = "SmtpUser";
    public const string KeyPassword = "SmtpPasswordProtected";
    public const string KeyFromAddress = "MailFromAddress";
    public const string KeyFromName = "MailFromName";
    public const string KeyBaseUrl = "AppBaseUrl";

    public async Task<MailSettings> LoadAsync()
    {
        var map = await _db.SiteSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value);
        string V(string k) => map.TryGetValue(k, out var v) ? v ?? "" : "";

        var s = new MailSettings
        {
            Enabled = V(KeyEnabled) == "true",
            Host = V(KeyHost),
            Port = int.TryParse(V(KeyPort), out var p) ? p : 587,
            UseStartTls = V(KeyStartTls) != "false",
            User = V(KeyUser),
            FromAddress = V(KeyFromAddress),
            FromName = V(KeyFromName),
            BaseUrl = V(KeyBaseUrl).TrimEnd('/')
        };

        var enc = V(KeyPassword);
        if (!string.IsNullOrEmpty(enc))
        {
            // A key-ring change (or a restore onto another machine) makes the stored
            // ciphertext unreadable — treat that as "no password" rather than crashing
            // the mail worker, and let the admin re-enter it.
            try { s.Password = _dp.CreateProtector(Purpose).Unprotect(enc); } catch { s.Password = ""; }
        }
        return s;
    }

    /// <summary>Settings to send a message belonging to <paramref name="organisationId"/>:
    /// that tenant's own configuration when it has one that is enabled and usable, otherwise
    /// the platform default (§NTF-04). Platform-level mail — anything with no tenant — always
    /// uses the default. Falling back rather than failing means adding a tenant never silently
    /// stops its mail, while a tenant that needs its own sender identity can have one.</summary>
    public async Task<MailSettings> LoadForOrganisationAsync(int? organisationId)
    {
        var platform = await LoadAsync();
        if (organisationId == null) return platform;

        var row = await _db.OrganisationMailSettings.AsNoTracking()
            .FirstOrDefaultAsync(m => m.OrganisationId == organisationId);
        if (row == null || !row.IsEnabled) return platform;

        var s = new MailSettings
        {
            Enabled = true,
            Host = row.Host,
            Port = row.Port,
            UseStartTls = row.UseStartTls,
            User = row.User,
            FromAddress = row.FromAddress,
            FromName = row.FromName,
            // A tenant on the shared host need not restate the base URL.
            BaseUrl = string.IsNullOrWhiteSpace(row.BaseUrl) ? platform.BaseUrl : row.BaseUrl.TrimEnd('/')
        };
        if (!string.IsNullOrEmpty(row.PasswordProtected))
        {
            try { s.Password = _dp.CreateProtector(Purpose).Unprotect(row.PasswordProtected); }
            catch { s.Password = ""; }
        }

        // An incomplete tenant row must not black-hole that tenant's mail.
        return s.IsUsable ? s : platform;
    }

    public string Protect(string plain) => _dp.CreateProtector(Purpose).Protect(plain);

    public async Task SaveAsync(Dictionary<string, string> values)
    {
        foreach (var kv in values)
        {
            var row = await _db.SiteSettings.FindAsync(kv.Key);
            if (row != null) row.Value = kv.Value;
            else _db.SiteSettings.Add(new SiteSetting { Key = kv.Key, Value = kv.Value });
        }
        await _db.SaveChangesAsync();
    }
}
