using Nuraherbex.Shared.Models;

namespace Nuraherbex.UI.Services;

public class ProjectState
{
    public string ProjectName { get; set; } = ProjectConfig.NeuraHerbex;
    public bool IsMidFit => ProjectConfig.IsMidFit(ProjectName);
    public bool IsNeuraHerbex => ProjectConfig.IsNeuraHerbex(ProjectName);

    public string BrandName => IsMidFit ? "MidFit" : "Nura Herbex";
    public string BrandSubtitle => IsMidFit ? "FITNESS & WELLNESS WORKSPACE" : "STORE ADMINISTRATION";
    public string ThemeClass => IsMidFit ? "theme-midfit" : "";
}
