namespace Burbuja.Agents;

public sealed class AgentsOptions
{
    public const string Section = "Agents";

    public string ScenarioDirectory { get; set; } = "";

    public string ReposFile { get; set; } = "";

    public string[] HttpAllowHosts { get; set; } = [];

    public int NodeCapacity { get; set; } = 4;

    /// <summary>
    /// Which installed <c>llm.provider</c> serves agent turns, e.g. <c>scripted</c> or
    /// <c>deepseek</c>. Empty keeps the historical "last registration wins" rule; naming an
    /// unknown provider fails at startup instead of silently running on a paid endpoint.
    /// </summary>
    public string ProviderId { get; set; } = "";
}
