using PKHeX.Core;

public class DexIFService(SAV_InfiniteFusion save) : DexGenService(save)
{
    protected override DexItemForm GetDexItemForm(ushort species, bool isOwned, bool isOwnedShiny, byte form, Gender gender)
    {
        var pi = save.Personal.GetFormEntry(species, form);

        var isCaught = isOwned || save.GetCaught(species);
        var isSeen = isCaught || save.GetSeen(species);

        return new DexItemForm(
            Id: DexLoader.GetId(species, form, gender),
            Species: species,
            Form: form,
            Gender: gender,
            Types: GetTypes(pi),
            Abilities: GetAbilities(pi),
            AbilityHidden: GetAbilityHidden(pi),
            BaseStats: GetBaseStats(pi),
            IsSeen: isSeen,
            IsSeenShiny: isOwnedShiny,
            IsSeenAlpha: false,
            IsCaught: isCaught,
            IsOwned: isOwned,
            IsOwnedShiny: isOwnedShiny
        );
    }

    protected override IEnumerable<LanguageID> GetDexLanguages(ushort species)
    {
        return [];
    }

    public override async Task EnableSpeciesForm(EnableSpeciesFormPayload payload)
    {
        if (!save.Personal.IsPresentInGame(payload.Species, payload.Form))
            return;

        if (payload.IsSeen)
            save.SetSeen(payload.Species, true);

        if (payload.IsCaught)
            save.SetCaught(payload.Species, true);
    }

    /// <summary>Write-back for the fusion-matrix Pokédex: mark a fusion (head/body PKHeX species ids) seen/caught.</summary>
    public void EnableFusion(ushort headSpecies, ushort bodySpecies, bool seen, bool caught)
    {
        int head = save.GetIfIndex(headSpecies);
        int body = save.GetIfIndex(bodySpecies);
        if (head <= 0 || body <= 0)
            return;
        if (seen)
            save.SetFusionSeen(head, body, true);
        if (caught)
            save.SetFusionCaught(head, body, true);
    }

    /// <summary>
    /// Surfaces the fusion-matrix Pokédex (head × body). Only cells the save reports as seen or
    /// caught are emitted, indexed by IF dex number (1..577) and resolved to PKHeX species ids via
    /// <see cref="IFSpeciesOrder"/>. The portmanteau name and merged types come from the realized
    /// fusions harvested at load time, falling back to a head/body composition.
    /// </summary>
    public override List<FusionDexItemDTO> GetFusionDex()
    {
        var result = new List<FusionDexItemDTO>();
        var realized = BuildRealizedFusionNames(save);
        int lang = save.Language is > 0 and <= 10 ? save.Language : (int)LanguageID.English;

        for (int head = 1; head <= IFSpeciesOrder.Count; head++)
        for (int body = 1; body <= IFSpeciesOrder.Count; body++)
        {
            bool seen = save.GetFusionSeen(head, body);
            bool caught = save.GetFusionCaught(head, body);
            if (!seen && !caught)
                continue;

            ushort headSp = IFSpeciesOrder.GetSpecies(head);
            ushort bodySp = IFSpeciesOrder.GetSpecies(body);
            if (headSp == 0 || bodySp == 0)
                continue;

            string headName = SpeciesName.GetSpeciesNameGeneration(headSp, lang, 9);
            string bodyName = SpeciesName.GetSpeciesNameGeneration(bodySp, lang, 9);
            List<byte> realizedTypes = null;
            string fusionName = realized.TryGetValue((headSp, bodySp), out var r) && r.Name.Length > 0
                ? r.Name
                : $"{headName}/{bodyName}";
            if (r.Types is { Count: > 0 })
                realizedTypes = r.Types;

            result.Add(new FusionDexItemDTO(
                Id: $"{headSp}_{bodySp}_{save.ID32}",
                SaveId: save.ID32,
                HeadSpecies: headSp,
                BodySpecies: bodySp,
                HeadName: headName,
                BodyName: bodyName,
                FusionName: fusionName,
                Types: realizedTypes ?? GetFusionTypes(headSp, bodySp),
                IsSeen: seen,
                IsCaught: caught
            ));
        }

        return result;
    }

    private static Dictionary<(ushort Head, ushort Body), (string Name, List<byte> Types)> BuildRealizedFusionNames(SAV_InfiniteFusion save)
    {
        var dict = new Dictionary<(ushort, ushort), (string, List<byte>)>();
        foreach (var pair in save.Fusions.Values.Concat(save.PartyFusions.Values))
        {
            if (pair.Head == 0 || pair.Body == 0)
                continue;
            var key = (pair.Head, pair.Body);
            if (!dict.ContainsKey(key))
            {
                var name = pair.FusionName.Length > 0 ? pair.FusionName : string.Empty;
                var types = pair.Types is { Length: > 0 } ? new List<byte>(pair.Types) : [];
                dict[key] = (name, types);
            }
        }
        return dict;
    }

    private List<byte> GetFusionTypes(ushort head, ushort body)
    {
        var types = new List<byte>();
        AddTypes(types, head);
        AddTypes(types, body);
        return [.. types.Distinct()];
    }

    private void AddTypes(List<byte> types, ushort species)
    {
        var pi = save.Personal.GetFormEntry(species, 0);
        if (pi.Type1 != 0)
            types.Add((byte)(pi.Type1 + 1));
        if (pi.Type2 != 0 && pi.Type2 != pi.Type1)
            types.Add((byte)(pi.Type2 + 1));
    }
}
