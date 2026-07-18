namespace Forge.Core;

public interface IClarificationEngine
{
    ClarificationResult Evaluate(EngineeringTask task);
}
