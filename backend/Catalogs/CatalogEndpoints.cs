namespace Notes.Api.Catalogs;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/catalogs", async (
            string? keys,
            string? stage,
            bool? includeInactive,
            CatalogService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var parsed = CatalogRegistry.ParseKeys(keys);
                return Results.Ok(await service.GetAsync(parsed, stage, includeInactive ?? false, cancellationToken));
            }
            catch (CatalogRequestException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: exception.StatusCode);
            }
            catch (CatalogUnavailableException)
            {
                return Results.Json(new { error = "El servicio de catálogos no está disponible temporalmente." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        return endpoints;
    }
}
