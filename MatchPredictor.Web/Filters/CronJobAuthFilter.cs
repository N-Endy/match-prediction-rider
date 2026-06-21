using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MatchPredictor.Web.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class CronJobAuthAttribute : Attribute, IAuthorizationFilter
{
    public const string SecretHeaderName = "X-Cron-Secret";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedSecret = configuration["CronJob:Secret"];

        if (string.IsNullOrWhiteSpace(expectedSecret))
        {
            context.Result = new ObjectResult(new { error = "Cron job secret is not configured." })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
            return;
        }

        var providedSecret = context.HttpContext.Request.Headers[SecretHeaderName].ToString();
        if (!FixedTimeEquals(providedSecret, expectedSecret))
        {
            context.Result = new UnauthorizedResult();
        }
    }

    private static bool FixedTimeEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
