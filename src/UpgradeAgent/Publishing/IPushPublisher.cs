namespace UpgradeAgent.Publishing;

internal interface IPushPublisher
{
    string How { get; }

    Task<PushResult> PublishAsync(PushBranchTool tool, CancellationToken cancellationToken);
}
