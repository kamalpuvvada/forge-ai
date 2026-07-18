namespace Forge.Core;

public interface IEngineeringTaskPdfExporter
{
    byte[] Export(EngineeringTask task);
}
