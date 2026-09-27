using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Core.Arm;

public static class ArmConstants
{
    public const string ApiVersion = "2026-09-01";
    public const string ResourcesNamespace = "Burbuja.Resources";
    public const string AuthorizationNamespace = "Burbuja.Authorization";
}

public sealed class ArmException(ArmError error) : Exception(error.Message)
{
    public ArmError Error { get; } = error;
}

public sealed record ArmErrorBody(string Code, string Message, string? Target, IReadOnlyList<ArmErrorBody> Details, System.Text.Json.Nodes.JsonObject? Innererror);

public sealed record ArmErrorEnvelope(ArmErrorBody Error)
{
    public static ArmErrorEnvelope From(ArmError error) =>
        new(new ArmErrorBody(
            error.Code,
            error.Message,
            error.Target,
            error.Details.Select(d => From(d).Error).ToArray(),
            error.InnerError));
}
