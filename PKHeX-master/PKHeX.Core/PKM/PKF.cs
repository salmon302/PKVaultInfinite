using System;
using static System.Buffers.Binary.BinaryPrimitives;

namespace PKHeX.Core;

/// <summary>
/// Pokémon Infinite Fusion fusion entity.
/// </summary>
/// <remarks>
/// Inherits the Gen9 (SV) layout so its byte storage and encryption are identical to <see cref="PK9"/>, but
/// carries the fusion's head/body species in the reserved <see cref="PK9"/> extra bytes (0x96–0x99). The
/// entity's own <see cref="EntityContext"/> is <see cref="EntityContext.Gen9Fusion"/>, isolating it as a
/// conversion island, while the owning <see cref="SAV_InfiniteFusion"/> save stays <see cref="EntityContext.Gen9"/>.
/// <see cref="PKM.Species"/> remains the head species; a fusion is "present" iff <see cref="BodySpecies"/> is non-zero.
/// </remarks>
public sealed class PKF : PK9
{
    public override EntityContext Context => EntityContext.Gen9Fusion;

    public PKF() : base() { }

    public PKF(Memory<byte> data) : base(data) { }

    public PKF(PK9 src) : base(src.Data.ToArray()) { }

    /// <summary> Head species (national dex id). Also the inherited <see cref="PKM.Species"/>. </summary>
    public ushort HeadSpecies
    {
        get => ReadUInt16LittleEndian(Data[0x96..]);
        set => WriteUInt16LittleEndian(Data[0x96..], value);
    }

    /// <summary> Body species (national dex id). Non-zero indicates a realized fusion. </summary>
    public ushort BodySpecies
    {
        get => ReadUInt16LittleEndian(Data[0x98..]);
        set => WriteUInt16LittleEndian(Data[0x98..], value);
    }

    /// <summary> True when this entity is a fusion (has a body species). </summary>
    public bool IsFusion => BodySpecies != 0;

    public override PKF Clone() => new(this);
}
