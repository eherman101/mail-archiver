using MailArchiver.Auth.Middlewares;

namespace MailArchiver.Auth.Extensions
{
    public static class UseAuthExtension
    {
        public static WebApplication UseAuth(this WebApplication app)
        {
            // Fork only: sign in as Authentication:AutoLoginUser when set (no login screen)
            app.UseMiddleware<AutoLoginMiddleware>();
            // Add our custom authentication middleware
            app.UseMiddleware<AuthenticationMiddleware>();
            app.UseAuthorization();
            return app;
        }
    }
}
