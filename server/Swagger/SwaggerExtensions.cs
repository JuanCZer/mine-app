using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Notes.Api.Swagger;

public static class SwaggerExtensions
{
    public const string SessionCookieScheme = "SessionCookie";

    public static IServiceCollection AddDevelopmentSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "GDS Mine API",
                Version = "v1",
                Description = "Flujo local: GET /api/auth/csrf, POST /api/auth/login y después los endpoints protegidos."
            });
            options.AddSecurityDefinition(SessionCookieScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Cookie,
                Name = "GDS.Session",
                Description = "Cookie de sesión HttpOnly emitida por el inicio de sesión."
            });
            options.OperationFilter<SessionCookieSecurityOperationFilter>();
        });
        return services;
    }

    public static WebApplication UseDevelopmentSwagger(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "GDS Mine API v1");
            options.UseRequestInterceptor("""
                (request) => {
                  const target = new URL(request.url, window.location.href);
                  if (target.origin !== window.location.origin) return request;

                  request.credentials = 'same-origin';
                  const method = (request.method || 'GET').toUpperCase();
                  const mutable = !['GET', 'HEAD', 'OPTIONS', 'TRACE'].includes(method);
                  const apiPath = target.pathname === '/api' || target.pathname.startsWith('/api/');
                  if (!mutable || !apiPath) return request;

                  const cookie = document.cookie.split(';')
                    .map(value => value.trim())
                    .find(value => value.startsWith('XSRF-TOKEN='));
                  if (cookie) {
                    request.headers = request.headers || {};
                    request.headers['X-XSRF-TOKEN'] = decodeURIComponent(cookie.substring('XSRF-TOKEN='.length));
                  }
                  return request;
                }
                """);
        });
        return app;
    }
}

public sealed class SessionCookieSecurityOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            return;

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SwaggerExtensions.SessionCookieScheme, context.Document)] = []
            }
        ];
    }
}
