namespace DbData.Config.Testing;

/// <summary>Thrown by every helper in this package when an expectation fails; any test framework reports it as a failure.</summary>
public sealed class ConfigTestException(string message, Exception? inner = null) : Exception(message, inner);
