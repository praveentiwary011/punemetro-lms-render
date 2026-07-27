using Microsoft.AspNetCore.DataProtection;

namespace LMS.Web.Services;

/// <summary>
/// Issues and verifies short-lived, user-bound access tokens for hosted Knowledge Hub
/// media (SEC-16 tokenised streaming). A token is a Data-Protection-signed payload
/// <c>userId|kind|id</c> with a lifetime; it is placed on the media URL when a hub page
/// renders. The <see cref="MediaController"/> refuses to serve a file unless the URL
/// carries a valid, unexpired token that matches the requesting user and the exact file,
/// so a copied link cannot be shared with another account and stops working after expiry.
/// </summary>
public class MediaTokenService
{
    public const string Purpose = "LMS.media.access.v1";
    public const string DocumentKind = "doc";
    public const string VideoKind = "vid";

    private readonly ITimeLimitedDataProtector _protector;

    public MediaTokenService(IDataProtectionProvider dp) =>
        _protector = dp.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    public string Issue(string userId, string kind, int id, TimeSpan lifetime) =>
        _protector.Protect($"{userId}|{kind}|{id}", lifetime);

    public bool Validate(string? token, string userId, string kind, int id)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try { return _protector.Unprotect(token) == $"{userId}|{kind}|{id}"; }
        catch { return false; } // tampered, expired, or minted with a different key
    }
}
