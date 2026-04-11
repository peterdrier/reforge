using Microsoft.AspNetCore.Mvc;

namespace SampleSolution.Web.Controllers;

/// <summary>
/// Controller with class-level [Authorize] — tests that audit-auth
/// does NOT flag actions here as missing [Authorize].
/// Also has [AutoValidateAntiforgeryToken] so POST methods don't need per-method token.
/// </summary>
[Authorize]
[AutoValidateAntiforgeryToken]
public class SecureController : Controller
{
    /// <summary>
    /// Clean: class has [Authorize] and [AutoValidateAntiforgeryToken].
    /// </summary>
    [HttpPost]
    public void SecurePost()
    {
    }

    [HttpDelete]
    public void SecureDelete()
    {
    }

    [HttpGet]
    public string GetData() => "data";
}
