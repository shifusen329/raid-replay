using System.Collections.Concurrent;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using RaidReplay.Core.GameData;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace RaidReplay.GameData;

/// <summary>
/// <see cref="IGameData"/> over Lumina excel sheets. Used by the plugin (IDataManager.Excel) and linked into the
/// CLI (standalone Lumina GameData over the local sqpack) so shapes can be validated outside the game.
/// </summary>
public sealed class LuminaGameData : IGameData
{
    private readonly ExcelSheet<LuminaAction> actions;
    private readonly ExcelSheet<Status> statuses;
    private readonly ExcelSheet<ClassJob> jobs;
    private readonly ExcelSheet<Map> maps;
    private readonly ExcelSheet<FieldMarker> fieldMarkers;
    private readonly ExcelSheet<Marker> markers;
    private readonly ConcurrentDictionary<uint, ActionInfo?> actionCache = new();
    private readonly ConcurrentDictionary<uint, StatusInfo?> statusCache = new();
    private readonly ConcurrentDictionary<uint, MapInfo?> mapCache = new();

    public LuminaGameData(ExcelModule excel)
    {
        actions = excel.GetSheet<LuminaAction>();
        statuses = excel.GetSheet<Status>();
        jobs = excel.GetSheet<ClassJob>();
        maps = excel.GetSheet<Map>();
        fieldMarkers = excel.GetSheet<FieldMarker>();
        markers = excel.GetSheet<Marker>();
    }

    public ActionInfo? GetAction(uint id) => actionCache.GetOrAdd(id, LoadAction);

    private ActionInfo? LoadAction(uint id)
    {
        if (id > 0xFFFFFF || !actions.TryGetRow(id, out var row))
            return null;
        var omen = row.Omen.ValueNullable?.Path.ToString();
        return new ActionInfo(id, row.Name.ToString(), row.CastType, row.EffectRange, row.XAxisModifier,
                              string.IsNullOrEmpty(omen) ? null : omen, row.TargetArea, row.Cast100ms, row.Icon,
                              row.IsPlayerAction);
    }

    public StatusInfo? GetStatus(uint id) => statusCache.GetOrAdd(id, sid =>
        statuses.TryGetRow(sid, out var row)
            ? new StatusInfo(sid, row.Name.ToString(), row.Icon, row.MaxStacks, row.StatusCategory)
            : null);

    public JobInfo? GetJob(uint id)
    {
        if (!jobs.TryGetRow(id, out var row))
            return Jobs.Get(id);
        var fallback = Jobs.Get(id);
        return new JobInfo(id, row.Abbreviation.ToString(), row.Role, fallback?.Caster ?? false, 62100 + id);
    }

    public MapInfo? GetMap(uint id) => mapCache.GetOrAdd(id, mid =>
        maps.TryGetRow(mid, out var row) && !row.Id.IsEmpty
            ? new MapInfo(mid, row.Id.ToString(), row.SizeFactor, row.OffsetX, row.OffsetY,
                          row.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty)
            : null);

    public uint WaymarkIcon(int slot) =>
        fieldMarkers.TryGetRow((uint)(slot + 1), out var row) ? row.UiIcon : 0u;

    public uint SignIcon(int marker) =>
        markers.TryGetRow((uint)(marker + 1), out var row) && row.Icon > 0 ? (uint)row.Icon : 0u;
}
