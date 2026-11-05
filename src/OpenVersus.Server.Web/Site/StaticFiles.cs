using Microsoft.AspNetCore.StaticFiles;

namespace OpenVersus.Server.Web.Site;

/// <summary>The website's files (Static/, copied next to the service: copies of the TS server's src/static).</summary>
public static class StaticFiles
{
    public static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Static");

    // What GET /images/{image} and /favicon/{image} serve (the TS server's handleImages: one list for both paths).
    private static readonly Dictionary<string, string> s_images = new(StringComparer.Ordinal)
    {
        ["favicon-96x96.png"] = "favicon/favicon-96x96.png",
        ["favicon.svg"] = "favicon/favicon.svg",
        ["favicon.ico"] = "favicon/favicon.ico",
        ["apple-touch-icon.png"] = "favicon/apple-touch-icon.png",
        ["site.webmanifest"] = "favicon/site.webmanifest",
        ["name_change_background.jpeg"] = "name_change_background.jpeg",
        ["OpenVersus_logo.png"] = "OpenVersus_logo.png",
        ["200w.webp"] = "200w.webp",
        ["200.webp"] = "200.webp",
        ["200w2.webp"] = "200w2.webp",
        ["multiversus-bugs-bunny.gif"] = "multiversus-bugs-bunny.gif",
    };

    private static readonly FileExtensionContentTypeProvider s_types = new();

    /// <summary>One of the images by its name, or 404 "Image not found".</summary>
    public static Task SendImageAsync(HttpContext context, string? name)
    {
        if (name is not null && s_images.TryGetValue(name, out string? path))
        {
            return SendAsync(context, path);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return context.Response.WriteAsync("Image not found");
    }

    /// <summary>A file under Static/ with its type (from the extension), and a Cache-Control when given.</summary>
    public static async Task SendAsync(HttpContext context, string path, string? cacheControl = null)
    {
        string full = Path.Combine(Root, path);
        if (!File.Exists(full))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            // Started: FastEndpoints answers 204 for a response a handler never started.
            await context.Response.StartAsync();
            return;
        }

        context.Response.ContentType = s_types.TryGetContentType(full, out string? type) ? type : "application/octet-stream";
        if (cacheControl is not null)
        {
            context.Response.Headers.CacheControl = cacheControl;
        }

        await context.Response.SendFileAsync(full);
    }
}
