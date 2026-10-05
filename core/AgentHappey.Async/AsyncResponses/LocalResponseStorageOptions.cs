namespace AgentHappey.AsyncResponses;

public sealed class LocalResponseStorageOptions
{
    public string? RootPath { get; set; }

    public string GetRootPath()
    {
        if (!string.IsNullOrWhiteSpace(RootPath))
            return Path.GetFullPath(RootPath, AppContext.BaseDirectory);

        var applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData))
            throw new InvalidOperationException("The local application-data directory is unavailable. Configure LocalResponses:RootPath.");

        return Path.Combine(applicationData, "aihappey", "agents");
    }
}
