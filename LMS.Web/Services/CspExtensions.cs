using Microsoft.AspNetCore.Http;

namespace LMS.Web.Services;

/// <summary>Per-request Content-Security-Policy nonce. The security-headers middleware
/// generates a fresh random nonce each request, stores it here, and names it in the
/// CSP <c>script-src</c>. Inline &lt;script&gt; blocks echo it via
/// <c>@Context.CspNonce()</c> so they are allowed while injected scripts (which cannot
/// know the nonce) are blocked.</summary>
public static class CspExtensions
{
    public const string NonceKey = "csp-nonce";

    public static string CspNonce(this HttpContext ctx) => ctx.Items[NonceKey] as string ?? "";
}
