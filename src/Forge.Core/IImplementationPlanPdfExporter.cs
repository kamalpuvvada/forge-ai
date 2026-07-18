namespace Forge.Core;

public interface IImplementationPlanPdfExporter
{
    byte[] Export(EngineeringTask task);
}
