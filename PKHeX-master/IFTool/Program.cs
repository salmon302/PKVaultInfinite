using PKHeX.Core;
using PKHeX.Core.Ruby;

var path = args.Length > 0 ? args[0] : @"C:\Users\salmo\Documents\GitHub\PKVaultInfinite\InfiniteSave\File A.rxdata";
var bytes = File.ReadAllBytes(path);

var sw = System.Diagnostics.Stopwatch.StartNew();
Console.WriteLine($"IsMatch: {SAV_InfiniteFusion.IsMatch(bytes)}");
if (!SaveUtil.TryGetSaveFile(bytes, out var sav, path))
{
    Console.WriteLine("NOT DETECTED by SaveUtil");
    return;
}
sw.Stop();

var iff = (SAV_InfiniteFusion)sav;
Console.WriteLine($"Detected {sav.GetType().Name} in {sw.ElapsedMilliseconds} ms");
Console.WriteLine(sav.MiscSaveInfo());
Console.WriteLine($"version={sav.Version} gen={sav.Generation} ctx={sav.Context} lang={sav.Language}");
Console.WriteLine($"OT={sav.OT} ID32={sav.ID32} TID={sav.TID16} SID={sav.SID16} gender={sav.Gender} money={sav.Money} playtime={sav.PlayedHours}h{sav.PlayedMinutes:00}m{sav.PlayedSeconds:00}s");
Console.WriteLine($"BoxCount={sav.BoxCount} slots/box={sav.BoxSlotCount} CurrentBox={sav.CurrentBox} PartyCount={sav.PartyCount} HasParty={sav.HasParty} HasBox={sav.HasBox}");
Console.WriteLine($"buffer={sav.Buffer.Length} bytes; exported={sav.Write().Length} bytes (orig {bytes.Length}); byte-identical={sav.Write().Span.SequenceEqual(bytes)}");

Console.WriteLine("\n--- party ---");
for (int i = 0; i < 6; i++)
{
    var pk = sav.GetPartySlotAtIndex(i);
    var fus = iff.PartyFusions.TryGetValue(i, out var f) ? $"  [fusion {f}]" : "";
    Console.WriteLine($"  [{i}] {Show(pk)}{fus}");
}

Console.WriteLine("\n--- storage ---");
int total = 0, shown = 0, badSpecies = 0, badMove = 0;
for (int b = 0; b < sav.BoxCount; b++)
{
    for (int s = 0; s < sav.BoxSlotCount; s++)
    {
        var pk = sav.GetBoxSlotAtIndex(b, s);
        if (pk.Species == 0) continue;
        total++;
        if (pk.Species > sav.MaxSpeciesID) badSpecies++;
        if (pk.Move1 == 0) badMove++;
        if (shown++ < 20)
        {
            var fus = iff.Fusions.TryGetValue((b * 30) + s, out var f) ? $"  [fusion {f}]" : "";
            Console.WriteLine($"  [{iff.GetBoxName(b)}/{s:00}] {Show(pk)}{fus}");
        }
    }
}
Console.WriteLine($"  ... stored total={total}, fusions={iff.Fusions.Count}, bad species={badSpecies}, empty Move1={badMove}");

// fusion-matrix dex read-back
Console.WriteLine("\n--- fusion-matrix dex ---");
{
    int seen = 0, caught = 0;
    var samples = new List<(int h, int b)>();
    for (int h = 1; h <= IFSpeciesOrder.Count; h++)
    for (int b = 1; b <= IFSpeciesOrder.Count; b++)
    {
        if (iff.GetFusionSeen(h, b)) { seen++; if (samples.Count < 10) samples.Add((h, b)); }
        if (iff.GetFusionCaught(h, b)) caught++;
    }
    Console.WriteLine($"  fusion seen={seen} caught={caught} (of {IFSpeciesOrder.Count * IFSpeciesOrder.Count})");
    foreach (var (h, b) in samples)
    {
        var hs = IFSpeciesOrder.GetSpecies(h);
        var bs = IFSpeciesOrder.GetSpecies(b);
        Console.WriteLine($"  fusion[{h},{b}] head={hs}({SpeciesName.GetSpeciesNameGeneration(hs, 2, 9)}) body={bs}({SpeciesName.GetSpeciesNameGeneration(bs, 2, 9)}) seen={iff.GetFusionSeen(h, b)} caught={iff.GetFusionCaught(h, b)}");
    }
}

