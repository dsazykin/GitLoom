using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GitLoom.Core.Analytics;

public class LanguageModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("extensions")]
    public List<string> Extensions { get; set; } = new();

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#cccccc";
}
