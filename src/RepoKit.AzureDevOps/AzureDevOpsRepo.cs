namespace RepoKit.AzureDevOps;

public sealed record AzureDevOpsRepo
{
    public AzureDevOpsRepo(string organizationUrl, string project, string repository)
    {
        if (!Uri.TryCreate(organizationUrl, UriKind.Absolute, out var organization) || organization.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"'{organizationUrl}' is not an https organization URL, e.g. https://dev.azure.com/contoso.", nameof(organizationUrl));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        OrganizationUrl = new UriBuilder(organization) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        Project = project;
        Repository = repository;
    }

    public string OrganizationUrl { get; }

    public string Project { get; }

    public string Repository { get; }

    public string CloneUrl => $"{OrganizationUrl}/{Uri.EscapeDataString(Project)}/_git/{Uri.EscapeDataString(Repository)}";

    public string WebUrl => CloneUrl;

    public override string ToString() => $"{Project}/{Repository}";
}