// dump one GameData::FusedSpecies sample
Console.WriteLine("\n--- FusedSpecies sample ---");
{
    var rf = sav.GetType().GetField("_root", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    var root = rf?.GetValue(sav) as RbValue;
    RbObject? found = null;
    Walk(root, v =>
    {
        if (found == null && v is RbObject o && o.ClassName.Name == "GameData::FusedSpecies")
            found = o;
    });
    if (found != null)
    {
        foreach (var key in new[] { "@real_name", "@type1", "@type2", "@types", "@abilities", "@hidden_abilities", "@base_stats", "@head_pokemon", "@body_pokemon", "@height", "@weight" })
            Console.WriteLine($"  {key} => {found[key]}");
    }
    else
    {
        Console.WriteLine("  none found");
    }
}

// fusion-matrix write-back round trip
Console.WriteLine("\n--- fusion write-back ---");
{
    int fh = -1, fb = -1;
    for (int h = 1; h <= IFSpeciesOrder.Count && fh < 0; h++)
    for (int b = 1; b <= IFSpeciesOrder.Count; b++)
    {
        if (!iff.GetFusionSeen(h, b)) { fh = h; fb = b; break; }
    }
    if (fh > 0)
    {
        iff.SetFusionSeen(fh, fb, true);
        iff.SetFusionCaught(fh, fb, true);
        var wb = iff.Write();
        if (!SaveUtil.TryGetSaveFile(wb, out var sav3, path))
        {
            Console.WriteLine("  RELOAD FAILED");
        }
        else
        {
            var iff3 = (SAV_InfiniteFusion)sav3;
            Console.WriteLine($"  fusion[{fh},{fb}] seenAfter={iff3.GetFusionSeen(fh, fb)} caughtAfter={iff3.GetFusionCaught(fh, fb)}");
        }
    }
    else
    {
        Console.WriteLine("  (all fusions already seen — nothing to flip)");
    }
}

// legality smoke test on a couple of non-fused mons
Console.WriteLine("\n--- sanity ---");
foreach (var pk in sav.PartyData)
    Console.WriteLine($"  {pk.Nickname,-12} valid-species={pk.Species <= sav.MaxSpeciesID} lvl={pk.CurrentLevel} exp={pk.EXP} ball={(Ball)pk.Ball} item={pk.HeldItem} egg={pk.IsEgg} shiny={pk.IsShiny} chk-ok={pk.ChecksumValid}");

// dex read-back
Console.WriteLine("\n--- dex (Pokédex) ---");
Console.WriteLine($"  HasPokeDex={sav.HasPokeDex} MaxSpeciesID={sav.MaxSpeciesID} SeenCount={sav.SeenCount} CaughtCount={sav.CaughtCount}");
int shownSeen = 0;
for (ushort sp = 1; sp <= sav.MaxSpeciesID; sp++)
{
    if (sav.GetSeen(sp))
    {
        if (shownSeen++ < 25)
            Console.WriteLine($"  seen[{sp}] {SpeciesName.GetSpeciesNameGeneration(sp, 2, 9),-12} caught={sav.GetCaught(sp)}");
    }
}

Console.WriteLine("\n--- mini round trip ---");
{
    var g = new RbHash(new(), null);
    var s = new RbString(System.Text.Encoding.UTF8.GetBytes("hello"), "UTF-8");
    s.SetIvar(new RbSymbol("E"), new RbString(System.Text.Encoding.UTF8.GetBytes("UTF-8"), "UTF-8"));
    g.Pairs.Add(new KeyValuePair<RbValue, RbValue?>(new RbSymbol("a"), s));
    g.Pairs.Add(new KeyValuePair<RbValue, RbValue?>(new RbSymbol("b"), new RbBignum(System.Numerics.BigInteger.Parse("3291957797"))));
    g.Pairs.Add(new KeyValuePair<RbValue, RbValue?>(new RbSymbol("c"), new RbArray(new List<RbValue?> { new RbFixnum(5), new RbBool(true) })));
    var round = RubyMarshal.Save(g);
    var g2 = (RbHash)RubyMarshal.Load(round);
    Console.WriteLine($"  mini ok={g2.Pairs.Count == 3} len={round.Length}");
    // extended types
    var g3 = new RbHash(new(), null);
    g3.Pairs.Add(new KeyValuePair<RbValue, RbValue?>(new RbSymbol("f"), new RbFloat(0.5)));
    g3.Pairs.Add(new KeyValuePair<RbValue, RbValue?>(new RbSymbol("r"), new RbRegexp("ab.*")));
    g3.Pairs.Add(new KeyValuePair<RbValue, RbValue?>(new RbSymbol("u"), new RbUserDef(new RbSymbol("Time"), new byte[] { 1, 2, 3 })));
    g3.Pairs.Add(new KeyValuePair<RbValue, RbValue?>(new RbSymbol("m"), new RbModule("Foo")));
    g3.Pairs.Add(new KeyValuePair<RbValue, RbValue?>(new RbSymbol("cl"), new RbClass("Bar")));
    var s2 = new RbString(System.Text.Encoding.UTF8.GetBytes("enc"), "UTF-8");
    s2.SetIvar(new RbSymbol("E"), new RbSymbol("UTF-8")); // symbol-valued encoding
    g3.Pairs.Add(new KeyValuePair<RbValue, RbValue?>(new RbSymbol("es"), s2));
    var st = new RbStruct(new RbSymbol("MyStruct"), new());
    st.Members[new RbSymbol("@a")] = new RbFixnum(9);
    g3.Pairs.Add(new KeyValuePair<RbValue, RbValue?>(new RbSymbol("st"), st));
    var r3 = RubyMarshal.Save(g3);
    var g4 = (RbHash)RubyMarshal.Load(r3);
    Console.WriteLine($"  mini2 ok={g4.Pairs.Count == 7}");
    // nested object + cycle
    var inner = new RbObject(new RbSymbol("Foo"), new());
    inner.IVariables[new RbSymbol("@x")] = new RbFixnum(7);
    var outer = new RbHash(new(), null);
    outer.Pairs.Add(new KeyValuePair<RbValue, RbValue?>(new RbSymbol("o"), inner));
    inner.IVariables[new RbSymbol("@self")] = inner; // cycle
    var r2 = RubyMarshal.Save(outer);
    var outer2 = (RbHash)RubyMarshal.Load(r2);
    Console.WriteLine($"  cycle ok={outer2.Pairs.Count == 1}");
}

Console.WriteLine("\n--- inspect struct classes ---");
{
    var f = sav.GetType().GetField("_root", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    var root = f?.GetValue(sav) as RbValue;
    int structs = 0;
    Walk(root, v =>
    {
        if (v is RbStruct st)
        {
            structs++;
            if (structs <= 5)
                Console.WriteLine($"  struct[{structs}] class type={st.ClassName.GetType().Name} name={st.ClassName}");
        }
    });
    Console.WriteLine($"  total structs={structs}");
}

Console.WriteLine("\n--- base_stats round trip ---");
{
    var f = sav.GetType().GetField("_root", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    var root = f?.GetValue(sav) as RbValue;
    DumpBS(root, "loaded");
    var outb = sav.Write();
    try
    {
        var r2 = RubyMarshal.Load(outb.ToArray());
        DumpBS(r2, "after-save");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  after-save reload EX: {ex.Message}");
        int p = 521;
        if (System.Text.RegularExpressions.Regex.Match(ex.Message, @"at pos (\d+)") is { Success: true } m2)
            p = int.Parse(m2.Groups[1].Value);
        var win = outb.ToArray().Skip(Math.Max(0, p - 40)).Take(80).ToArray();
        Console.WriteLine($"  OUT around pos {p}: {BitConverter.ToString(win)}");
    }
}
void DumpBS(RbValue? root, string tag)
{
    if (root is not RbHash h) { Console.WriteLine($"  [{tag}] root not hash"); return; }
    var player = h[new RbSymbol("player")] as RbObject;
    var party = player?["@party"] as RbArray;
    var mon0 = party?.Items[0] as RbObject;
    var sd = mon0?["@species_data"] as RbObject;
    var bs = sd?["@base_stats"] as RbHash;
    if (bs is null) { Console.WriteLine($"  [{tag}] no @base_stats (mon0={mon0?.GetType().Name})"); return; }
    Console.WriteLine($"  [{tag}] @base_stats pairs={bs.Pairs.Count}");
    foreach (var kv in bs.Pairs)
        Console.WriteLine($"    {kv.Key} => {kv.Value}");
}

void Walk(RbValue? v, Action<RbValue> action)
{
    var seen = new HashSet<RbValue>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
    void Rec(RbValue? x)
    {
        if (x is null || !seen.Add(x)) return;
        action(x);
        switch (x)
        {
            case RbObject o: foreach (var kv in o.IVariables) { Rec(kv.Key); Rec(kv.Value); } break;
            case RbStruct s: foreach (var kv in s.Members) { Rec(kv.Key); Rec(kv.Value); } break;
            case RbArray a: foreach (var it in a.Items) Rec(it); break;
            case RbHash h: foreach (var kv in h.Pairs) { Rec(kv.Key); Rec(kv.Value); } break;
        }
    }
    Rec(v);
}

Console.WriteLine("\n--- write-back round trip ---");
var beforeBytes = sav.Write();

ushort flip = 0;
for (ushort sp = 1; sp <= sav.MaxSpeciesID; sp++)
{
    if (!sav.GetSeen(sp) && IFSpeciesOrder.GetIndex(sp) > 0) { flip = sp; break; }
}
bool seenBefore = sav.GetSeen(flip);
sav.SetSeen(flip, !seenBefore);
sav.SetCaught(flip, true);
var writtenBytes = sav.Write();
Console.WriteLine($"  wrote {writtenBytes.Length} bytes (orig {beforeBytes.Length}); byte-identical-without-edit={writtenBytes.Span.SequenceEqual(beforeBytes.Span)}");

// Idempotency: Save(Load(x)) must be stable across many passes (no structural drift / shrink).
// Test against the RAW original file bytes (the task's requirement).
var origBytes = File.ReadAllBytes(path);
var g0 = RubyMarshal.Load(origBytes);
var w0 = RubyMarshal.Save(g0);
var w0b = RubyMarshal.Save(g0);
Console.WriteLine($"  Save(g0) deterministic: {w0.AsSpan().SequenceEqual(w0b.AsSpan())}  (len {w0.Length})");
var wPrev = w0;

// Localize the divergence: first byte offset where w0 and w1=Save(Load(w0)) differ.
var w1 = RubyMarshal.Save(RubyMarshal.Load(w0.ToArray()));
int diffPos = -1;
for (int i = 0; i < Math.Min(w0.Length, w1.Length); i++)
    if (w0[i] != w1[i]) { diffPos = i; break; }
Console.WriteLine($"  w0 vs w1 first-diff pos={diffPos} (w0len={w0.Length} w1len={w1.Length})");
if (diffPos >= 0)
{
    int s = Math.Max(0, diffPos - 12);
    int e = Math.Min(Math.Min(w0.Length, w1.Length), diffPos + 20);
    var sb0 = new System.Text.StringBuilder(); for (int i = s; i < e; i++) sb0.Append($"{w0[i]:X2} ");
    var sb1 = new System.Text.StringBuilder(); for (int i = s; i < e; i++) sb1.Append($"{w1[i]:X2} ");
    Console.WriteLine($"   w0[{s}..]: {sb0}");
    Console.WriteLine($"   w1[{s}..]: {sb1}");
}

bool converged = true;
int passes = 8;
List<byte[]> outs = new() { w0 };
for (int p = 1; p <= passes; p++)
{
    var g = RubyMarshal.Load(wPrev.ToArray());
    var wNext = RubyMarshal.Save(g);
    bool stable = wNext.AsSpan().SequenceEqual(wPrev.AsSpan());
    if (!stable) converged = false;
    outs.Add(wNext);
    Console.WriteLine($"  pass {p}: len={wNext.Length} stable-vs-prev={stable}");
    wPrev = wNext;
}
// Detect a fixed-point / cycle: does any later pass equal the first re-serialization?
bool reachesW0 = outs.Skip(1).Any(w => w.AsSpan().SequenceEqual(w0.AsSpan()));
Console.WriteLine($"  Save(Load(orig))==w0 first-pass stable: {outs[1].AsSpan().SequenceEqual(w0.AsSpan())}");
Console.WriteLine($"  IDEMPOTENT (Save(Load(x)) stable over {passes} passes): {converged}  (settles-to-w0={reachesW0})");

// Functional persistence via the real SaveFile path.
if (!SaveUtil.TryGetSaveFile(writtenBytes, out var sav2, path))
{
    Console.WriteLine("  RELOAD FAILED (not detected)");
    return;
}
var iff2 = (SAV_InfiniteFusion)sav2;
bool seenAfter = iff2.GetSeen(flip);
bool caughtAfter = iff2.GetCaught(flip);
Console.WriteLine($"  flip species={flip} ({SpeciesName.GetSpeciesNameGeneration(flip, 2, 9)}): seenBefore={seenBefore} seenAfter={seenAfter} caughtAfter={caughtAfter}");
Console.WriteLine($"  OT match={iff2.OT == iff.OT} money match={iff2.Money == iff.Money} box match={iff2.BoxCount == iff.BoxCount} party match={iff2.PartyCount == iff.PartyCount}");
Console.WriteLine($"  SeenCount before={sav.SeenCount} after={iff2.SeenCount}  CaughtCount before={sav.CaughtCount} after={iff2.CaughtCount}");

static string Show(PKM pk) =>
    $"{pk.Species,4} {SpeciesName.GetSpeciesNameGeneration(pk.Species, 2, 9),-11} Lv{pk.CurrentLevel,-3} '{pk.Nickname,-12}' " +
    $"nat={pk.Nature,-8} IV={pk.IV_HP:00}/{pk.IV_ATK:00}/{pk.IV_DEF:00}/{pk.IV_SPA:00}/{pk.IV_SPD:00}/{pk.IV_SPE:00} " +
    $"mv={MoveName(pk.Move1)},{MoveName(pk.Move2)},{MoveName(pk.Move3)},{MoveName(pk.Move4)}";

static string MoveName(ushort m) => m == 0 ? "-" : GameInfo.GetStrings("en").movelist[m];
