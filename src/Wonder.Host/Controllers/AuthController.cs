using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Wonder.Host.Contracts;
using Wonder.Infrastructure;

namespace Wonder.Host.Controllers;

[Route("/api/v1/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthController(WonderDbContext db, IConfiguration config, IPasswordHasher<ApplicationUser> hasher)
    : WonderControllerBase(db)
{
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register(Credentials input, CancellationToken ct)
    {
        if (!config.GetValue("Authentication:AllowRegistration", true)) return StatusCode(403);
        if (!MailAddress.TryCreate(input.Email, out _) || input.Password.Length is < 12 or > 200)
            return BadRequest(new { error = "Use a valid email and a password of 12–200 characters." });
        var email = input.Email.Trim().ToUpperInvariant();
        if (await Db.Users.AnyAsync(x => x.NormalizedEmail == email, ct)) return Conflict();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), Email = input.Email.Trim(), UserName = input.Email.Trim(), NormalizedEmail = email,
            NormalizedUserName = email, SecurityStamp = Guid.NewGuid().ToString()
        };
        user.PasswordHash = hasher.HashPassword(user, input.Password);
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        return Created("/api/v1/me", new { user.Id });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(Credentials input, CancellationToken ct)
    {
        var normalized = input.Email.Trim().ToUpperInvariant();
        var user = await Db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);
        if (user is null || user.LockoutEnd > DateTimeOffset.UtcNow) return Unauthorized();
        var verification = hasher.VerifyHashedPassword(user, user.PasswordHash ?? "", input.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            user.AccessFailedCount++;
            if (user.AccessFailedCount >= 5) user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(5);
            await Db.SaveChangesAsync(ct);
            return Unauthorized();
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            user.PasswordHash = hasher.HashPassword(user, input.Password);
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        await Db.SaveChangesAsync(ct);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(
            new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Email ?? "")
            ], CookieAuthenticationDefaults.AuthenticationScheme)));
        return Ok(new { user.Id, user.Email });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return NoContent();
    }
}