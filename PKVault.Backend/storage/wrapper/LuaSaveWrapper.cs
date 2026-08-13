using PKHeX.Core;
using PKHeX.Core.Saves.Gen1.Lua;

/**
 * SaveWrapper for Gen1 "Recomp" project Lua save files (.lua).
 * Overrides GetSaveFileData to serialize the internal SAV1 binary back to Lua text,
 * enabling round-trip editing of recomp save slots through PKVault.
 */
public class LuaSaveWrapper(SAV1 save) : SaveWrapper(save)
{
    public override byte[] GetSaveFileData()
    {
        return LuaSaveConverter.SAV1ToLua(GetSave() as SAV1 ?? save);
    }
}
