using System.Text;
using System.Text.Json.Nodes;
using OpenVerse.Engine;
using Xunit.Abstractions;

namespace OpenVerse.Tests;

// Replays what EngineRecorder saved from live matches through a real MirrorPair, offline. The pair takes exactly the
// inputs it took in the match, so the first message a board could not apply is the first place it left the match,
// and the zones printed around it say what it was missing. Runs only when OPENVERSE_REPLAY_DIR points at recordings
// and the engine is built; the report goes to replay-report.txt in that folder (or OPENVERSE_REPLAY_OUT).
//
// Each recording is replayed twice: as live does it (ShadowReconciler only), and with SetDeckMirror installed from
// the recorded idxChangeSeeds, which is the open question in BattleHub.BeginShadow
[Collection("Engine")]
public class EngineReplayTests
{
    readonly ITestOutputHelper _out;
    public EngineReplayTests(ITestOutputHelper o) => _out = o;

    const int Detailed = 3;

    sealed record Begin(int Seed, int[] ADeck, int[] BDeck, int[] AHand, int[] BHand, int AIdxSeed, int BIdxSeed);
    sealed record Obs(int Line, string Uri, bool FromA, JsonObject Body);

    [Fact]
    public void ReplaysRecordedMatches()
    {
        var dir = Environment.GetEnvironmentVariable("OPENVERSE_REPLAY_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        Assert.True(Fixtures.EngineDlls, "OPENVERSE_REPLAY_DIR is set but the engine is not built into the test output");
        var csv = Fixtures.CardMasterCsv();
        Assert.True(csv is not null, "the engine is built but card_master_full.csv.gz is not under release/server/data");

        var report = new StringBuilder();
        var files = Directory.GetFiles(dir, "*.jsonl").OrderBy(f => f).ToList();
        report.AppendLine($"{files.Count} recording(s) in {dir}");
        foreach (var file in files)
        {
            var (begin, rows) = Load(file);
            if (begin is null) { report.AppendLine($"\n## {Path.GetFileName(file)}: no begin line"); continue; }
            foreach (var deckMirror in new[] { false, true })
                Replay(csv!, Path.GetFileName(file), begin, rows, deckMirror, report);
        }

        var outPath = Environment.GetEnvironmentVariable("OPENVERSE_REPLAY_OUT") is { Length: > 0 } o
            ? o : Path.Combine(dir, "replay-report.txt");
        File.WriteAllText(outPath, report.ToString());
        _out.WriteLine(report.ToString());
    }

    static (Begin?, List<Obs>) Load(string file)
    {
        Begin? begin = null;
        var rows = new List<Obs>();
        var n = 0;
        foreach (var raw in File.ReadLines(file))
        {
            n++;
            if (JsonNode.Parse(raw) is not JsonObject o) continue;
            switch ((string?)o["t"])
            {
                case "begin":
                    begin = new Begin((int)o["seed"]!, Ints(o["aDeck"]), Ints(o["bDeck"]), Ints(o["aHand"]), Ints(o["bHand"]),
                                      (int?)o["aIdxSeed"] ?? -1, (int?)o["bIdxSeed"] ?? -1);
                    break;
                case "obs" when o["body"] is JsonObject body:
                    rows.Add(new Obs(n, (string)o["uri"]!, (bool)o["fromA"]!, body));
                    break;
            }
        }
        return (begin, rows);
    }

    static int[] Ints(JsonNode? n) => n is JsonArray a ? a.Select(x => (int)x!).ToArray() : [];

    static void Replay(string csv, string name, Begin b, List<Obs> rows, bool deckMirror, StringBuilder report)
    {
        report.AppendLine($"\n## {name}  [{(deckMirror ? "SetDeckMirror ON" : "as live")}]  rows={rows.Count}");
        using var pair = MirrorPair.Boot(csv, "replay");
        if (!pair.Ready) { report.AppendLine($"pair did not boot: {pair.Failure}"); return; }

        var startLog = new List<string>();
        pair.Begin(b.Seed, aFirst: true, b.ADeck, b.BDeck, b.AHand, b.BHand, startLog.Add,
                   deckMirror ? b.AIdxSeed : -1, deckMirror ? b.BIdxSeed : -1);
        pair.WaitIdle();
        foreach (var l in startLog.Where(l => !l.Contains("observing"))) report.AppendLine($"start: {l}");

        var failures = 0;
        var byReason = new Dictionary<string, int>();
        string zonesBefore = Zones(pair);
        Obs? previous = null;
        foreach (var r in rows)
        {
            var log = new List<string>();
            pair.Observe(r.Uri, r.Body, r.FromA, log.Add);
            pair.WaitIdle();
            var bad = log.Where(l => l.Contains("not applied")).ToList();
            if (bad.Count > 0)
            {
                failures++;
                foreach (var l in bad)
                {
                    var reason = l[(l.IndexOf("not applied", StringComparison.Ordinal))..].Split('\n')[0];
                    var key = $"{r.Uri}/{Type(r.Body)}: {reason}";
                    byReason[key] = byReason.GetValueOrDefault(key) + 1;
                }
                if (failures <= Detailed)
                {
                    report.AppendLine($"\n-- failure {failures} at line {r.Line}: {r.Uri} type={Type(r.Body)} playIdx={r.Body["playIdx"]} fromA={r.FromA}");
                    foreach (var l in bad) report.AppendLine("   " + l.Replace("\n", "\n   "));
                    if (previous is not null)
                        report.AppendLine($"   previous: line {previous.Line} {previous.Uri} type={Type(previous.Body)} playIdx={previous.Body["playIdx"]} fromA={previous.FromA}");
                    report.AppendLine("   message: " + Clip(r.Body.ToJsonString(), 1500));
                    report.AppendLine("   boards BEFORE it:\n" + Indent(zonesBefore));
                }
            }
            zonesBefore = Zones(pair);
            previous = r;
        }

        report.AppendLine($"\nunapplied rows: {failures} / {rows.Count}");
        foreach (var (k, v) in byReason.OrderByDescending(kv => kv.Value)) report.AppendLine($"  {v}x {k}");
        report.AppendLine("final boards:\n" + Indent(zonesBefore));
    }

    static string Zones(MirrorPair pair)
    {
        pair.A.TryZones(out var a);
        pair.B.TryZones(out var b);
        return "A (its own side is P):\n" + a + "\nB (its own side is P):\n" + b;
    }

    static string Type(JsonObject body) => body["type"]?.ToString() ?? "-";
    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "...";
    static string Indent(string s) => "      " + s.Replace("\n", "\n      ");
}
