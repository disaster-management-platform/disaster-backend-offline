namespace DisasterLocation.BLL;

public sealed class DisasterConflictException(string message = "La clave de idempotencia ya se usó con otros datos.")
    : Exception(message);
