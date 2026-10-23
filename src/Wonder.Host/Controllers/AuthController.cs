using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController(
    WonderDbContext db,
    IConfiguration config,
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn) : WonderControllerBase(db)
{
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(Credentials input)
    {
        if (!config.GetValue("Authentication:AllowRegistration", true)) return StatusCode(403);
        var email = input.Email.Trim();
        var user = new ApplicationUser { Id = Guid.NewGuid(), Email = email, UserName = email };
        var result = await users.CreateAsync(user, input.Password);
        if (result.Succeeded) return Created("/api/v1/me", new { user.Id });
        if (result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName")) return Conflict();
        foreach (var error in result.Errors) ModelState.AddModelError(error.Code, error.Description);
        return ValidationProblem(ModelState);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(Credentials input)
    {
        var user = await users.FindByEmailAsync(input.Email.Trim());
        if (user is null) return Unauthorized();
        var result = await signIn.PasswordSignInAsync(user, input.Password, false, true);
        return result.Succeeded ? Ok(new { user.Id, user.Email }) : Unauthorized();
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return NoContent();
    }
}