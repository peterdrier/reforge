using Microsoft.AspNetCore.Mvc;
using SampleSolution.Core.Models;
using SampleSolution.Services.Data;

namespace SampleSolution.Web.Controllers;

/// <summary>
/// Intentional design rule violations for Phase 3 audit testing:
/// 1. Directly uses AppDbContext (DbContext-like class) instead of going through a service
/// 2. Has a method with bool isAdmin parameter (privileged boolean)
/// Also tests ownership-violations: controller accesses Users and AuditLogs tables directly.
/// Also tests audit-auth: missing [Authorize] and [ValidateAntiForgeryToken] on actions.
/// </summary>
public class BadController : Controller
{
    private readonly AppDbContext _dbContext;

    public BadController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Design violation: controller directly uses DbContext.
    /// </summary>
    public User? GetUserDirectly(int id)
    {
        return _dbContext.Users.FirstOrDefault(u => u.Id == id);
    }

    /// <summary>
    /// Design violation: bool isAdmin parameter (privileged boolean anti-pattern).
    /// </summary>
    public IReadOnlyList<User> GetUsers(bool isAdmin, int maxResults = 100)
    {
        if (isAdmin)
        {
            return _dbContext.Users.Take(maxResults).ToList().AsReadOnly();
        }

        return _dbContext.Users.Where(u => u.IsActive).Take(maxResults).ToList().AsReadOnly();
    }

    /// <summary>
    /// audit-auth violation: POST action missing [Authorize].
    /// </summary>
    [HttpPost]
    public void UnsafePost()
    {
    }

    /// <summary>
    /// audit-auth violation: POST action missing [ValidateAntiForgeryToken].
    /// Has [Authorize] but no anti-forgery protection.
    /// </summary>
    [Authorize]
    [HttpPost]
    public void NoAntiForgery()
    {
    }

    /// <summary>
    /// audit-auth violation: PUT action missing [Authorize].
    /// </summary>
    [HttpPut]
    public void UnsafePut()
    {
    }

    /// <summary>
    /// audit-auth: clean — has both [Authorize] and [ValidateAntiForgeryToken].
    /// </summary>
    [Authorize]
    [ValidateAntiForgeryToken]
    [HttpPost]
    public void SafePost()
    {
    }

    /// <summary>
    /// audit-auth: clean — GET actions don't require [Authorize] by default.
    /// </summary>
    [HttpGet]
    public User? GetUser(int id)
    {
        return _dbContext.Users.FirstOrDefault(u => u.Id == id);
    }

    /// <summary>
    /// audit-auth: clean — has [AllowAnonymous] so missing [Authorize] is intentional.
    /// </summary>
    [AllowAnonymous]
    [HttpDelete]
    public void PublicDelete()
    {
    }
}
