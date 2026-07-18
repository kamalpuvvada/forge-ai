namespace Forge.Core;

public interface IClarificationEngine
{
    Task<ClarificationEvaluation> EvaluateAsync(
        EngineeringTask task,
        CancellationToken cancellationToken = default);
}
