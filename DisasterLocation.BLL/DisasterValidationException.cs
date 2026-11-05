namespace DisasterLocation.BLL;

public sealed class DisasterValidationException(Dictionary<string, string[]> errors)
    : Exception("Los datos del desastre no son válidos.")
{
    public Dictionary<string, string[]> Errors { get; } = errors;
}
