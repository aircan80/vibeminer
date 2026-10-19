using VibeMiner.Application;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal sealed class GeometryWorkBudget(AcquisitionResources resources, IAcquisitionWorkContext? context = null) : IGeometryArtifactWorkBudget
{
    public async ValueTask<IDisposable> EnterAsync(CancellationToken token) => await resources.EnterCpuAsync(context, token);
}
