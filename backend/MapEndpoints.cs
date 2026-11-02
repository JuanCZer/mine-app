using Notes.Api.Data;

namespace Notes.Api;

public static class MapEndpoints
{
    public static void MapMapEndpoints(this IEndpointRouteBuilder app)
    {
        var map = app.MapGroup("/api/map").RequireAuthorization();
        map.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (NoteRepositoryUnavailableException) { return Results.Json(new { error = "El servicio de incidencias no está disponible temporalmente." }, statusCode: 503); }
        });
        map.MapGet("/sectors", async (IMapRepository store, HttpContext context) => Results.Ok(await store.Sectors(context.RequestAborted)));
        map.MapGet("/incidents", async (double west, double south, double east, double north, string? after, IMapRepository store, HttpContext context) =>
        {
            if (!double.IsFinite(west) || !double.IsFinite(east) || !double.IsFinite(south) || !double.IsFinite(north) || west < -180 || east > 180 || south < -90 || north > 90 || west >= east || south >= north || (east - west) * (north - south) > 400 || after?.Length > 128)
                return Results.BadRequest(new { error = "Los límites del mapa o el cursor no son válidos." });
            return Results.Ok(await store.Incidents(new MapBounds(west, south, east, north), string.IsNullOrEmpty(after) ? null : after, context.RequestAborted));
        });
        map.MapGet("/incidents/{id}", async (string id, IMapRepository store, HttpContext context) =>
        {
            if (id.Length > 128) return Results.NotFound();
            var note = await store.SharedDetail(id, context.RequestAborted);
            return note is null ? Results.NotFound() : Results.Ok(note);
        });
    }
}
