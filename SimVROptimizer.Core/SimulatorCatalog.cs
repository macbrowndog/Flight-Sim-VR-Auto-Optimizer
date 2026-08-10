namespace SimVROptimizer.Core;

public static class SimulatorCatalog
{
    public static IReadOnlyList<SimulatorDefinition> All { get; } =
    [
        new("msfs2024-steam", "Microsoft Flight Simulator 2024 (Steam)", ["FlightSimulator2024"], LaunchKind.Uri, "steam://run/2537590"),
        new("msfs2020-steam", "Microsoft Flight Simulator 2020 (Steam)", ["FlightSimulator"], LaunchKind.Uri, "steam://run/1250410"),
        new("dcs-steam", "DCS World (Steam)", ["DCS", "DCS_mt"], LaunchKind.Uri, "steam://run/223750"),
        new("xplane12-steam", "X-Plane 12 (Steam)", ["X-Plane"], LaunchKind.Uri, "steam://run/2014780"),
        new("msfs2024-store", "Microsoft Flight Simulator 2024 (Store)", ["FlightSimulator2024"], LaunchKind.Uri, "shell:AppsFolder\\Microsoft.Limitless_8wekyb3d8bbwe!App"),
        new("msfs2020-store", "Microsoft Flight Simulator 2020 (Store)", ["FlightSimulator"], LaunchKind.Uri, "shell:AppsFolder\\Microsoft.FlightSimulator_8wekyb3d8bbwe!App"),
        new("il2-sturmovik-steam", "IL-2 Sturmovik: Battle of Stalingrad (Steam)", ["Il-2"], LaunchKind.Uri, "steam://run/307960")
    ];

    // Seven catalog entries plus the dynamically resolved DCS and X-Plane standalone targets.
    public static int SupportedConfigurationCount => All.Count + 2;

    public static SimulatorDefinition? Find(string? id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyDictionary<string, SimulatorDefinition> SteamByAppId { get; } = new Dictionary<string, SimulatorDefinition>
    {
        ["2537590"] = All[0],
        ["1250410"] = All[1],
        ["223750"] = All[2],
        ["2014780"] = All[3],
        ["307960"] = All[6]
    };
}
