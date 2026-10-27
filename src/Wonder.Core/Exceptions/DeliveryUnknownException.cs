namespace Wonder.Core.Exceptions;

public sealed class DeliveryUnknownException(string message, Exception? inner = null) : Exception(message, inner);