using MailArchiver.Auth.Handlers;
using MailArchiver.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MailArchiver.Auth.Middlewares
{
    /// <summary>
    /// Fork only (eherman101/mail-archiver): skips the login screen.
    /// <para>
    /// Upstream made authentication mandatory, but this deployment sits behind the LAN
    /// firewall and Tailscale, which already decide who can reach it. When
    /// <c>Authentication:AutoLoginUser</c> names an existing, active user, any browser
    /// request without a session is signed in as that user (with a persistent cookie)
    /// and sent on to the page it asked for, and the login page forwards to the
    /// dashboard. Everything downstream sees an ordinary signed-in user, so admin
    /// checks and per-user account access work unchanged.
    /// </para>
    /// <para>
    /// The keyed <c>/api/</c> and <c>/mcp</c> routes are left alone: they keep
    /// requiring an API key. Leave the setting empty to get upstream's login back.
    /// </para>
    /// </summary>
    public class AutoLoginMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<AutoLoginMiddleware> _logger;
        private readonly string? _autoLoginUser;

        public AutoLoginMiddleware(RequestDelegate next, ILogger<AutoLoginMiddleware> logger, IConfiguration configuration)
        {
            _next = next;
            _logger = logger;
            _autoLoginUser = configuration["Authentication:AutoLoginUser"]?.Trim();
            if (!string.IsNullOrEmpty(_autoLoginUser))
            {
                _logger.LogWarning("Login is disabled: every browser request is signed in as '{User}' (Authentication:AutoLoginUser)", _autoLoginUser);
            }
        }

        public async Task InvokeAsync(HttpContext context, MailArchiver.Services.IAuthenticationService authService,
            IUserService userService, AuthenticationHandler authenticationHandler)
        {
            var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
            var isKeyedRoute = path.StartsWith("/api/") || path.StartsWith("/mcp/") || path == "/mcp";
            if (string.IsNullOrEmpty(_autoLoginUser) || isKeyedRoute)
            {
                await _next(context);
                return;
            }

            var isLoginPage = path.StartsWith("/auth/login");
            if (authService.IsAuthenticated(context))
            {
                if (isLoginPage)
                {
                    context.Response.Redirect("/");
                    return;
                }
                await _next(context);
                return;
            }

            var user = await userService.GetUserByUsernameAsync(_autoLoginUser);
            if (user == null || !user.IsActive)
            {
                _logger.LogError("Authentication:AutoLoginUser '{User}' is not an active user; falling back to the login page", _autoLoginUser);
                await _next(context);
                return;
            }

            // Clear any half-finished 2FA state, which IsAuthenticated treats as signed out.
            context.Session.Remove("TwoFactorUsername");
            await authenticationHandler.HandleUserAuthenticated(
                CookieAuthenticationDefaults.AuthenticationScheme, user.Username, persistAuthentication: true);

            // The new cookie only counts from the next request, so send the browser back
            // to where it was going (the dashboard, for the login page itself).
            context.Response.Redirect(isLoginPage ? "/" : context.Request.PathBase + context.Request.Path + context.Request.QueryString);
        }
    }
}
