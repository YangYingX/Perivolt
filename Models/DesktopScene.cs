namespace PowerHub.Models;

public sealed class DesktopScene
{
    public const int MaximumCount = 12;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Theme { get; set; } = "System";
    public bool ThemeEffectsEnabled { get; set; } = true;
    public bool WidgetEnabled { get; set; }
    public string WidgetLayout { get; set; } = "Stack";
    public bool WidgetSnapEnabled { get; set; } = true;
    public bool WidgetCompact { get; set; }
    public bool WidgetTransparent { get; set; }
    public bool WidgetTopmost { get; set; }
    public bool WidgetLocked { get; set; }
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public List<string> PinnedDeviceIds { get; set; } = [];
    public List<string> DeviceOrder { get; set; } = [];
    public Dictionary<string, WidgetPosition> WidgetPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static DesktopScene Capture(AppSettings settings, string name, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"), Name = name.Trim(), Theme = settings.Theme,
        ThemeEffectsEnabled = settings.ThemeEffectsEnabled, WidgetEnabled = settings.WidgetEnabled,
        WidgetLayout = settings.WidgetLayout, WidgetSnapEnabled = settings.WidgetSnapEnabled,
        WidgetCompact = settings.WidgetCompact, WidgetTransparent = settings.WidgetTransparent,
        WidgetTopmost = settings.WidgetTopmost, WidgetLocked = settings.WidgetLocked,
        WidgetLeft = settings.WidgetLeft, WidgetTop = settings.WidgetTop,
        PinnedDeviceIds = settings.Devices.Where(pair => pair.Value.Pinned).Select(pair => settings.ResolveDeviceId(pair.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        DeviceOrder = settings.DeviceOrder.ToList(),
        WidgetPositions = CopyPositions(settings.WidgetPositions)
    };

    public void Normalize()
    {
        Name = AppSettings.NormalizeCustomName(Name) ?? "";
        Name = Name.Replace('\r', ' ').Replace('\n', ' ');
        if (Name.Length > 40) Name = Name[..(char.IsHighSurrogate(Name[39]) ? 39 : 40)];
        WidgetLayout = WidgetLayout == "Free" ? "Free" : "Stack";
        if (WidgetLeft is double left && !double.IsFinite(left)) WidgetLeft = null;
        if (WidgetTop is double top && !double.IsFinite(top)) WidgetTop = null;
        PinnedDeviceIds = CleanIds(PinnedDeviceIds);
        DeviceOrder = CleanIds(DeviceOrder);
        WidgetPositions = CopyPositions(WidgetPositions);
    }

    public void Apply(AppSettings settings)
    {
        settings.Theme = Theme;
        settings.ThemeEffectsEnabled = ThemeEffectsEnabled;
        settings.WidgetEnabled = WidgetEnabled;
        settings.WidgetLayout = WidgetLayout;
        settings.WidgetSnapEnabled = WidgetSnapEnabled;
        settings.WidgetCompact = WidgetCompact;
        settings.WidgetTransparent = WidgetTransparent;
        settings.WidgetTopmost = WidgetTopmost;
        settings.WidgetLocked = WidgetLocked;
        settings.WidgetLeft = WidgetLeft;
        settings.WidgetTop = WidgetTop;
        foreach (var preference in settings.Devices.Values) preference.Pinned = false;
        foreach (var id in PinnedDeviceIds.Select(settings.ResolveDeviceId))
        {
            if (!settings.Devices.TryGetValue(id, out var preference)) settings.Devices[id] = preference = new();
            preference.Pinned = true;
        }
        settings.DeviceOrder = DeviceOrder.Select(settings.ResolveDeviceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.WidgetPositions = CopyPositions(WidgetPositions, settings.ResolveDeviceId);
    }

    internal static Dictionary<string, WidgetPosition> CopyPositions(Dictionary<string, WidgetPosition>? positions, Func<string, string>? resolve = null)
    {
        var result = new Dictionary<string, WidgetPosition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, position) in positions ?? [])
        {
            if (string.IsNullOrWhiteSpace(id) || position is null) continue;
            var key = resolve?.Invoke(id) ?? id;
            result[key] = new()
            {
                Left = position.Left is double left && double.IsFinite(left) ? left : null,
                Top = position.Top is double top && double.IsFinite(top) ? top : null
            };
        }
        return result;
    }

    private static List<string> CleanIds(List<string>? ids) => (ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
        .Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
