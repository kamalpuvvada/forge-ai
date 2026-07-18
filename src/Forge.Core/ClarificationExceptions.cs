namespace Forge.Core;

public sealed class ClarificationConfigurationException(string message) : InvalidOperationException(message);

public sealed class ClarificationProviderException(
    string safeMessage,
    string category,
    ModelCallRecord failedCall,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public string Category { get; } = category;
    public ModelCallRecord FailedCall { get; } = failedCall;
}
