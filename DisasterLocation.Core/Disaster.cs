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
    
    public double Radio { get; set; }
    
    public required string DesastreAsociado { get; set; }
    
    public required string FechaHora { get; set; }
    
    public required string Evaluador { get; set; }
    
    public required string Organizacion { get; set; }
    
    public required string TipoActivo { get; set; }
    
    public required string NivelDano { get; set; }
    
    public required string Evidencias { get; set; }
    
    public required string EstadoEvaluacion { get; set; }
    
}
