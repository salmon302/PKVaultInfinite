using PKHeX.Core;

/// <summary>
/// A single cell of the Infinite Fusion fusion-matrix Pokédex (head × body), keyed by the two
/// component PKHeX species ids. Surfaced by <see cref="DexIFService.GetFusionDex"/>.
/// </summary>
public record FusionDexItemDTO(
    string Id,
    uint SaveId,
    ushort HeadSpecies,
    ushort BodySpecies,
    string HeadName,
    string BodyName,
    string FusionName,
    List<byte> Types,
    bool IsSeen,
    bool IsCaught
) : IWithId;
