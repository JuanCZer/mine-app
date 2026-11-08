using Notes.Api.Models;

namespace Notes.Api.Data;

public sealed record MapSector(double Latitude, double Longitude, long Count, int RadiusMeters = 1000);
public sealed record MapSectors(long Total, IReadOnlyList<MapSector> Sectors);
public sealed record MapIncident(string Id, string Title, string Content, string? State, double Latitude, double Longitude, string Status, DateTimeOffset CreatedAt);
public sealed record MapIncidentPage(IReadOnlyList<MapIncident> Items, string? NextCursor);
public sealed record MapBounds(double West, double South, double East, double North);

public interface IMapRepository
{
    Task<MapSectors> Sectors(CancellationToken cancellationToken);
    Task<MapIncidentPage> Incidents(MapBounds bounds, string? after, CancellationToken cancellationToken);
    Task<Note?> SharedDetail(string id, CancellationToken cancellationToken);
}
