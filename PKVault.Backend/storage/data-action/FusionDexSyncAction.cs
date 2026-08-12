using PKHeX.Core;

public record FusionDexSyncActionInput(uint[] saveIds);

public class FusionDexSyncAction(
    ILogger<FusionDexSyncAction> log,
    DexService dexService,
    ISavesLoadersService savesLoadersService
) : DataAction<FusionDexSyncActionInput>
{
    protected override async Task<DataActionPayload> Execute(FusionDexSyncActionInput input, DataUpdateFlags flags)
    {
        if (input.saveIds.Length < 2)
        {
            throw new ArgumentException($"Saves IDs should be at least 2");
        }

        var saveLoaders = input.saveIds.Select(id => id == FakeSaveFile.Default.ID32
            ? null
            : savesLoadersService.GetLoaders(id)
        ).ToList();

        var dex = await dexService.GetFusionDex(input.saveIds);

        // Merge every save's fusion seen/caught state into a single set of (head, body) flags.
        var merged = new Dictionary<(ushort Head, ushort Body), (bool Seen, bool Caught)>();
        foreach (var entries in dex.Values)
        {
            foreach (var entry in entries)
            {
                var key = (entry.HeadSpecies, entry.BodySpecies);
                if (!merged.TryGetValue(key, out var cur))
                {
                    merged[key] = (entry.IsSeen, entry.IsCaught);
                }
                else
                {
                    merged[key] = (cur.Seen || entry.IsSeen, cur.Caught || entry.IsCaught);
                }
            }
        }

        log.LogDebug($"Fusion dex sync: {merged.Count} unique fusions across {input.saveIds.Length} saves");

        await Task.WhenAll(
            saveLoaders.Select(async saveLoader =>
            {
                var service = dexService.GetDexService(saveLoader?.Save ?? new(FakeSaveFile.Default));
                if (service is not DexIFService ifService)
                {
                    return;
                }

                foreach (var (key, value) in merged)
                {
                    if (value.Seen || value.Caught)
                    {
                        ifService.EnableFusion(key.Head, key.Body, value.Seen, value.Caught);
                    }
                }

                saveLoader?.Pkms.HasWritten = true;
            })
        );

        flags.Dex.All = true;

        return new(
            type: DataActionType.DEX_SYNC,
            parameters: []
        );
    }
}
