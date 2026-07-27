using LMS.Web.Models;
using LMS.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Data;

/// <summary>
/// Seeds the database with a rich, deterministic Pune Metro dataset:
/// 20+ rows per section so every table, chart and graph renders realistically.
/// </summary>
public static class DbSeeder
{
    /// <summary>Bump this to force a database rebuild with fresh seed data on next start.</summary>
    public const string SeedVersion = "punemetro-v23";

    /// <summary>Writes a small, valid single-page PDF (Helvetica text) without external libraries.</summary>
    private static void WriteSimplePdf(string path, string title, string[] lines)
    {
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

        var content = new System.Text.StringBuilder();
        content.Append($"BT /F1 16 Tf 50 800 Td ({Esc(title)}) Tj ET\n");
        content.Append("50 790 m 545 790 l S\n");
        var y = 762;
        foreach (var line in lines)
        {
            content.Append($"BT /F1 11 Tf 50 {y} Td ({Esc(line)}) Tj ET\n");
            y -= 18;
        }
        var stream = content.ToString();

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        var sb = new System.Text.StringBuilder();
        var offsets = new long[objects.Length + 1];
        sb.Append("%PDF-1.4\n");
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xrefPos = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objects.Length; i++)
            sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF");

        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(sb.ToString()));
    }

    private static readonly Random Rnd = new(42);

    public static async Task SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();

        foreach (var role in new[] { "SuperUser", "Admin", "Principal", "Instructor", "Student" })
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));

        if (await db.Courses.AnyAsync()) return; // already seeded

        // ---------------------------------------------------------------
        // ORGANISATIONS: AbsoluteSYS owns the platform (the Super User's home);
        // Pune Metro is the first onboarded client tenant.
        // ---------------------------------------------------------------
        var ownerOrg = new Organisation
        {
            Name = "AbsoluteSYS",
            Code = "ABSOLUTESYS",
            Address = "Pune, Maharashtra",
            ContactEmail = "support@absolutesys.com"
        };
        var org = new Organisation
        {
            Name = "Pune Metro Rail Corporation (Maha-Metro)",
            Code = "PUNEMETRO",
            LogoUrl = "/images/logo_new.png",   // tenant branding: Pune Metro's existing logo
            Address = "Metro House, Civil Court Interchange, Pune 411001",
            ContactEmail = "admin@punemetro.in",
            ContactPhone = "020-26051072"
        };
        db.Organisations.AddRange(ownerOrg, org);
        await db.SaveChangesAsync();

        // ---------------------------------------------------------------
        // USERS  (2 admins, 1 principal, 6 trainers, 20 trainees = 29)
        // ---------------------------------------------------------------
        async Task<ApplicationUser> CreateUser(string email, string name, string role, string department)
        {
            var user = new ApplicationUser { UserName = email, Email = email, FullName = name, EmailConfirmed = true, Department = department, OrganisationId = org.Id };
            await userManager.CreateAsync(user, "Pass@123");
            await userManager.AddToRoleAsync(user, role);
            return user;
        }

        var departments = new[] { "Operations", "Engineering & Maintenance", "Signalling & Telecom", "Customer Service", "Safety & Security" };

        // Platform Super User: onboards tenant organisations and manages their roles.
        // Holds Admin too (the Super User is a superset of Admin) and belongs to
        // AbsoluteSYS — the organisation that owns the application — never to a client tenant.
        var superUser = new ApplicationUser
        {
            UserName = "superuser@absolutesys.in", Email = "superuser@absolutesys.in",
            FullName = "Ashok Kadam", EmailConfirmed = true, Department = "Platform Operations",
            OrganisationId = ownerOrg.Id
        };
        await userManager.CreateAsync(superUser, "Pass@123");
        await userManager.AddToRolesAsync(superUser, new[] { "SuperUser", "Admin" });

        // Subscription licensing (LIC): the client tenant is operational only while
        // a license covers today. Pune Metro's current 12-month license is ~2 months
        // from expiry, so the Admin expiry-reminder schedule (first at T-2 months,
        // then weekly) demonstrates itself on first run.
        var licStart = DateTime.UtcNow.Date.AddMonths(-10);
        db.SubscriptionLicenses.Add(new SubscriptionLicense
        {
            OrganisationId = org.Id,
            StartDate = licStart,
            EndDate = licStart.AddMonths(12).AddDays(-1),
            ValidityType = LicenseValidityType.Months,
            ValidityValue = 12,
            Reference = "PO-2025-0114",
            CreatedById = superUser.Id
        });
        await db.SaveChangesAsync();

        var admin = await CreateUser("admin@punemetro.in", "Anil Deshmukh", "Admin", "HR & Administration");
        var admin2 = await CreateUser("kavita.r@punemetro.in", "Kavita Rane", "Admin", "HR & Administration");
        var principal = await CreateUser("principal@punemetro.in", "Dr. Sunita Kulkarni", "Principal", "Training Institute");
        // Multi-role test account: Principal + Trainer + Trainee
        await userManager.AddToRoleAsync(principal, "Instructor");
        await userManager.AddToRoleAsync(principal, "Student");

        // Certificates: the Principal signs as Training Director, with a real
        // (transparent-background) cursive signature image so certificates carry a
        // genuine signature. A real upload from the Users page / profile overrides it.
        principal.SignatureUrl = "/images/signatures/sig-sunita-kulkarni.svg";
        await userManager.UpdateAsync(principal);
        org.CertificateSignatoryId = principal.Id;
        await db.SaveChangesAsync();

        var trainerNames = new (string Email, string Name, string Dept)[]
        {
            ("trainer@punemetro.in", "Rajesh Patil", "Operations"),
            ("meera.joshi@punemetro.in", "Meera Joshi", "Customer Service"),
            ("vikram.s@punemetro.in", "Vikram Sathe", "Engineering & Maintenance"),
            ("anita.g@punemetro.in", "Anita Gokhale", "Signalling & Telecom"),
            ("suresh.m@punemetro.in", "Suresh More", "Safety & Security"),
            ("neha.d@punemetro.in", "Neha Dixit", "Operations")
        };
        var trainers = new List<ApplicationUser>();
        foreach (var t in trainerNames)
            trainers.Add(await CreateUser(t.Email, t.Name, "Instructor", t.Dept));
        await userManager.AddToRoleAsync(trainers[0], "Student"); // multi-role demo: trainer who also learns

        // Give each trainer a real (transparent-background) cursive signature image so
        // the Course Instructor slot on certificates carries a genuine signature.
        var trainerSignatures = new Dictionary<string, string>
        {
            ["Rajesh Patil"]  = "/images/signatures/sig-rajesh-patil.svg",
            ["Meera Joshi"]   = "/images/signatures/sig-meera-joshi.svg",
            ["Vikram Sathe"]  = "/images/signatures/sig-vikram-sathe.svg",
            ["Anita Gokhale"] = "/images/signatures/sig-anita-gokhale.svg",
            ["Suresh More"]   = "/images/signatures/sig-suresh-more.svg",
            ["Neha Dixit"]    = "/images/signatures/sig-neha-dixit.svg",
        };
        foreach (var tr in trainers)
            if (trainerSignatures.TryGetValue(tr.FullName, out var sig))
            {
                tr.SignatureUrl = sig;
                await userManager.UpdateAsync(tr);
            }

        // Example organisation-specific custom role (Admin can add more per tenant)
        await roleManager.CreateAsync(new IdentityRole("Safety Officer"));
        db.OrganisationRoles.Add(new OrganisationRole
        {
            OrganisationId = org.Id, Name = "Safety Officer",
            Description = "Leads safety drills and compliance walk-downs",
            MapsToRole = "Instructor"   // tagged to Trainer — holders inherit its capabilities
        });
        await userManager.AddToRoleAsync(trainers[4], "Safety Officer"); // Suresh More, Safety & Security

        // Training locations & rooms captured at client onboarding — suggested in Batch Set-up
        var locationDefs = new (string Name, string? Room)[]
        {
            ("Range Hills Training Centre", "Room 2 · projector · 30 seats"),
            ("Range Hills Training Centre", "Room 5 · simulator lab"),
            ("Civil Court Station", "Training Room 1 · smart board"),
            ("Hill View Depot", "Classroom A · 40 seats"),
            ("Vanaz Crew Room", "Crew Room · 20 seats"),
            ("PCMC Station", "Conference Room · video wall")
        };
        foreach (var (lName, lRoom) in locationDefs)
            db.TrainingLocations.Add(new TrainingLocation { OrganisationId = org.Id, Name = lName, Room = lRoom });

        var traineeNames = new[]
        {
            "Sameer Kale", "Priya Kapoor", "Omkar Pawar", "Aisha Shaikh", "Rohan Deshpande",
            "Sneha Kulkarni", "Akash Jadhav", "Pooja Bhosale", "Nikhil Gaikwad", "Divya Iyer",
            "Amit Shinde", "Rutuja Salunkhe", "Farhan Khan", "Manasi Apte", "Ganesh Chavan",
            "Ishita Verma", "Tushar Wagh", "Kirti Naik", "Sagar Kadam", "Ankita Thorat"
        };
        var trainees = new List<ApplicationUser>();
        for (int i = 0; i < traineeNames.Length; i++)
        {
            var email = i == 0 ? "trainee@punemetro.in" :
                traineeNames[i].ToLower().Split(' ') is var p ? $"{p[0]}.{p[1][..1]}@punemetro.in" : $"user{i}@punemetro.in";
            trainees.Add(await CreateUser(email, traineeNames[i], "Student", departments[i % departments.Length]));
        }
        var learners = new List<ApplicationUser>(trainees) { trainers[0] }; // trainer[0] learns too

        // Default tagged organisation roles (§ORG-05): the client organisation names
        // the four LMS roles in its own vocabulary — every seeded user is tagged to
        // an organisation role backed by its mapped built-in.
        foreach (var rn in new[] { "Trainee", "Trainer" })
            if (!await roleManager.RoleExistsAsync(rn))
                await roleManager.CreateAsync(new IdentityRole(rn));
        db.OrganisationRoles.AddRange(
            new OrganisationRole { OrganisationId = org.Id, Name = "Trainee", MapsToRole = "Student", Description = "Default organisation role tagged to the built-in Trainee LMS role" },
            new OrganisationRole { OrganisationId = org.Id, Name = "Trainer", MapsToRole = "Instructor", Description = "Default organisation role tagged to the built-in Trainer LMS role" },
            new OrganisationRole { OrganisationId = org.Id, Name = "Principal", MapsToRole = "Principal", Description = "Default organisation role tagged to the built-in Principal LMS role" },
            new OrganisationRole { OrganisationId = org.Id, Name = "Admin", MapsToRole = "Admin", Description = "Default organisation role tagged to the built-in Admin LMS role" });
        await db.SaveChangesAsync();
        foreach (var t in trainees) await userManager.AddToRoleAsync(t, "Trainee");
        foreach (var t in trainers) await userManager.AddToRoleAsync(t, "Trainer");
        await userManager.AddToRoleAsync(trainers[0], "Trainee");                      // multi-role: trainer who learns
        await userManager.AddToRolesAsync(principal, new[] { "Trainer", "Trainee" }); // multi-role principal

        // ---------------------------------------------------------------
        // CATEGORIES + 20 COURSES
        // ---------------------------------------------------------------
        var cats = departments.Select(d => new Category { Name = d }).ToList();
        var catLeadership = new Category { Name = "Leadership & Soft Skills" };
        cats.Add(catLeadership);
        db.Categories.AddRange(cats);

        var courseDefs = new (string Code, string Title, CourseKind Kind, int Cat, string Desc)[]
        {
            ("OPS101", "Metro Train Operations Fundamentals", CourseKind.RoleSpecific, 0, "Network overview, ATO/ATP operation, and depot-to-mainline workflows for the Purple and Aqua lines."),
            ("OPS210", "OCC & Traffic Management", CourseKind.RoleSpecific, 0, "Operations Control Centre roles, timetable regulation, and disruption handling."),
            ("OPS220", "Depot Operations & Stabling", CourseKind.Technical, 0, "Wake-up tests, stabling plans, and depot movement safety."),
            ("STN201", "Station Operations & Customer Service", CourseKind.RoleSpecific, 3, "AFC gates, PA/PIDS, crowd management, and passenger assistance."),
            ("STN230", "Crowd & Event Day Management", CourseKind.RoleSpecific, 3, "Peak surge plans, queuing layouts, and coordination with security."),
            ("SAF110", "Safety, Security & Emergency Preparedness", CourseKind.Compliance, 4, "Mandatory induction: fire, evacuation, traction power safety, and incident reporting."),
            ("SAF150", "Fire Safety & Evacuation Drills", CourseKind.Compliance, 4, "Alarm zones, evacuation routes, assembly points, and drill conduct."),
            ("SAF201", "First Response & Medical Emergencies", CourseKind.Compliance, 4, "First-responder duties, AED usage, and casualty handling on platforms."),
            ("ENG150", "Rolling Stock Maintenance Basics", CourseKind.Technical, 1, "Daily inspection, bogie and brake basics for 3-car aluminium trainsets."),
            ("ENG240", "Track & Civil Infrastructure Basics", CourseKind.Technical, 1, "Track geometry, viaduct inspection, and permanent-way safety."),
            ("ENG310", "HVAC & Auxiliary Systems", CourseKind.Technical, 1, "Saloon HVAC, auxiliary converters, and battery systems maintenance."),
            ("SIG110", "Signalling Fundamentals (CBTC)", CourseKind.Technical, 2, "Communications-based train control principles, movement authority, and failure modes."),
            ("SIG205", "Telecom & PIS Systems", CourseKind.Technical, 2, "Radio, fibre backbone, PIDS and CCTV system operation."),
            ("SIG260", "AFC System Maintenance", CourseKind.Technical, 2, "Gate arrays, validators, and NCMC acceptance testing."),
            ("CUS140", "Divyangjan & Accessibility Assistance", CourseKind.RoleSpecific, 3, "Accessibility features and correct assistance protocol at stations."),
            ("CUS180", "Multilingual Passenger Communication", CourseKind.RoleSpecific, 3, "Marathi–Hindi–English announcements and service-recovery phrasing."),
            ("LDR120", "Leadership Essentials for Supervisors", CourseKind.Leadership, 5, "Leading frontline teams, shift briefings, and performance conversations."),
            ("LDR210", "Conflict Resolution & Team Communication", CourseKind.Leadership, 5, "De-escalation, feedback models, and cross-department coordination."),
            ("ONB100", "Pune Metro Induction Programme", CourseKind.Onboarding, 0, "Organisation, culture, code of conduct, and service values for new joiners."),
            ("ONB110", "IT Systems & LMS Orientation", CourseKind.Onboarding, 0, "Email, HRMS, and learning platform orientation for new staff.")
        };

        var courses = new List<Course>();
        for (int i = 0; i < courseDefs.Length; i++)
        {
            var def = courseDefs[i];
            var content = SeedContent.Courses[def.Code];
            var course = new Course
            {
                Title = def.Title, Code = def.Code, Kind = def.Kind, Category = cats[def.Cat],
                OrganisationId = org.Id,
                InstructorId = trainers[i % trainers.Count].Id,
                Description = def.Desc,
                IsPublished = i != 10 && i != 19,   // two drafts
                StartDate = DateTime.UtcNow.AddDays(-60 + i * 2),
                EndDate = DateTime.UtcNow.AddDays(60 + i * 2),
                CreatedAt = DateTime.UtcNow.AddDays(-90 + i)
            };

            void AddModule(string title, string[] lessons, int order)
            {
                var module = new Module { Title = $"Module {order} — {title}", Order = order };
                for (int l = 0; l < lessons.Length; l++)
                {
                    module.Lessons.Add(new Lesson
                    {
                        Title = lessons[l], Type = LessonType.Text, Order = l + 1,
                        DurationMinutes = 10 + Rnd.Next(4) * 5,
                        Content = $"<p><strong>{lessons[l]}</strong></p>" +
                                  $"<p>This lesson is part of <em>{def.Title}</em> ({def.Code}) — {def.Desc}</p>" +
                                  "<p>Work through the material carefully; the checkpoint quiz and your duty procedures draw directly from this lesson. " +
                                  "Note down doubts and raise them in the course discussion forum or the next training session.</p>"
                    });
                }
                course.Modules.Add(module);
            }
            AddModule(content.Module1, content.Lessons1, 1);
            AddModule(content.Module2, content.Lessons2, 2);

            // Checkpoint quiz with course-specific questions
            var quiz = new Quiz
            {
                Title = $"{def.Code} Checkpoint", Description = "Covers both modules.",
                TimeLimitMinutes = 15, MaxAttempts = 2, PassingScore = 60,
                DueDate = DateTime.UtcNow.AddDays(20 + i)
            };
            var mcq = new Question { Text = content.Question, Type = QuestionType.MultipleChoice, Points = 2, Order = 1 };
            var options = new List<QuestionOption> { new() { Text = content.Answer, IsCorrect = true } };
            options.AddRange(content.Wrong.Select(w => new QuestionOption { Text = w }));
            foreach (var o in options.OrderBy(_ => Rnd.Next())) mcq.Options.Add(o);
            quiz.Questions.Add(mcq);
            quiz.Questions.Add(new Question
            {
                Text = "Official procedures taught in this course override informal shortcuts used on duty.",
                Type = QuestionType.TrueFalse, Points = 1, Order = 2,
                Options = { new QuestionOption { Text = "True", IsCorrect = true }, new QuestionOption { Text = "False" } }
            });
            quiz.Questions.Add(new Question
            {
                Text = "Enter the course code of this programme.",
                Type = QuestionType.ShortAnswer, Points = 1, Order = 3, AnswerKey = def.Code
            });
            course.Quizzes.Add(quiz);

            if (i % 5 == 0)
            {
                var self = new Quiz
                {
                    Title = $"{def.Code} Practice (Self Assessment)", Description = "Unlimited attempts — does not affect grades.",
                    TimeLimitMinutes = 10, MaxAttempts = 99, PassingScore = 0, IsSelfAssessment = true
                };
                self.Questions.Add(new Question
                {
                    Text = content.Question, Type = QuestionType.MultipleChoice, Points = 1, Order = 1,
                    Options =
                    {
                        new QuestionOption { Text = content.Answer, IsCorrect = true },
                        new QuestionOption { Text = content.Wrong[0] },
                        new QuestionOption { Text = content.Wrong[1] }
                    }
                });
                self.Questions.Add(new Question
                {
                    Text = "This practice quiz affects my final grade.", Type = QuestionType.TrueFalse, Points = 1, Order = 2,
                    Options = { new QuestionOption { Text = "True" }, new QuestionOption { Text = "False", IsCorrect = true } }
                });
                course.Quizzes.Add(self);
            }

            if (content.AssignmentTitle != null)
            {
                course.Assignments.Add(new Assignment
                {
                    Title = content.AssignmentTitle,
                    Description = content.AssignmentBrief ?? "",
                    DueDate = DateTime.UtcNow.AddDays(10 + i), MaxPoints = 100
                });
            }
            courses.Add(course);
        }
        // The Principal (Dr. Sunita Kulkarni) is the organisation's certificate signatory
        // (Training Director), so she does NOT instruct any course — this keeps the two
        // certificate signatures (Course Instructor vs Training Director) as two different
        // people on every certificate. She keeps the Instructor role for trainer-view testing.
        // (LDR210 therefore keeps its round-robin trainer assigned above.)

        // Courses that ship with a graded quiz/assignment require it to be passed for
        // completion (the course-setup "Require passing the quiz/assessment" flag).
        foreach (var c in courses)
            c.RequiresAssessment = c.Quizzes.Any(q => q.IsPublished && !q.IsSelfAssessment) || c.Assignments.Any();

        db.Courses.AddRange(courses);
        await db.SaveChangesAsync();

        // ---------------------------------------------------------------
        // ENROLLMENTS (each learner joins 5–8 courses spread over the last
        // ~6 months so the dashboard trend charts have data in every month;
        // ~38% completed, completion dated 1–4 weeks after enrollment)
        // ---------------------------------------------------------------
        var published = courses.Where(c => c.IsPublished).ToList();
        var enrollments = new List<Enrollment>();
        foreach (var learner in learners)
        {
            var count = 5 + Rnd.Next(4);
            foreach (var course in published.Where(c => c.InstructorId != learner.Id).OrderBy(_ => Rnd.Next()).Take(count))
            {
                var enrolledAt = DateTime.UtcNow.AddDays(-Rnd.Next(3, 170));
                var e = new Enrollment { CourseId = course.Id, StudentId = learner.Id, EnrolledAt = enrolledAt };
                var ageDays = (int)(DateTime.UtcNow - enrolledAt).TotalDays;
                if (ageDays > 10 && Rnd.NextDouble() < 0.38)
                {
                    e.Status = EnrollmentStatus.Completed;
                    e.FinalGrade = 60 + Rnd.Next(36) + Rnd.NextDouble();
                    e.CompletedAt = enrolledAt.AddDays(Rnd.Next(7, Math.Min(30, ageDays)));
                }
                enrollments.Add(e);
            }
        }
        // Principal as trainee: two enrollments (one completed) for the trainee-view test
        var principalCourses = published.Where(c => c.InstructorId != principal.Id).Take(2).ToList();
        if (principalCourses.Count == 2)
        {
            enrollments.Add(new Enrollment { CourseId = principalCourses[0].Id, StudentId = principal.Id, EnrolledAt = DateTime.UtcNow.AddDays(-20) });
            enrollments.Add(new Enrollment
            {
                CourseId = principalCourses[1].Id, StudentId = principal.Id, EnrolledAt = DateTime.UtcNow.AddDays(-40),
                Status = EnrollmentStatus.Completed, FinalGrade = 88, CompletedAt = DateTime.UtcNow.AddDays(-15)
            });
        }

        // Guarantee the demo trainee has at least one completed course (certificate, tracker, grades)
        var demoEnrollments = enrollments.Where(e => e.StudentId == trainees[0].Id).ToList();
        if (demoEnrollments.All(e => e.Status != EnrollmentStatus.Completed) && demoEnrollments.Count > 0)
        {
            var e = demoEnrollments[0];
            e.Status = EnrollmentStatus.Completed;
            e.FinalGrade = 85;
            e.CompletedAt = e.EnrolledAt.AddDays(14);
        }
        db.Enrollments.AddRange(enrollments);
        await db.SaveChangesAsync();

        // Certificates for completed enrollments
        foreach (var e in enrollments.Where(e => e.Status == EnrollmentStatus.Completed))
            db.Certificates.Add(new Certificate
            {
                EnrollmentId = e.Id,
                IssuedAt = e.CompletedAt!.Value,
                SerialNumber = $"CERT-{e.CompletedAt:yyyyMMdd}-{e.Id:D4}{Rnd.Next(10, 99)}"
            });

        // Lesson progress: completed => all lessons; active => partial
        var lessonsByCourse = courses.ToDictionary(c => c.Id, c => c.Modules.SelectMany(m => m.Lessons).ToList());
        foreach (var e in enrollments)
        {
            var lessons = lessonsByCourse[e.CourseId];
            var take = e.Status == EnrollmentStatus.Completed ? lessons.Count : Rnd.Next(0, lessons.Count + 1);
            foreach (var lesson in lessons.Take(take))
                db.LessonProgress.Add(new LessonProgress
                {
                    LessonId = lesson.Id, StudentId = e.StudentId,
                    CompletedAt = e.EnrolledAt.AddDays(Rnd.Next(1, 25))
                });
        }

        // Quiz attempts (~70% of enrollments attempt the checkpoint 1–2 times)
        var quizByCourse = courses.ToDictionary(c => c.Id, c => c.Quizzes.First(q => !q.IsSelfAssessment));
        foreach (var e in enrollments)
        {
            if (Rnd.NextDouble() > 0.70 && e.Status != EnrollmentStatus.Completed) continue;
            var quiz = quizByCourse[e.CourseId];
            var questions = quiz.Questions.OrderBy(q => q.Order).ToList();
            var maxScore = questions.Sum(q => q.Points);
            var passMark = maxScore * quiz.PassingScore / 100.0;
            var attemptsCount = 1 + (Rnd.NextDouble() < 0.3 ? 1 : 0);
            for (int a = 1; a <= attemptsCount; a++)
            {
                var when = e.EnrolledAt.AddDays(Rnd.Next(2, 28));
                // A COMPLETED enrollment must own a PASSING attempt: course completion is
                // gated on passing the assessment (LSN-04), so the seeded history has to
                // satisfy the same rule the runtime enforces — otherwise the demo dataset
                // contains certificates that could not have been issued.
                bool mustPass = e.Status == EnrollmentStatus.Completed && a == attemptsCount;
                var target = maxScore * (0.4 + Rnd.NextDouble() * 0.6);

                // Decide which questions this learner got right, then derive the score from
                // them, so the attempt reconciles question-by-question in the answer review
                // rather than carrying a header score with nothing behind it.
                var correct = new HashSet<int>();
                var awarded = 0.0;
                foreach (var q in questions.OrderBy(_ => Rnd.Next()))
                    if (awarded + q.Points <= target + 1e-9) { correct.Add(q.Order); awarded += q.Points; }
                if (mustPass)
                    foreach (var q in questions.OrderByDescending(x => x.Points))
                    {
                        if (awarded >= passMark) break;
                        if (correct.Add(q.Order)) awarded += q.Points;
                    }

                var attempt = new QuizAttempt
                {
                    QuizId = quiz.Id, StudentId = e.StudentId, AttemptNumber = a,
                    StartedAt = when, SubmittedAt = when.AddMinutes(12),
                    MaxScore = maxScore, Score = awarded
                };
                foreach (var q in questions)
                {
                    var right = correct.Contains(q.Order);
                    var answer = new QuizAnswer
                    {
                        QuestionId = q.Id,
                        PointsAwarded = right ? q.Points : 0
                    };
                    if (q.Type == QuestionType.MultipleChoice || q.Type == QuestionType.TrueFalse)
                        answer.SelectedOptionId = (right
                            ? q.Options.FirstOrDefault(o => o.IsCorrect)
                            : q.Options.Where(o => !o.IsCorrect).OrderBy(_ => Rnd.Next()).FirstOrDefault())?.Id;
                    else
                        answer.TextAnswer = right ? q.AnswerKey : "Not sure";
                    attempt.Answers.Add(answer);
                }
                db.QuizAttempts.Add(attempt);
            }
        }

        // Assignment submissions (queue for grading + graded history)
        foreach (var course in courses.Where(c => c.Assignments.Any()))
        {
            var assignment = course.Assignments.First();
            var courseEnrollments = enrollments.Where(e => e.CourseId == course.Id).Take(4).ToList();
            foreach (var e in courseEnrollments)
            {
                var graded = Rnd.NextDouble() < 0.5;
                db.Submissions.Add(new Submission
                {
                    AssignmentId = assignment.Id, StudentId = e.StudentId,
                    SubmittedAt = e.EnrolledAt.AddDays(Rnd.Next(3, 20)),
                    Text = $"Field report for {course.Code}: observations from my duty station with corrective actions and escalation notes.",
                    Grade = graded ? 60 + Rnd.Next(38) : null,
                    Feedback = graded ? "Good structure — add trainset/station identifiers next time." : null,
                    GradedAt = graded ? DateTime.UtcNow.AddDays(-Rnd.Next(1, 10)) : null
                });
            }
        }

        // Attendance: first 6 published courses × 5 recent dates
        foreach (var course in published.Take(6))
        {
            var courseLearners = enrollments.Where(e => e.CourseId == course.Id).Select(e => e.StudentId).ToList();
            for (int d = 1; d <= 5; d++)
            {
                var date = DateTime.UtcNow.AddDays(-d * 3).Date;
                foreach (var sid in courseLearners)
                {
                    var roll = Rnd.NextDouble();
                    db.AttendanceRecords.Add(new AttendanceRecord
                    {
                        CourseId = course.Id, StudentId = sid, Date = date,
                        Status = roll < 0.78 ? AttendanceStatus.Present : roll < 0.88 ? AttendanceStatus.Late : roll < 0.96 ? AttendanceStatus.Absent : AttendanceStatus.Excused
                    });
                }
            }
        }

        // ---------------------------------------------------------------
        // FEEDBACK (~45 rows)
        // ---------------------------------------------------------------
        var comments = new[]
        {
            "Very practical and relevant to daily duty.", "Excellent trainer — clear explanations.",
            "Good pace; more real incidents would help.", "The scenarios module was the best part.",
            "Quiz was fair and matched the content.", "Would like a refresher session every quarter.",
            "Slides could be more visual.", "Handouts were very useful on the job.",
            "Perfect for new joiners.", "More hands-on practice time please."
        };
        foreach (var e in enrollments.Where(_ => Rnd.NextDouble() < 0.45))
        {
            db.CourseFeedbacks.Add(new CourseFeedback
            {
                CourseId = e.CourseId, StudentId = e.StudentId,
                Rating = Rnd.NextDouble() < 0.6 ? 4 + Rnd.Next(2) : 3,
                Comments = comments[Rnd.Next(comments.Length)],
                SubmittedAt = DateTime.UtcNow.AddDays(-Rnd.Next(1, 40))
            });
        }

        // ---------------------------------------------------------------
        // TRAINING SESSIONS (22 upcoming, mixed modes)
        // ---------------------------------------------------------------
        var venues = new[] { "Range Hills Training Centre, Room 2", "Civil Court Station, Training Room", "Hill View Depot, Classroom A", "Vanaz Crew Room", "PCMC Station Conference Room" };
        for (int i = 0; i < 22; i++)
        {
            var course = published[i % published.Count];
            var online = i % 3 == 0;
            var start = DateTime.UtcNow.AddDays(1 + i * 2).Date.AddHours(9 + (i % 4) * 2);
            db.TrainingSessions.Add(new TrainingSession
            {
                Title = $"{course.Code}: {SeedContent.SessionTitles[i % SeedContent.SessionTitles.Length]}",
                CourseId = course.Id, TrainerId = course.InstructorId,
                Mode = online ? SessionMode.Online : SessionMode.Offline,
                Location = online ? "https://teams.microsoft.com/l/meetup/punemetro" : venues[i % venues.Length],
                Start = start, End = start.AddHours(i % 2 == 0 ? 2 : 1),
                Notes = i % 4 == 0 ? "Attendance will be marked." : null
            });
        }

        // ---------------------------------------------------------------
        // TRAINING BATCHES (completed, ongoing and upcoming intakes)
        // ---------------------------------------------------------------
        var batchDefs = new (string Name, int DaysFromNow, int LengthDays, int Intake)[]
        {
            ("Batch A — Jan Intake", -160, 45, 30), ("Batch B — Feb Intake", -130, 45, 25),
            ("Batch C — Apr Intake", -75, 60, 30), ("Batch D — Ops Refresher", -20, 40, 20),
            ("Batch E — Monsoon Intake", -5, 45, 35), ("Batch F — Aug Intake", 20, 45, 30),
            ("Batch G — New Joiners Q3", 35, 30, 40), ("Batch H — Supervisors", 50, 21, 15)
        };
        var batchRooms = new[]
        {
            "Room 2 · projector · 30 seats", "Training Room 1 · smart board", "Classroom A · 40 seats",
            "Crew Room · 20 seats", "Conference Room · video wall", "Room 5 · simulator lab",
            "Classroom B · 25 seats", "Seminar Hall · 60 seats"
        };
        for (int i = 0; i < batchDefs.Length; i++)
        {
            var (bName, offset, len, intake) = batchDefs[i];
            var course = published[(i * 3 + 1) % published.Count];
            db.TrainingBatches.Add(new TrainingBatch
            {
                Name = bName, CourseId = course.Id,
                CreatedById = course.InstructorId,
                StartDate = DateTime.UtcNow.AddDays(offset).Date,
                EndDate = DateTime.UtcNow.AddDays(offset + len).Date,
                MaxIntake = intake,
                Location = venues[i % venues.Length].Split(',')[0].Trim(),
                Room = batchRooms[i % batchRooms.Length],
                Description = $"{course.Code} intake for {intake} trainees.",
                CreatedAt = DateTime.UtcNow.AddDays(offset - 10)
            });
        }

        // ---------------------------------------------------------------
        // KNOWLEDGE HUB (20 documents, 20 videos, 20 FAQs)
        // ---------------------------------------------------------------
        var docDefs = new (string Title, DocumentCategory Cat)[]
        {
            ("Standard Operating Procedures — Train Operations", DocumentCategory.Procedure),
            ("HR Policy Handbook", DocumentCategory.Policy), ("Code of Conduct", DocumentCategory.Policy),
            ("Leave & Attendance Policy", DocumentCategory.Policy), ("POSH Policy", DocumentCategory.Policy),
            ("Emergency Response Plan (Stations)", DocumentCategory.Procedure), ("Evacuation Drill Procedure", DocumentCategory.Procedure),
            ("Power Block Request Procedure", DocumentCategory.Procedure), ("Shift Handover Procedure", DocumentCategory.Procedure),
            ("Incident Reporting Procedure", DocumentCategory.Procedure), ("AFC Gate Maintenance Manual", DocumentCategory.Manual),
            ("Rolling Stock Inspection Manual", DocumentCategory.Manual), ("CBTC Operations Manual", DocumentCategory.Manual),
            ("PIDS & PA Operation Manual", DocumentCategory.Manual), ("Escalator & Lift Safety Manual", DocumentCategory.Manual),
            ("Track Maintenance Manual", DocumentCategory.Manual), ("Uniform & Grooming Guidelines", DocumentCategory.Policy),
            ("Customer Service Charter", DocumentCategory.Other), ("Station Facilities Directory", DocumentCategory.Other),
            ("Training Calendar FY 2026-27", DocumentCategory.Other)
        };
        // Generate real sample PDFs for the first few library documents so they open on click
        var envDocs = services.GetRequiredService<IWebHostEnvironment>();
        var docsDir = Path.Combine(envDocs.WebRootPath, "uploads", "documents");
        Directory.CreateDirectory(docsDir);
        var pdfBodies = new Dictionary<string, string[]>
        {
            ["Standard Operating Procedures — Train Operations"] = new[]
            {
                "Document: SOP/OPS/001 Rev 3        Issued by: Maha-Metro Training Institute",
                "",
                "1. Scope",
                "   This SOP governs revenue-service train operations on the Purple and Aqua lines.",
                "2. Operating Modes",
                "   Trains run in ATO supervised by ATP. Restricted Manual (RM) mode is limited",
                "   to 25 km/h on the mainline and requires OCC authorisation.",
                "3. Depot Departure",
                "   Complete the wake-up test and brake continuity check before mainline entry.",
                "4. Shift Handover",
                "   Record rolling stock status, incidents, speed restrictions and pending",
                "   OCC instructions in the handover register before leaving duty.",
                "5. Emergencies",
                "   Follow the Emergency Response Plan; the Station Controller acts as",
                "   incident commander until relieved by the designated authority."
            },
            ["HR Policy Handbook"] = new[]
            {
                "Document: HR/POL/001 Rev 5         Issued by: HR & Administration",
                "",
                "1. Working Hours & Shifts",
                "   Operations staff follow the published shift roster; swaps need prior approval.",
                "2. Leave",
                "   Apply through HRMS. Emergency leave must be regularised within 48 hours.",
                "3. Code of Conduct",
                "   Uniform and grooming standards apply to all customer-facing staff.",
                "4. Training",
                "   Mandatory compliance training (SAF110) must be completed each cycle.",
                "5. Grievances",
                "   Raise concerns via the HR/Learning Team channel in the LMS Support desk."
            },
            ["Emergency Response Plan (Stations)"] = new[]
            {
                "Document: ERP/STN/002 Rev 2        Issued by: Safety & Security",
                "",
                "1. Alarm & Detection",
                "   On fire alarm activation, verify the zone on the station SCADA panel.",
                "2. Incident Command",
                "   The Station Controller assumes command; security marshals assist evacuation.",
                "3. Evacuation",
                "   Use the nearest signed route; lifts are prohibited. Assemble at the",
                "   designated assembly point and complete the headcount.",
                "4. Traction Power",
                "   Request an emergency power block from OCC before any track-level access.",
                "5. Reporting",
                "   File the incident report within 24 hours with timestamps and CCTV refs."
            }
        };
        var localPdfUrls = new Dictionary<string, string>();
        foreach (var (docTitle, body) in pdfBodies)
        {
            var fileName = docTitle.Split('—')[0].Trim().ToLower().Replace(' ', '-').Replace("(", "").Replace(")", "") + ".pdf";
            WriteSimplePdf(Path.Combine(docsDir, fileName), docTitle, body);
            localPdfUrls[docTitle] = $"/uploads/documents/{fileName}";
        }

        foreach (var (title, cat) in docDefs)
            db.Documents.Add(new DocumentItem
            {
                Title = title, Category = cat,
                Url = localPdfUrls.TryGetValue(title, out var localUrl) ? localUrl : "https://punemetrorail.org/documents",
                Description = $"{title} — official reference for all staff." + (localPdfUrls.ContainsKey(title) ? " (PDF)" : ""),
                UploadedById = Rnd.NextDouble() < 0.5 ? admin.Id : trainers[Rnd.Next(trainers.Count)].Id,
                UploadedAt = DateTime.UtcNow.AddDays(-Rnd.Next(2, 120))
            });

        var videoTopics = new[] { "Orientation", "Safety", "Operations", "Customer Service", "Engineering", "Signalling" };
        var videoUrls = new[]
        {
            "https://www.youtube.com/embed/2W1Zqn0YtEo", "https://www.youtube.com/embed/0y1eKPZbGxo",
            "https://www.youtube.com/embed/8bqjsrhLgMc", "https://www.youtube.com/embed/eIHKZfgddLM"
        };
        var videoTitles = new[]
        {
            "Pune Metro Network Overview", "Platform Safety Essentials", "Handling Passenger Complaints",
            "ATO/ATP Explained", "Evacuation Drill Walkthrough", "AFC Gate Fault First Aid",
            "Shift Briefing Best Practices", "Depot Walk-around Inspection", "PIDS Operation Basics",
            "Crowd Control on Event Days", "First Aid & CPR Refresher", "NCMC Card Handling",
            "Radio Communication Protocol", "Divyangjan Assistance Protocol", "Track Safety Awareness",
            "Fire Extinguisher Types & Use", "Escalator Incident Response", "OCC Coordination Basics",
            "Tunnel Emergency Procedures", "Customer Service Etiquette"
        };
        for (int i = 0; i < videoTitles.Length; i++)
            db.Videos.Add(new VideoItem
            {
                Title = videoTitles[i], Url = videoUrls[i % videoUrls.Length],
                Topic = videoTopics[i % videoTopics.Length],
                DurationMinutes = 6 + Rnd.Next(20),
                AddedAt = DateTime.UtcNow.AddDays(-Rnd.Next(2, 90))
            });

        var faqDefs = new (string Q, string A)[]
        {
            ("How do I reset my LMS password?", "Use Profile → Change password, or ask an administrator to reset it from the Users page."),
            ("What happens if I fail a scheduled assessment?", "Use your remaining attempts; once exhausted, request a retake from Assessments → Retake Assessments."),
            ("When do I receive my certificate?", "Automatically when your trainer posts a final grade at or above the course passing grade."),
            ("Is SAF110 mandatory?", "Yes — it is mandatory compliance training for all staff this quarter."),
            ("How do I enroll in a course?", "Open Course Catalog, pick a course and press Enroll."),
            ("Can I drop a course?", "Yes, from the course overview page. Your progress is retained."),
            ("Where do I find session joining links?", "Training Calendar → Upcoming Sessions lists links for online sessions."),
            ("How is attendance marked?", "Trainers mark attendance for classroom sessions; it appears in your progress report."),
            ("Who answers technical issues?", "Raise a ticket under Support → Technical Assistance."),
            ("Can I message my trainer?", "Yes — the Communication Center lets you message your trainers directly."),
            ("What is a self assessment?", "A practice quiz with unlimited attempts that never affects your grades."),
            ("How are final grades calculated?", "Trainers combine quiz scores, assignments and participation, then post a final grade."),
            ("Do partner courses give certificates?", "Completion is tracked and certified by the partner institution."),
            ("Can I access the LMS from home?", "Yes, the portal works on any modern browser; use your official credentials."),
            ("How do I change my department?", "Update it in your Profile, or ask HR to correct it."),
            ("What if a video does not play?", "Check the kiosk/browser network, then raise a Technical Assistance ticket."),
            ("How often are new courses added?", "The Training Institute publishes new programmes every quarter."),
            ("Are quizzes timed?", "Yes — the time limit is shown before you start; the quiz auto-submits at zero."),
            ("Can I retake a passed quiz?", "Only while attempts remain; retake requests are for exhausted attempts."),
            ("Who sees my feedback?", "Trainers and administrators see ratings and comments to improve courses.")
        };
        for (int i = 0; i < faqDefs.Length; i++)
            db.Faqs.Add(new Faq { Question = faqDefs[i].Q, Answer = faqDefs[i].A, Order = i + 1 });

        // ---------------------------------------------------------------
        // PARTNER COURSES (20)
        // ---------------------------------------------------------------
        var partnerDefs = new (string Title, string Provider, int Hours)[]
        {
            ("Metro Rail Signalling (CBTC) Fundamentals", "IRISET", 24),
            ("First Aid & CPR Certification", "Indian Red Cross Society", 8),
            ("Workplace Hindi–Marathi Communication", "SPPU Skill Centre", 12),
            ("Electrical Safety for Rail Systems", "NAIR Vadodara", 16),
            ("Fire Safety Officer Certification", "NFSC Nagpur", 40),
            ("Advanced Excel for Operations Reporting", "MKCL", 20),
            ("Disaster Management Essentials", "NIDM", 24),
            ("Customer Experience Excellence", "IIM Nagpur Exec-Ed", 16),
            ("Rolling Stock Bogie Overhaul", "BEML Academy", 32),
            ("OHE & Traction Systems", "IRIEEN Nasik", 28),
            ("Lift & Escalator Maintenance", "KONE Training Institute", 16),
            ("Industrial First Aid Refresher", "St John Ambulance", 6),
            ("Data Dashboards with Power BI", "MKCL", 18),
            ("Crowd Science & Station Design", "IIT Bombay CEP", 12),
            ("Railway Accident Investigation", "IRISET", 20),
            ("Occupational Health & Safety (IOSH)", "British Safety Council India", 24),
            ("Effective Public Announcements", "FTII Skill Lab", 8),
            ("Cyber Hygiene for Critical Infrastructure", "CDAC Pune", 10),
            ("Energy Efficiency in Metro Systems", "TERI", 14),
            ("Sign Language Basics for Frontline Staff", "AYJNISHD", 12)
        };
        foreach (var p in partnerDefs)
            db.PartnerCourses.Add(new PartnerCourse
            {
                Title = p.Title, Provider = p.Provider, DurationHours = p.Hours,
                Url = "https://punemetrorail.org/partners",
                Description = $"{p.Title} delivered by {p.Provider}; nomination via HR."
            });

        // ---------------------------------------------------------------
        // SUPPORT TICKETS (20) + RETAKE REQUESTS (10)
        // ---------------------------------------------------------------
        var ticketDefs = new (string Sub, TicketCategory Cat)[]
        {
            ("Video lessons buffer endlessly on crew-room kiosk", TicketCategory.TechnicalAssistance),
            ("Cannot upload assignment file (PDF, 6 MB)", TicketCategory.TechnicalAssistance),
            ("Quiz timer froze mid-attempt", TicketCategory.TechnicalAssistance),
            ("Login fails on mobile browser", TicketCategory.TechnicalAssistance),
            ("Certificate PDF prints without name", TicketCategory.TechnicalAssistance),
            ("Which course covers NCMC card handling?", TicketCategory.LearningQuery),
            ("Need refresher on degraded mode operations", TicketCategory.LearningQuery),
            ("Is LDR120 open for station controllers?", TicketCategory.LearningQuery),
            ("Prerequisites for SIG110?", TicketCategory.LearningQuery),
            ("Where are session recordings stored?", TicketCategory.LearningQuery),
            ("Request to add Marathi subtitles to videos", TicketCategory.LearningQuery),
            ("Clarification on SAF110 completion deadline", TicketCategory.LearningQuery),
            ("Nomination process for partner courses", TicketCategory.HRLearningTeam),
            ("Transfer my enrollments after department change", TicketCategory.HRLearningTeam),
            ("Duplicate profile — please merge accounts", TicketCategory.HRLearningTeam),
            ("Update my name spelling on certificate", TicketCategory.HRLearningTeam),
            ("Extend OPS101 access during medical leave", TicketCategory.HRLearningTeam),
            ("Add me to Engineering department cohort", TicketCategory.HRLearningTeam),
            ("PIDS simulator access for practice", TicketCategory.TechnicalAssistance),
            ("Bulk-enroll my team of 12 for SAF150", TicketCategory.HRLearningTeam)
        };
        for (int i = 0; i < ticketDefs.Length; i++)
        {
            var answered = i % 3 != 2;
            db.SupportTickets.Add(new SupportTicket
            {
                RaisedById = learners[i % learners.Count].Id,
                Category = ticketDefs[i].Cat, Subject = ticketDefs[i].Sub,
                Body = $"{ticketDefs[i].Sub}. Please assist at the earliest.",
                Status = answered ? (i % 4 == 0 ? TicketStatus.Closed : TicketStatus.Answered) : TicketStatus.Open,
                Response = answered ? "Thanks for flagging — actioned by the L&D helpdesk. Reply here if the issue persists." : null,
                CreatedAt = DateTime.UtcNow.AddDays(-Rnd.Next(1, 30)),
                RespondedAt = answered ? DateTime.UtcNow.AddDays(-Rnd.Next(0, 5)) : null
            });
        }

        var reasons = new[]
        {
            "Scored below passing in both attempts; have revised the material.",
            "Network dropped during my second attempt.",
            "Was on emergency duty during the assessment window.",
            "Misread two questions; confident after revision.",
            "Medical leave during the attempt window."
        };
        for (int i = 0; i < 10; i++)
        {
            var e = enrollments[Rnd.Next(enrollments.Count)];
            db.RetakeRequests.Add(new RetakeRequest
            {
                QuizId = quizByCourse[e.CourseId].Id, StudentId = e.StudentId,
                Reason = reasons[i % reasons.Length],
                Status = i < 4 ? RetakeStatus.Pending : i < 8 ? RetakeStatus.Approved : RetakeStatus.Rejected,
                DecisionNote = i < 4 ? null : i < 8 ? "Approved — one extra attempt granted." : "Please attend the refresher session first.",
                RequestedAt = DateTime.UtcNow.AddDays(-Rnd.Next(1, 20)),
                DecidedAt = i < 4 ? null : DateTime.UtcNow.AddDays(-Rnd.Next(0, 5))
            });
        }

        // ---------------------------------------------------------------
        // ANNOUNCEMENTS (20), MESSAGES (24), NOTIFICATIONS, CALENDAR
        // ---------------------------------------------------------------
        var siteAnnouncements = new[]
        {
            ("Welcome to the Pune Metro LMS", "The training platform is live. All staff must complete SAF110 this quarter."),
            ("Q3 training calendar published", "Browse Training Calendar → Upcoming Sessions and plan your month."),
            ("New partner courses added", "Twenty partner programmes from IRISET, Red Cross and more are now open for nomination."),
            ("LMS maintenance window", "The portal will be briefly unavailable Sunday 02:00–04:00 for updates."),
            ("Safety week drills", "Evacuation drills across all stations next week — attendance mandatory.")
        };
        foreach (var (t, b) in siteAnnouncements)
            db.Announcements.Add(new Announcement { Title = t, Body = b, AuthorId = admin.Id, CreatedAt = DateTime.UtcNow.AddDays(-Rnd.Next(1, 30)) });
        for (int i = 0; i < 15; i++)
        {
            var course = published[i % published.Count];
            var (title, body) = SeedContent.CourseAnnouncements[i % SeedContent.CourseAnnouncements.Length];
            db.Announcements.Add(new Announcement
            {
                CourseId = course.Id, AuthorId = course.InstructorId,
                Title = $"{course.Code}: {title}",
                Body = body,
                CreatedAt = DateTime.UtcNow.AddDays(-Rnd.Next(1, 20))
            });
        }

        // Discussion forums: realistic threads with trainer replies
        for (int i = 0; i < SeedContent.Threads.Length; i++)
        {
            var (title, body, reply) = SeedContent.Threads[i];
            var course = published[(i * 2 + 1) % published.Count];
            var courseStudents = enrollments.Where(e => e.CourseId == course.Id).Select(e => e.StudentId).ToList();
            if (courseStudents.Count == 0) continue;
            var thread = new DiscussionThread
            {
                CourseId = course.Id, Title = title, Body = body,
                AuthorId = courseStudents[Rnd.Next(courseStudents.Count)],
                CreatedAt = DateTime.UtcNow.AddDays(-Rnd.Next(2, 30)),
                IsPinned = i % 6 == 0
            };
            thread.Posts.Add(new DiscussionPost
            {
                AuthorId = course.InstructorId, Body = reply,
                CreatedAt = thread.CreatedAt.AddDays(Rnd.Next(1, 3))
            });
            if (i % 3 == 0 && courseStudents.Count > 1)
                thread.Posts.Add(new DiscussionPost
                {
                    AuthorId = courseStudents[Rnd.Next(courseStudents.Count)],
                    Body = "Thanks — this clarifies it. Very helpful!",
                    CreatedAt = thread.CreatedAt.AddDays(3)
                });
            db.DiscussionThreads.Add(thread);
        }

        for (int i = 0; i < 24; i++)
        {
            var trainer = trainers[i % trainers.Count];
            var learner = learners[i % learners.Count];
            var (subject, body) = SeedContent.Messages[i % SeedContent.Messages.Length];
            // trainer-authored subjects vs learner-authored queries
            var fromTrainer = subject is "Welcome to the course" or "Session reschedule" or "Great progress this week" or "Quiz revision tips" or "Attendance correction";
            db.Messages.Add(new Message
            {
                SenderId = fromTrainer ? trainer.Id : learner.Id,
                RecipientId = fromTrainer ? learner.Id : trainer.Id,
                Subject = subject,
                Body = body,
                SentAt = DateTime.UtcNow.AddDays(-Rnd.Next(0, 20)).AddHours(-Rnd.Next(0, 12)),
                ReadAt = i % 3 == 0 ? null : DateTime.UtcNow.AddDays(-Rnd.Next(0, 5))
            });
        }

        foreach (var learner in learners.Take(10))
            Notifier.Notify(db, learner.Id, "New training session scheduled in one of your courses.", "/Training/Sessions");
        foreach (var trainer in trainers)
            Notifier.Notify(db, trainer.Id, "You have submissions awaiting grading.", "/Instructor/Dashboard");
        Notifier.Notify(db, trainees[0].Id, "Congratulations! A certificate has been issued to you.", "/Certificates");

        // Event reminders — every role gets personal events this month, plus
        // course-wide reminders visible to everyone enrolled in / teaching the course
        var staff = new List<ApplicationUser> { admin, admin2, principal };
        staff.AddRange(trainers);
        var staffEventTitles = new[]
        {
            "Quarterly training plan review", "Safety compliance audit prep", "New course content workshop",
            "Trainer sync — upcoming sessions", "Grading day — clear submission queue", "L&D steering committee",
            "Department training needs review", "Assessment moderation meeting", "Induction batch kickoff"
        };
        for (int i = 0; i < staff.Count; i++)
        {
            db.CalendarEvents.Add(new CalendarEvent
            {
                UserId = staff[i].Id, Title = staffEventTitles[i % staffEventTitles.Length],
                Start = DateTime.UtcNow.AddDays(1 + i % 12).Date.AddHours(10 + i % 6), Type = EventType.Other
            });
            db.CalendarEvents.Add(new CalendarEvent
            {
                UserId = staff[i].Id, Title = staffEventTitles[(i + 4) % staffEventTitles.Length],
                Start = DateTime.UtcNow.AddDays(-(2 + i % 7)).Date.AddHours(9 + i % 4), Type = EventType.Other
            });
        }
        for (int i = 0; i < learners.Count; i++)
            db.CalendarEvents.Add(new CalendarEvent
            {
                UserId = learners[i].Id, Title = i % 2 == 0 ? "Personal study block" : "Revision — checkpoint quiz prep",
                Start = DateTime.UtcNow.AddDays(1 + i % 14).Date.AddHours(i % 2 == 0 ? 18 : 20), Type = EventType.Personal
            });
        for (int i = 0; i < 12; i++)
        {
            var course = published[i % published.Count];
            db.CalendarEvents.Add(new CalendarEvent
            {
                CourseId = course.Id,
                Title = i % 3 == 0 ? $"{course.Code} checkpoint quiz due" :
                        i % 3 == 1 ? $"{course.Code} assignment submission due" : $"{course.Code} live session",
                Start = DateTime.UtcNow.AddDays(2 + i * 2).Date.AddHours(i % 3 == 2 ? 11 : 17),
                Type = i % 3 == 0 ? EventType.Quiz : i % 3 == 1 ? EventType.Assignment : EventType.Course
            });
        }

        // Settings + audit
        db.SiteSettings.AddRange(
            new SiteSetting { Key = "SiteName", Value = "Learning Management System" },
            new SiteSetting { Key = "AllowSelfRegistration", Value = "true" },
            new SiteSetting { Key = "DefaultPassingGrade", Value = "60" },
            new SiteSetting { Key = "SeedVersion", Value = SeedVersion }
        );
        db.AuditLogs.Add(new AuditLog { UserId = admin.Id, UserName = admin.FullName, Action = "System", Details = $"Database seeded with Pune Metro demo dataset ({SeedVersion})" });

        // ---------------------------------------------------------------
        // Demo SCORM 1.2 package attached to OPS101 (end-to-end standards test)
        // ---------------------------------------------------------------
        var env = services.GetRequiredService<IWebHostEnvironment>();
        var scormRoot = Path.Combine(env.WebRootPath, "scorm", "demo-ops-refresher");
        Directory.CreateDirectory(scormRoot);
        await File.WriteAllTextAsync(Path.Combine(scormRoot, "imsmanifest.xml"), """
<?xml version="1.0" encoding="UTF-8"?>
<manifest identifier="PM-OPS-REFRESHER" version="1.2"
  xmlns="http://www.imsproject.org/xsd/imscp_rootv1p1p2"
  xmlns:adlcp="http://www.adlnet.org/xsd/adlcp_rootv1p2">
  <organizations default="ORG1">
    <organization identifier="ORG1"><title>Operations Refresher (SCORM 1.2)</title>
      <item identifier="ITEM1" identifierref="RES1"><title>Operations Refresher</title></item>
    </organization>
  </organizations>
  <resources>
    <resource identifier="RES1" type="webcontent" adlcp:scormtype="sco" href="index.html">
      <file href="index.html"/>
    </resource>
  </resources>
</manifest>
""");
        await File.WriteAllTextAsync(Path.Combine(scormRoot, "index.html"), """
<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>Operations Refresher</title>
<style>body{font-family:'DM Sans',sans-serif;background:#f5f6fa;margin:0;padding:2.5rem;color:#364a63}
.card{background:#fff;border:1px solid #dbdfea;border-radius:8px;max-width:640px;margin:0 auto;padding:2rem;text-align:center}
button{background:#6576ff;color:#fff;border:0;border-radius:6px;padding:.7rem 1.6rem;font-size:1rem;cursor:pointer}
.ok{color:#1ee0ac;font-weight:700}</style></head>
<body><div class="card">
<h2>Operations Refresher — SCORM 1.2 demo</h2>
<p>This content talks to the LMS through the SCORM 1.2 runtime API (<code>window.API</code>).</p>
<p id="who"></p>
<button id="btn" onclick="complete()">Mark module completed (score 95)</button>
<p id="status"></p>
<script>
function findAPI(w){for(var i=0;i<10&&w;i++){if(w.API)return w.API;w=w.parent!==w?w.parent:w.opener}return null}
var API=findAPI(window);
if(API){API.LMSInitialize('');
 document.getElementById('who').textContent='Learner: '+API.LMSGetValue('cmi.core.student_name')+' ('+API.LMSGetValue('cmi.core.student_id')+') — status: '+API.LMSGetValue('cmi.core.lesson_status');}
function complete(){if(!API)return;
 API.LMSSetValue('cmi.core.score.raw','95');
 API.LMSSetValue('cmi.core.lesson_status','completed');
 API.LMSSetValue('cmi.core.session_time','0000:05:00');
 API.LMSCommit('');API.LMSFinish('');
 document.getElementById('status').innerHTML='<span class="ok">Completed & reported to the LMS ✓</span>';
 document.getElementById('btn').disabled=true;}
</script></div></body></html>
""");
        var demoPkg = new ContentPackage
        {
            Title = "Operations Refresher (SCORM 1.2)", Standard = ContentStandard.Scorm12,
            RootPath = "demo-ops-refresher", LaunchUrl = "index.html", UploadedById = admin.Id
        };
        db.ContentPackages.Add(demoPkg);
        await db.SaveChangesAsync();
        var ops101Module2 = courses.First(c => c.Code == "OPS101").Modules.OrderBy(m => m.Order).Last();
        db.Lessons.Add(new Lesson
        {
            ModuleId = ops101Module2.Id, Title = "Operations Refresher (Interactive SCORM)",
            Type = LessonType.Scorm, Order = ops101Module2.Lessons.Count + 1,
            DurationMinutes = 15, ContentPackageId = demoPkg.Id,
            Content = "<p>Interactive SCORM 1.2 content — completion is reported automatically.</p>"
        });

        await db.SaveChangesAsync();
    }
}
