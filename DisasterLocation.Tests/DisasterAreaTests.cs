using DisasterLocation.Core;
using Xunit;

namespace DisasterLocation.Tests;

public sealed class DisasterAreaTests
{
    [Fact]
    public void OnePointAndRepeatedPointsHaveUnknownRadius()
    {
        var area = DisasterArea.FromPoints([(2.9, -75.2), (2.9, -75.2)])!;
        Assert.Equal(2.9, area.Latitude); Assert.Equal(-75.2, area.Longitude);
        Assert.Null(area.Radius); Assert.Null(area.Polygon);
    }

    [Fact]
    public void TwoPointsAndCollinearReportsUseFirstPointAndFarthestDistance()
    {
        var area = DisasterArea.FromPoints([(2.9, -75.2), (2.91, -75.2)])!;
        Assert.Equal(2.9, area.Latitude); Assert.Equal(-75.2, area.Longitude);
        Assert.InRange(area.Radius!.Value, 1111, 1113); Assert.Null(area.Polygon);
        var line = DisasterArea.FromPoints([(2.9, -75.2), (2.91, -75.2), (2.92, -75.2)])!;
        Assert.Equal(2.9, line.Latitude); Assert.Equal(-75.2, line.Longitude);
        Assert.InRange(line.Radius!.Value, 2222, 2225); Assert.Null(line.Polygon);
    }

    [Fact]
    public void PolygonUsesHullCentroidAndGeographicRadius()
    {
        var area = DisasterArea.FromPoints([(2.9, -75.2), (2.91, -75.2), (2.9, -75.19), (2.901, -75.199)])!;
        Assert.Equal(2.903333333333333, area.Latitude, 10);
        Assert.Equal(-75.19666666666667, area.Longitude, 10);
        var ring = area.Polygon!.Value.GetProperty("coordinates")[0];
        Assert.Equal(4, ring.GetArrayLength());
        Assert.Equal(ring[0].ToString(), ring[3].ToString());
        Assert.InRange(area.Radius!.Value, 800, 850);
        Assert.Throws<ArgumentOutOfRangeException>(() => DisasterArea.FromPoints([(2.9, -75.2), (double.NaN, 0)]));
    }
}
