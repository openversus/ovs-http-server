using HandlebarsDotNet;

namespace OpenVersus.Server.Web.Site;

/// <summary>The pages, rendered from the TS server's handlebars templates (Static/*.html) as it renders them.</summary>
public static class Pages
{
    private static readonly IHandlebars s_handlebars = Handlebars.Create();
    private static readonly Lazy<HandlebarsTemplate<object, object>> s_nameChange = new(() => Compile("name_change.html"));
    private static readonly Lazy<HandlebarsTemplate<object, object>> s_accountPicker = new(() => Compile("account_picker.html"));
    private static readonly Lazy<HandlebarsTemplate<object, object>> s_accountVerify = new(() => Compile("account_verify.html"));
    private static readonly Lazy<HandlebarsTemplate<object, object>> s_home = new(() => Compile("home.html"));
    private static readonly Lazy<HandlebarsTemplate<object, object>> s_leaderboard = new(() => Compile("leaderboard.html"));
    private static readonly Lazy<HandlebarsTemplate<object, object>> s_matches = new(() => Compile("matches.html"));
    private static readonly Lazy<HandlebarsTemplate<object, object>> s_myStats = new(() => Compile("my_stats.html"));
    private static readonly Lazy<HandlebarsTemplate<object, object>> s_adminBanner = new(() => Compile("admin.html"));

    /// <summary>home.html, leaderboard.html, matches.html: no variables.</summary>
    public static string Home() => s_home.Value(new Dictionary<string, object?>());
    public static string Leaderboard() => s_leaderboard.Value(new Dictionary<string, object?>());
    public static string Matches() => s_matches.Value(new Dictionary<string, object?>());

    /// <summary>my_stats.html: hasStats, playerName, aggregateJson, charactersJson (raw JSON for inline scripts), updatedAt.</summary>
    public static string MyStats(bool hasStats, string? playerName = null, string? aggregateJson = null, string? charactersJson = null, long? updatedAt = null) =>
        s_myStats.Value(new Dictionary<string, object?>
        {
            ["hasStats"] = hasStats, ["playerName"] = playerName, ["aggregateJson"] = aggregateJson, ["charactersJson"] = charactersJson, ["updatedAt"] = updatedAt,
        });

    /// <summary>admin.html: onlineCount.</summary>
    public static string AdminBanner(long onlineCount) => s_adminBanner.Value(new Dictionary<string, object?> { ["onlineCount"] = onlineCount });

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
