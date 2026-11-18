using System.Text;
using System.Text.Json;
using DisasterLocation.Core;

namespace DisasterLocation.BLL;

internal static class GeographyValidation
{
    public static void Validate(DisasterGeography? geography)
    {
        try
        {
            if (geography is null || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(geography)) > 65_536)
                throw new FormatException();
            foreach (var names in new[] { geography.Municipalities, geography.Neighborhoods, geography.RiskZones })
                if (names is not null && (names.Length > 100 || names.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 200)
                    || names.Distinct(StringComparer.Ordinal).Count() != names.Length)) throw new FormatException();
            if (geography.Polygon is not JsonElement polygon || polygon.ValueKind == JsonValueKind.Null) return;
            if (polygon.ValueKind != JsonValueKind.Object || polygon.GetProperty("type").GetString() != "Polygon"
                || polygon.EnumerateObject().Any(x => x.Name is not ("type" or "coordinates"))) throw new FormatException();
            var rings = polygon.GetProperty("coordinates").EnumerateArray().Select(ring => ring.EnumerateArray()
                .Select(point =>
                {
                    var coordinates = point.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    if (coordinates.Length != 2 || !double.IsFinite(coordinates[0]) || !double.IsFinite(coordinates[1])
                        || coordinates[0] is < -180 or > 180 || coordinates[1] is < -90 or > 90) throw new FormatException();
                    return (X: coordinates[0], Y: coordinates[1]);
                }).ToArray()).ToArray();
            if (rings.Length is < 1 or > 50 || rings.Sum(x => x.Length) > 2000) throw new FormatException();
            foreach (var ring in rings)
            {
                if (ring.Length < 4 || ring[0] != ring[^1] || ring[..^1].Distinct().Count() != ring.Length - 1) throw new FormatException();
                var area = 0d;
                for (var i = 0; i < ring.Length - 1; i++)
                {
                    area += ring[i].X * ring[i + 1].Y - ring[i + 1].X * ring[i].Y;
                    for (var j = i + 2; j < ring.Length - 1; j++)
                        if (!(i == 0 && j == ring.Length - 2) && Intersects(ring[i], ring[i + 1], ring[j], ring[j + 1])) throw new FormatException();
                }
                if (Math.Abs(area) < 1e-12) throw new FormatException();
            }
            for (var i = 0; i < rings.Length; i++)
                for (var j = i + 1; j < rings.Length; j++)
                {
                    for (var a = 0; a < rings[i].Length - 1; a++)
                        for (var b = 0; b < rings[j].Length - 1; b++)
                            if (Intersects(rings[i][a], rings[i][a + 1], rings[j][b], rings[j][b + 1])) throw new FormatException();
                    if (i == 0 ? !Contains(rings[0], rings[j][0]) : Contains(rings[i], rings[j][0]) || Contains(rings[j], rings[i][0]))
                        throw new FormatException();
                }
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException or JsonException)
        {
            throw new DisasterValidationException(new Dictionary<string, string[]> { ["geography"] =
                ["Use GeoJSON Polygon WGS84 válido (longitud, latitud), hasta 2000 posiciones, 50 anillos y 64 KiB; listas de zonas de hasta 100 nombres."] });
        }
    }

    private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    private static bool Intersects((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d) =>
        Math.Max(a.X, b.X) >= Math.Min(c.X, d.X) && Math.Max(c.X, d.X) >= Math.Min(a.X, b.X)
        && Math.Max(a.Y, b.Y) >= Math.Min(c.Y, d.Y) && Math.Max(c.Y, d.Y) >= Math.Min(a.Y, b.Y)
        && Cross(a, b, c) * Cross(a, b, d) <= 0 && Cross(c, d, a) * Cross(c, d, b) <= 0;
    private static bool Contains((double X, double Y)[] ring, (double X, double Y) p)
    {
        var inside = false;
        for (var i = 0; i < ring.Length - 1; i++)
        {
            var a = ring[i]; var b = ring[i + 1];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }
}
