using PKHeX.Core;
using PKHeX.Core.Ruby;

var path = args.Length > 0 ? args[0] : @"C:\Users\salmo\Documents\GitHub\PKVaultInfinite\InfiniteSave\File A.rxdata";
var bytes = File.ReadAllBytes(path);
var root = (RbHash)RubyMarshal.Load(bytes);

// ---------------------------------------------------------------- storage
Console.WriteLine("=== :storage_system ivars ===");
var storage = root["storage_system"] as RbObject;
if (storage is null)
    Console.WriteLine("  (missing)");
else
{
    DumpIvars(storage);
    if (storage["@boxes"] is RbArray boxes)
    {
        Console.WriteLine($"\n--- boxes ({boxes.Count}) ---");
        for (int b = 0; b < boxes.Count; b++)
        {
            var entry = boxes[b];
            int filled = entry switch
            {
                RbArray a => a.Items.Count(x => x is RbObject),
                RbObject o when o["@pokemon"] is RbArray inner => inner.Items.Count(x => x is RbObject),
                _ => -1,
            };
            Console.WriteLine($"  box[{b,2}] {Describe(entry)} filled={filled}");
            if (b == 0 && entry is RbObject bo)
            {
                Console.WriteLine("    --- ivars of box[0] ---");
                DumpIvars(bo, "      ");
            }
        }
    }
}

// ---------------------------------------------------------------- pokedex
Console.WriteLine("\n=== player.@pokedex ivars ===");
if ((root["player"] as RbObject)?["@pokedex"] is RbObject dex)
{
    DumpIvars(dex);
    foreach (var kv in dex.IVariables)
    {
        if (kv.Value is RbHash h && h.Pairs.Count > 0)
        {
            Console.WriteLine($"  --- {kv.Key} sample ---");
            foreach (var p in h.Pairs.Take(6))
                Console.WriteLine($"      {p.Key} = {Describe(p.Value)}");
        }
        else if (kv.Value is RbArray a && a.Count > 0)
        {
            Console.WriteLine($"  --- {kv.Key} sample ---");
            for (int i = 0; i < Math.Min(6, a.Count); i++)
                Console.WriteLine($"      [{i}] = {Describe(a[i])}");
        }
    }
}

// ---------------------------------------------------------------- symbol audit
Console.WriteLine("\n=== SYMBOL AUDIT ===");
var mons = new List<RbObject>();
if ((root["player"] as RbObject)?["@party"] is RbArray party)
    mons.AddRange(party.Items.OfType<RbObject>());
if (storage?["@boxes"] is RbArray bx)
{
    foreach (var entry in bx.Items)
    {
        IEnumerable<RbValue?> slots = entry switch
        {
            RbArray a => a.Items,
            RbObject o when o["@pokemon"] is RbArray inner => inner.Items,
            _ => [],
        };
        mons.AddRange(slots.OfType<RbObject>());
    }
}
Console.WriteLine($"Pokemon objects found: {mons.Count}");

var speciesSyms = new SortedSet<string>(StringComparer.Ordinal);
var moveSyms = new SortedSet<string>(StringComparer.Ordinal);
var itemSyms = new SortedSet<string>(StringComparer.Ordinal);
var ballSyms = new SortedSet<string>(StringComparer.Ordinal);
var abilitySyms = new SortedSet<string>(StringComparer.Ordinal);
var natureSyms = new SortedSet<string>(StringComparer.Ordinal);
var fusedCount = 0;

foreach (var m in mons)
{
    var sd = m["@species_data"] as RbObject;
    if (sd?.ClassName.Name == "GameData::FusedSpecies")
    {
        fusedCount++;
        AddSpecies(sd["@head_pokemon"]);
        AddSpecies(sd["@body_pokemon"]);
    }
    else
    {
        AddSpecies(sd);
    }
    if (m["@moves"] is RbArray mvs)
        foreach (var mv in mvs.Items.OfType<RbObject>())
            if (mv["@id"] is RbSymbol s) moveSyms.Add(s.Name);
    if (m["@learned_moves"] is RbArray lm)
        foreach (var s in lm.Items.OfType<RbSymbol>()) moveSyms.Add(s.Name);
    if (m["@item"] is RbSymbol it) itemSyms.Add(it.Name);
    if (m["@poke_ball"] is RbSymbol pb) ballSyms.Add(pb.Name);
    if (m["@ability"] is RbSymbol ab) abilitySyms.Add(ab.Name);
    if (m["@nature"] is RbSymbol na) natureSyms.Add(na.Name);
}

void AddSpecies(RbValue? v)
{
    if (v is RbObject o && o["@id"] is RbSymbol s) speciesSyms.Add(s.Name);
    else if (v is RbSymbol sym) speciesSyms.Add(sym.Name);
}

Console.WriteLine($"fused: {fusedCount}/{mons.Count}");
Report("SPECIES", speciesSyms, IFNameLookup.GetSpecies);
Report("MOVES", moveSyms, IFNameLookup.GetMove);
Report("ITEMS", itemSyms, IFNameLookup.GetItem);
Console.WriteLine($"BALLS   ({ballSyms.Count}): {string.Join(", ", ballSyms)}");
Console.WriteLine($"NATURES ({natureSyms.Count}): {string.Join(", ", natureSyms)}");
Console.WriteLine($"ABILITIES ({abilitySyms.Count}) unmapped: {string.Join(", ", abilitySyms.Where(a => !TryAbility(a)))}");

static bool TryAbility(string name) => IFNameLookup.GetAbility(name) != 0;

static void Report(string label, SortedSet<string> syms, Func<string, ushort> lookup)
{
    var bad = syms.Where(s => lookup(s) == 0).ToList();
    Console.WriteLine($"{label,-8} total={syms.Count,-4} unmapped={bad.Count}");
    if (bad.Count != 0)
        Console.WriteLine($"         -> {string.Join(", ", bad)}");
}

static void DumpIvars(RbObject obj, string pad = "  ")
{
    foreach (var kv in obj.IVariables)
        Console.WriteLine($"{pad}{kv.Key,-22} = {Describe(kv.Value)}");
}

static string Describe(RbValue? v) => v switch
{
    null => "null",
    RbNil => "nil",
    RbSymbol s => ":" + s.Name,
    RbString s => $"\"{(s.Text.Length > 60 ? s.Text[..60] + "..." : s.Text)}\"",
    RbFixnum f => f.Value.ToString(),
    RbBignum b => b.Value.ToString(),
    RbFloat f => f.Value.ToString("0.###"),
    RbBool b => b.Value ? "true" : "false",
    RbArray a => $"Array({a.Count})" + (a.Count > 0 ? $" first={Brief(a[0])}" : ""),
    RbHash h => $"Hash({h.Pairs.Count})" + (h.Pairs.Count > 0 ? $" first={h.Pairs[0].Key}={Brief(h.Pairs[0].Value)}" : ""),
    RbObject o => $"#{o.ClassName.Name}(ivars={o.IVariables.Count})",
    RbStruct s => $"Struct#{s.ClassName.Name}",
    RbUserDef u => $"UserDef#{u.ClassName.Name}({u.Data.Length}b)",
    _ => v.GetType().Name,
};

static string Brief(RbValue? v) => v switch
{
    null => "null",
    RbNil => "nil",
    RbSymbol s => ":" + s.Name,
    RbString s => $"\"{s.Text}\"",
    RbFixnum f => f.Value.ToString(),
    RbObject o => $"#{o.ClassName.Name}",
    RbArray a => $"Array({a.Count})",
    _ => v.GetType().Name,
};
