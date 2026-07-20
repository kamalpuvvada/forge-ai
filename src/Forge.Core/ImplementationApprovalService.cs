namespace Forge.Core;

public sealed class ImplementationApprovalService(
    IImplementationApprovalRepository repository,
    ImplementationOperationCoordinator operationCoordinator,
    TimeProvider timeProvider)
{
    public async Task<EngineeringTask> ApproveAsync(
        Guid taskId,
        Guid commandId,
        long expectedRowVersion,
        Guid expectedRevisionId,
        string expectedResultFingerprint,
        CancellationToken cancellationToken = default)
    {
        var command = new ImplementationApprovalCommand(
            commandId,
            taskId,
            expectedRowVersion,
            expectedRevisionId,
            expectedResultFingerprint);
        using var operationLock = await operationCoordinator.EnterAsync(taskId, cancellationToken);
        return await repository.ApproveImplementationAsync(
            command,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }
}
