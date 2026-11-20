using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MuseFM.Data.Entities;

namespace MuseFM.Services;

/// <summary>
/// The security stamps of the accounts that were changed since the server started. A sign-in cookie carries the stamp
/// its account had when it was issued, and Identity replaces the stamp when a password, username or (see EditUser) role
/// changes. Identity compares the two only now and then, against the database; with the stamps at hand, a cookie that
/// was made invalid stops working with the very next request. That matters most for an administrator who was demoted
/// or deleted, and would otherwise keep the admin pages for a while.
/// </summary>
public sealed class SecurityStamps
{
    // A null stamp: the account was deleted.
    private readonly ConcurrentDictionary<string, string?> _current = new();

    public void Changed(AppUser user) => _current[user.Id] = user.SecurityStamp;

    public void Deleted(AppUser user) => _current[user.Id] = null;

    /// <summary>True when the account is known to have another stamp by now (or to be gone).</summary>
    public bool IsOutdated(string userId, string? stamp) => _current.TryGetValue(userId, out var current) && current != stamp;

    /// <summary>Cookie validation: refuses outdated sign-ins right away, then lets Identity do its periodic check.</summary>
    public static Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var services = context.HttpContext.RequestServices;
        var claims = services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
        if (context.Principal?.FindFirstValue(claims.UserIdClaimType) is { } userId
            && services.GetRequiredService<SecurityStamps>().IsOutdated(userId, context.Principal.FindFirstValue(claims.SecurityStampClaimType)))
        {
            // The cookie itself is left alone: this may be a request that was under way while the same browser got its
            // new cookie (after changing the password), and deleting "the" cookie now would sign that browser out too.
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        return SecurityStampValidator.ValidatePrincipalAsync(context);
    }
}

/// <summary>Identity's user manager, reporting every saved account to <see cref="SecurityStamps"/>.</summary>
public sealed class AppUserManager(
    IUserStore<AppUser> store,
    IOptions<IdentityOptions> options,
    IPasswordHasher<AppUser> passwordHasher,
    IEnumerable<IUserValidator<AppUser>> userValidators,
    IEnumerable<IPasswordValidator<AppUser>> passwordValidators,
    ILookupNormalizer keyNormalizer,
    IdentityErrorDescriber errors,
    IServiceProvider services,
    ILogger<UserManager<AppUser>> logger,
    SecurityStamps stamps)
    : UserManager<AppUser>(store, options, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
{
    // Every change of an existing account ends up here, whatever it was.
    protected override async Task<IdentityResult> UpdateUserAsync(AppUser user)
    {
        var result = await base.UpdateUserAsync(user);
        if (result.Succeeded) stamps.Changed(user);
        return result;
    }

    public override async Task<IdentityResult> DeleteAsync(AppUser user)
    {
        var result = await base.DeleteAsync(user);
        if (result.Succeeded) stamps.Deleted(user);
        return result;
    }
}
