using System.Text.Json.Serialization;

namespace AgentHappey.Common.Models;

public class AgentSystemInfo
{
    public DateTime? UtcNow { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; set; }

}