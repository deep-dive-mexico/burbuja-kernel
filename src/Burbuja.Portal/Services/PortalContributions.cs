using System.Text.Json.Nodes;

namespace Burbuja.Portal.Services;

/// <summary>A product-owned component mounted at an existing kernel page's extension point.</summary>
public sealed record PortalComponentContribution(string Slot, string PortalId, Type Component);

public sealed record PortalLiveness(string State, string Why);

/// <summary>Optional provenance of a generic runtime row. A product owns its naming convention.</summary>
public interface IPortalSessionLabel
{
    string? Label(string? name);
}

/// <summary>A subject-specific decision, after the inbox has checked the approval's current ETag.
/// The target belongs to the product; the approval's ETag must not be sent as the target's ETag.</summary>
public sealed record PortalApprovalAction(string Target, JsonObject Body);

public interface IPortalApprovalContribution
{
    string? Consequence(PortalApproval approval);
    PortalApprovalAction? Decision(ArmScope scope, PortalApproval approval, string approvalId, string action, string? note);
}
