using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Notes.Api.HttpSecurity;

public sealed class HttpSecuritySettings
{
    public const long DefaultMaxRequestBodyBytes = 1024 * 1024;
    public const string DefaultReportOnlyPolicy =
        "default-src 'self'; base-uri 'self'; object-src 'none'; form-action 'self'; " +
        "script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https://api.mapbox.com https://*.tiles.mapbox.com; " +
        "connect-src 'self' https://api.mapbox.com https://events.mapbox.com https://*.tiles.mapbox.com; worker-src 'self' blob:";

    public bool EnableContentSecurityPolicyReportOnly { get; set; } = true;
    public string ContentSecurityPolicyReportOnly { get; set; } = DefaultReportOnlyPolicy;
}

public static class HttpSecurityExtensions
{
    public static WebApplicationBuilder AddHttpSecurity(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<HttpSecuritySettings>()
            .Bind(builder.Configuration.GetSection("HttpSecurity"))
            .Validate(settings =>
                    !settings.EnableContentSecurityPolicyReportOnly
                    || (!string.IsNullOrWhiteSpace(settings.ContentSecurityPolicyReportOnly)
                        && settings.ContentSecurityPolicyReportOnly.Length <= 4096
                        && !settings.ContentSecurityPolicyReportOnly.Contains('\r')
                        && !settings.ContentSecurityPolicyReportOnly.Contains('\n')),
                "HttpSecurity report-only CSP must be non-empty, bounded and single-line when enabled.")
            .ValidateOnStart();

        builder.Services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(180);
            options.IncludeSubDomains = false;
            options.Preload = false;
            options.ExcludedHosts.Clear();
        });

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = true;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var value in builder.Configuration.GetSection("HttpSecurity:KnownProxies").Get<string[]>() ?? [])
            {
                if (!IPAddress.TryParse(value, out var address))
                    throw new OptionsValidationException("HttpSecurity:KnownProxies", typeof(ForwardedHeadersOptions), ["Every known proxy must be an explicit IP address."]);
                options.KnownProxies.Add(address);
            }
            foreach (var value in builder.Configuration.GetSection("HttpSecurity:KnownNetworks").Get<string[]>() ?? [])
            {
                if (!System.Net.IPNetwork.TryParse(value, out var network))
                    throw new OptionsValidationException("HttpSecurity:KnownNetworks", typeof(ForwardedHeadersOptions), ["Every known network must use valid CIDR notation."]);
                options.KnownIPNetworks.Add(network);
            }
            // Empty trust lists mean "trust every forwarder" to the framework. Keep an
            // impossible sentinel so even auto-enabled forwarded headers remain fail-closed.
            if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
                options.KnownProxies.Add(IPAddress.None);
        });

        builder.WebHost.ConfigureKestrel((context, options) =>
        {
            var bytes = context.Configuration.GetValue<long?>("HttpSecurity:MaxRequestBodyBytes")
                ?? HttpSecuritySettings.DefaultMaxRequestBodyBytes;
            if (bytes is < 64 * 1024 or > 16 * 1024 * 1024)
                throw new InvalidOperationException("HttpSecurity:MaxRequestBodyBytes must be between 64 KiB and 16 MiB.");
            options.Limits.MaxRequestBodySize = bytes;
        });
        return builder;
    }

    public static WebApplication UseHttpSecurity(this WebApplication app)
    {
        var hasKnownForwarder = app.Configuration.GetSection("HttpSecurity:KnownProxies").GetChildren().Any()
            || app.Configuration.GetSection("HttpSecurity:KnownNetworks").GetChildren().Any();
        if (hasKnownForwarder) app.UseForwardedHeaders();
        if (!(app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))) app.UseHsts();

        var settings = app.Services.GetRequiredService<IOptions<HttpSecuritySettings>>().Value;
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Content-Security-Policy"] = "frame-ancestors 'none'";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
                if (settings.EnableContentSecurityPolicyReportOnly)
                    headers["Content-Security-Policy-Report-Only"] = settings.ContentSecurityPolicyReportOnly;
                return Task.CompletedTask;
            });
            await next(context);
        });
        return app;
    }
}
