using Wonder.Core;

namespace Wonder.Host.Contracts;

public sealed record DeliveryResolution(DeliveryResolutionAction Action, string? ExternalId);