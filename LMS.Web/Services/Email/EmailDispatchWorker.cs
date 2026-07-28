using LMS.Web.Data;
using LMS.Web.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace LMS.Web.Services.Email;

/// <summary>The only component that talks to SMTP. Drains <see cref="EmailOutbox"/>
/// on a short cycle, opening one connection per batch rather than per message.
///
/// A message that fails is left unsent with its error recorded and retried on later
/// ticks with a widening delay, up to <see cref="MaxAttempts"/>; after that it stays
/// in the outbox as a visible failure rather than being silently dropped. Nothing is
/// deleted, so the outbox doubles as the delivery record.</summary>
public class EmailDispatchWorker : BackgroundService
{
    private const int MaxAttempts = 6;
    private const int BatchSize = 50;
    private static readonly TimeSpan Cycle = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<EmailDispatchWorker> _log;
    public EmailDispatchWorker(IServiceScopeFactory scopes, ILogger<EmailDispatchWorker> log)
    { _scopes = scopes; _log = log; }

    /// <summary>Back-off before a failed message is retried: ~2, 4, 8, 16, 32 minutes.</summary>
    private static TimeSpan RetryDelay(int attempts) => TimeSpan.FromMinutes(Math.Pow(2, Math.Min(attempts, 5)));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await DrainAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "Email dispatch cycle failed"); }
            try { await Task.Delay(Cycle, ct); } catch (TaskCanceledException) { break; }
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await scope.ServiceProvider.GetRequiredService<MailSettingsStore>().LoadAsync();

        if (!settings.IsUsable) return;      // mail not configured yet — leave the queue alone

        var now = DateTime.UtcNow;
        var due = await db.EmailOutbox
            .Where(e => e.SentAt == null && e.Attempts < MaxAttempts)
            .OrderBy(e => e.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        // Respect the per-message back-off without expressing Math.Pow in SQL.
        due = due.Where(e => e.LastAttemptAt == null || now - e.LastAttemptAt >= RetryDelay(e.Attempts)).ToList();
        if (due.Count == 0) return;

        using var smtp = new SmtpClient();
        try
        {
            var socket = settings.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
            await smtp.ConnectAsync(settings.Host, settings.Port, socket, ct);
            if (!string.IsNullOrWhiteSpace(settings.User))
                await smtp.AuthenticateAsync(settings.User, settings.Password, ct);
        }
        catch (Exception ex)
        {
            // The server is unreachable or rejected us: record it against the batch and
            // try again next cycle rather than burning attempts one message at a time.
            _log.LogWarning(ex, "SMTP connection failed ({Host}:{Port})", settings.Host, settings.Port);
            foreach (var m in due) { m.Attempts++; m.LastAttemptAt = now; m.LastError = Short(ex); }
            await db.SaveChangesAsync(ct);
            return;
        }

        foreach (var m in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await smtp.SendAsync(Build(m, settings), ct);
                m.SentAt = DateTime.UtcNow;
                m.LastError = null;
            }
            catch (Exception ex)
            {
                m.LastError = Short(ex);
                _log.LogWarning(ex, "Email {Id} to {To} failed (attempt {N})", m.Id, m.ToAddress, m.Attempts + 1);
            }
            finally
            {
                m.Attempts++;
                m.LastAttemptAt = DateTime.UtcNow;
            }
        }

        try { await smtp.DisconnectAsync(true, ct); } catch { /* closing is best-effort */ }
        await db.SaveChangesAsync(ct);
    }

    private static MimeMessage Build(EmailOutbox m, MailSettings s)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(s.FromName ?? "", s.FromAddress));
        msg.To.Add(new MailboxAddress(m.ToName ?? "", m.ToAddress));
        msg.Subject = m.Subject;
        msg.Body = new BodyBuilder { HtmlBody = m.HtmlBody }.ToMessageBody();
        return msg;
    }

    private static string Short(Exception ex) =>
        (ex.Message ?? ex.GetType().Name) is { Length: > 400 } s ? s[..400] : ex.Message ?? ex.GetType().Name;
}
