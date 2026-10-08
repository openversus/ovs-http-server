using HandlebarsDotNet;

namespace OpenVersus.Server.Web.Site;

/// <summary>The pages, rendered from the TS server's handlebars templates (Static/*.html) as it renders them.</summary>
public static class Pages
{
    private static readonly IHandlebars s_handlebars = Handlebars.Create();
    private static readonly Lazy<HandlebarsTemplate<object, object>> s_nameChange = new(() => Compile("name_change.html"));
    private static readonly Lazy<HandlebarsTemplate<object, object>> s_accountPicker = new(() => Compile("account_picker.html"));
    private static readonly Lazy<HandlebarsTemplate<object, object>> s_accountVerify = new(() => Compile("account_verify.html"));

    /// <summary>name_change.html: currentUsername, error, success.</summary>
    public static string NameChange(string currentUsername, string? error, bool? success) =>
        s_nameChange.Value(new Dictionary<string, object?> { ["currentUsername"] = currentUsername, ["error"] = error, ["success"] = success });

    /// <summary>account_picker.html: returnTo, error, accounts (id, name, steamId, epicId, hardwareId).</summary>
    public static string AccountPicker(string returnTo, string? error, IEnumerable<Dictionary<string, object?>> accounts) =>
        s_accountPicker.Value(new Dictionary<string, object?> { ["returnTo"] = returnTo, ["error"] = error, ["accounts"] = accounts.ToList() });

    /// <summary>account_verify.html: verifyId, returnTo, accountName, steamId, error.</summary>
    public static string AccountVerify(string verifyId, string returnTo, string accountName, string steamId, string? error) =>
        s_accountVerify.Value(new Dictionary<string, object?>
        {
            ["verifyId"] = verifyId, ["returnTo"] = returnTo, ["accountName"] = accountName, ["steamId"] = steamId, ["error"] = error,
        });

    /// <summary>Sends a page (text/html) with a status. Started even when empty: FastEndpoints answers 204 for a response a handler never started.</summary>
    public static async Task SendAsync(HttpContext context, string html, int status = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.StartAsync();
        await context.Response.WriteAsync(html);
    }

    /// <summary>Sends text (text/html, as express's res.send of a string) with a status.</summary>
    public static Task SendTextAsync(HttpContext context, string text, int status) => SendAsync(context, text, status);

    private static HandlebarsTemplate<object, object> Compile(string file) =>
        s_handlebars.Compile(File.ReadAllText(Path.Combine(StaticFiles.Root, file)));
}
