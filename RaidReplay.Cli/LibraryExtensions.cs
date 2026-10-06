using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;

namespace RaidReplay.Cli;

internal static class OptionsExt
{
    public static LogLibrary CreateLibrary(this Options o)
    {
        var reg = CliApp.Registry(o);
        return new LogLibrary(o.LogsDir, o.CacheDir, new EncounterObserverFactory(reg), reg.HashFor);
    }
}
