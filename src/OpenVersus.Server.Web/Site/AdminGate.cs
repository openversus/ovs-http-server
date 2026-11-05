using System.ComponentModel;
using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using Microsoft.Extensions.Options;

namespace OpenVersus.Server.Web.Site;

/// <summary>The website's admin pages (the TS ADMIN_PASSWORD) and the data asset sync (DATA_ASSET_TOKEN).</summary>
public sealed class AdminSettings
{
    [Description("The password of the website's admin pages (/admin/*, /api/admin/*): ?pw=<password> once, which sets the admin_auth cookie for a day. The TS server's ADMIN_PASSWORD.")]
    public string Password { get; set; } = "changeme";

    [Description("The bearer token POST /syncAsset must carry (the TS server's DATA_ASSET_TOKEN). Empty: every sync is refused.")]
    public string DataAssetToken { get; set; } = "";
}

/// <summary>
/// The TS admin gate: the password in ?pw= (which sets the admin_auth cookie, httpOnly, a day) or in the cookie lets a
/// request through; /admin/* answers a login form (401, HTML) otherwise and /api/admin/* a JSON 401.
/// </summary>
public static class AdminGate
{
    public const string Cookie = "admin_auth";

    private const string LoginForm = """

    <html><body style="font-family:Arial;display:grid;place-items:center;height:100vh;margin:0;background:#111;color:#fff">
      <form style="text-align:center">
        <h2>Admin Login</h2>
        <input name="pw" type="password" placeholder="Password" style="padding:10px;font-size:16px;border-radius:8px;border:none;margin:8px"/>
        <button type="submit" style="padding:10px 24px;font-size:16px;border-radius:8px;border:none;background:#ff7500;color:#fff;cursor:pointer">Login</button>
      </form>
    </body></html>
  
""";

    /// <summary>Whether the request is let through; a refusal has been answered (the form for a page, JSON for the API).</summary>
    public static async Task<bool> PassAsync(HttpContext context, bool api)
    {
        string password = context.RequestServices.GetRequiredService<IOptionsMonitor<AdminSettings>>().CurrentValue.Password;
        if (context.Request.Query["pw"] == password)
        {
            if (!api)
            {
                context.Response.Cookies.Append(Cookie, password, new CookieOptions { HttpOnly = true, MaxAge = TimeSpan.FromDays(1) });
            }

            return true;
        }

        if (context.Request.Cookies[Cookie] == password)
        {
            return true;
        }

        if (api)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(Js.Stringify(new JsonObject { ["error"] = "Unauthorized" }));
        }
        else
        {
            await Pages.SendAsync(context, LoginForm, StatusCodes.Status401Unauthorized);
        }

        return false;
    }
}
