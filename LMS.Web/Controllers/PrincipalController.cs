using LMS.Web.Data;
using LMS.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LMS.Web.Controllers;

[Authorize(Roles = "Principal")]
public class PrincipalController : Controller
{
    private readonly AppDbContext _db;
    public PrincipalController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Dashboard()
    {
        var vm = await DashboardBuilder.BuildAdminAsync(_db, showAudit: false);
        return View("~/Views/Admin/Dashboard.cshtml", vm);
    }

    /// <summary>Course oversight: the Principal can deactivate / reactivate any course.</summary>
    public async Task<IActionResult> Courses()
    {
        var courses = await _db.Courses
            .Include(c => c.Instructor).Include(c => c.Category).Include(c => c.Enrollments)
            .OrderBy(c => c.Title).ToListAsync();
        return View(courses);
    }
}
