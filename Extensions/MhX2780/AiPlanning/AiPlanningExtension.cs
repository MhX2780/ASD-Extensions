using ASD.Extensibility;

namespace AiPlanning;

public sealed class AiPlanningExtension : IAsdExtension
{
    public string Id => "MhX2780.AiPlanning";

    public void Register(IAsdHost host)
    {
        host.AddPage(new ExtensionPage { Tag = "aiplanning", Title = "AI Planning", Glyph = "", Create = () => new PlanPage(host.DataFolder) });
    }
}
