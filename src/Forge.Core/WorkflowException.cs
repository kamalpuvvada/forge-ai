namespace Forge.Core;

public sealed class WorkflowException(string message) : InvalidOperationException(message);
