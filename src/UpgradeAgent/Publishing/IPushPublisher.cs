namespace UpgradeAgent.Publishing;

/// <summary>Gets push_branch called, with the operator's approval.</summary>
internal interface IPushPublisher
{
    /// <summary>How approval works, shown before publishing.</summary>
    string How { get; }

    Task<PushResult> PublishAsync(PushBranchTool tool, CancellationToken cancellationToken);
}
