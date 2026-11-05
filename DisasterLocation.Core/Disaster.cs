namespace DisasterLocation.Core;

public sealed class Disaster
{
    public int Id { get; set; }
    public required string Nombre { get; set; }
    public required string Ubicacion { get; set; }
    public int Severidad { get; set; }
    public required string Descripcion { get; set; }
    public double Latitud { get; set; }
    public double Longitud { get; set; }
}
