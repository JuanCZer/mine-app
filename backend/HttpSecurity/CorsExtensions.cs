namespace Notes.Api.HttpSecurity;

public static class CorsExtensions
{
    private static readonly string[] DevelopmentOrigins =
    [
        "http://localhost:4200",
        "http://127.0.0.1:4200",
        "http://localhost:4317",
        "http://127.0.0.1:4317",
    ];

    public static WebApplicationBuilder AddApplicationCors(this WebApplicationBuilder builder)
    {
        var allowLoopback = builder.Environment.IsDevelopment();
        var configured = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var origins = configured.Select(value => ValidOrigin(value, allowLoopback));
        if (allowLoopback) origins = origins.Concat(DevelopmentOrigins);
        var allowedOrigins = origins.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        {
            if (allowedOrigins.Length > 0) policy.WithOrigins(allowedOrigins);
            policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }));
        return builder;
    }

    private static string ValidOrigin(string value, bool allowLoopback)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("Cors:AllowedOrigins must contain only HTTP(S) origins without paths, credentials, queries or fragments.");
        }

        if (!allowLoopback && uri.IsLoopback)
            throw new InvalidOperationException("Cors:AllowedOrigins cannot contain loopback origins outside Development.");

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
