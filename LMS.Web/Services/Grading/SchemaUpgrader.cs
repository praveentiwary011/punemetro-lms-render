using LMS.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Services.Grading;

/// <summary>Idempotent additive schema upgrade for the subjective-grading module, so an
/// existing database gains the new columns/tables without a seed-version rebuild (data preserved).
/// On a fresh database EnsureCreated already made these, so every statement here is a no-op
/// (guarded per-statement). SQLite is the default/dev provider; SQL Server variants included.</summary>
public static class SchemaUpgrader
{
    public static void EnsureGradingSchema(AppDbContext db, ILogger log)
    {
        // Only the SQLite/SQL Server providers can have a pre-grading database needing this
        // additive upgrade. PostgreSQL/MySQL ship with the grading feature, so a fresh
        // EnsureCreated already made every column/table — nothing to patch.
        if (!db.Database.IsSqlite() && !db.Database.IsSqlServer()) return;

        var sqlite = db.Database.IsSqlite();
        string blob = sqlite ? "BLOB" : "VARBINARY(MAX)";
        string txt = sqlite ? "TEXT" : "NVARCHAR(MAX)";
        string real = sqlite ? "REAL" : "FLOAT";
        string intc = sqlite ? "INTEGER" : "INT";
        string dt = sqlite ? "TEXT" : "DATETIME2";
        string pk = sqlite ? "INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT" : "INT NOT NULL IDENTITY PRIMARY KEY";

        // Additive columns (may already exist → guarded). "ALTER TABLE t ADD col type"
        // is valid on both SQLite and SQL Server (no COLUMN keyword).
        Run(db, log, $"ALTER TABLE Questions ADD RubricText {txt} NULL");
        Run(db, log, $"ALTER TABLE Questions ADD ReferenceAnswer {txt} NULL");
        Run(db, log, $"ALTER TABLE QuizAnswers ADD GradingPending {intc} NOT NULL DEFAULT 0");
        Run(db, log, $"ALTER TABLE Quizzes ADD GeneratedByAi {intc} NOT NULL DEFAULT 0");
        Run(db, log, $"ALTER TABLE Lessons ADD ExtractedText {txt} NULL");

        Run(db, log, $@"CREATE TABLE IF NOT EXISTS SsoConfigurations (
            Id {pk},
            OrganisationId {intc} NOT NULL,
            Protocol {intc} NOT NULL,
            IsEnabled {intc} NOT NULL,
            DisplayName {txt} NOT NULL,
            Authority {txt} NOT NULL,
            ClientId {txt} NOT NULL,
            ClientSecretProtected {txt} NULL,
            EmailDomains {txt} NOT NULL,
            RoleClaimName {txt} NULL,
            RoleMappings {txt} NULL,
            JitProvisioning {intc} NOT NULL,
            DefaultRole {txt} NOT NULL,
            AllowLocalPassword {intc} NOT NULL,
            UpdatedAt {dt} NOT NULL)", ifNotExistsHandled: sqlite);

        Run(db, log, $@"CREATE TABLE IF NOT EXISTS EmailOutbox (
            Id {pk},
            ToAddress {txt} NOT NULL,
            ToName {txt} NOT NULL,
            Subject {txt} NOT NULL,
            HtmlBody {txt} NOT NULL,
            Kind {intc} NOT NULL,
            DedupeKey {txt} NULL,
            OrganisationId {intc} NULL,
            CreatedAt {dt} NOT NULL,
            SentAt {dt} NULL,
            Attempts {intc} NOT NULL DEFAULT 0,
            LastAttemptAt {dt} NULL,
            LastError {txt} NULL)", ifNotExistsHandled: sqlite);

        Run(db, log, $@"CREATE TABLE IF NOT EXISTS OrganisationMailSettings (
            Id {pk},
            OrganisationId {intc} NOT NULL,
            IsEnabled {intc} NOT NULL,
            Host {txt} NOT NULL,
            Port {intc} NOT NULL,
            UseStartTls {intc} NOT NULL,
            User {txt} NOT NULL,
            PasswordProtected {txt} NULL,
            FromAddress {txt} NOT NULL,
            FromName {txt} NOT NULL,
            BaseUrl {txt} NOT NULL,
            UpdatedAt {dt} NOT NULL)", ifNotExistsHandled: sqlite);

        Run(db, log, $@"CREATE TABLE IF NOT EXISTS CourseEditRequests (
            Id {pk},
            CourseId {intc} NOT NULL,
            TrainerId {txt} NOT NULL,
            Reason {txt} NOT NULL,
            Status {intc} NOT NULL,
            DecisionNote {txt} NULL,
            DecidedById {txt} NULL,
            RequestedAt {dt} NOT NULL,
            DecidedAt {dt} NULL)", ifNotExistsHandled: sqlite);

        Run(db, log, $@"CREATE TABLE IF NOT EXISTS KnowledgeChunks (
            Id {pk},
            OrganisationId {intc} NOT NULL,
            CourseId {intc} NULL,
            SourceType {intc} NOT NULL,
            SourceRef {txt} NOT NULL,
            SourceLabel {txt} NOT NULL,
            Text {txt} NOT NULL,
            Embedding {blob} NOT NULL,
            CreatedAt {dt} NOT NULL)", ifNotExistsHandled: sqlite);

        Run(db, log, $@"CREATE TABLE IF NOT EXISTS SubjectiveGradeResults (
            Id {pk},
            QuizAnswerId {intc} NOT NULL,
            Score {real} NOT NULL,
            MaxScore {real} NOT NULL,
            Verdict {txt} NULL,
            Confidence {intc} NOT NULL,
            Feedback {txt} NULL,
            Citations {txt} NULL,
            Model {txt} NULL,
            Mode {intc} NOT NULL,
            RetrievedChunkLabels {txt} NULL,
            RawResult {txt} NULL,
            NeedsReview {intc} NOT NULL,
            GradedAt {dt} NOT NULL)", ifNotExistsHandled: sqlite);

        Run(db, log, $@"CREATE TABLE IF NOT EXISTS SubjectiveGradingReviews (
            Id {pk},
            QuizAnswerId {intc} NOT NULL,
            ProposedScore {real} NOT NULL,
            ResolvedScore {real} NULL,
            ReviewerUserId {txt} NULL,
            TrainerFeedback {txt} NULL,
            Status {intc} NOT NULL,
            CreatedAt {dt} NOT NULL,
            ResolvedAt {dt} NULL)", ifNotExistsHandled: sqlite);

        Run(db, log, "CREATE INDEX IF NOT EXISTS IX_KnowledgeChunks_Org_Course ON KnowledgeChunks (OrganisationId, CourseId)", ifNotExistsHandled: true);
        Run(db, log, "CREATE INDEX IF NOT EXISTS IX_CourseEditRequests_Course_Trainer_Status ON CourseEditRequests (CourseId, TrainerId, Status)", ifNotExistsHandled: true);
        Run(db, log, "CREATE UNIQUE INDEX IF NOT EXISTS IX_OrganisationMailSettings_Org ON OrganisationMailSettings (OrganisationId)", ifNotExistsHandled: true);
        Run(db, log, "CREATE UNIQUE INDEX IF NOT EXISTS IX_EmailOutbox_DedupeKey ON EmailOutbox (DedupeKey)", ifNotExistsHandled: true);
        Run(db, log, "CREATE INDEX IF NOT EXISTS IX_EmailOutbox_Sent_Created ON EmailOutbox (SentAt, CreatedAt)", ifNotExistsHandled: true);
    }

    private static void Run(AppDbContext db, ILogger log, string sql, bool ifNotExistsHandled = false)
    {
        try { db.Database.ExecuteSqlRaw(sql); }
        catch (Exception ex)
        {
            // Column/table already present, or "IF NOT EXISTS" unsupported on this provider:
            // both are benign here (fresh DBs are created by EnsureCreated).
            if (!ifNotExistsHandled)
                log.LogDebug("Grading schema upgrade statement skipped: {Msg}", ex.Message);
        }
    }
}
