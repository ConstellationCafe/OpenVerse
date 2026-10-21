using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenVerse.Battle;

// What the mirror pair of each match was fed, one JSON object per line: a "begin" line with the decks, opening hands
// and seeds the pair was started from, then one "obs" line per message in the order the pair took them. Replaying the
// file through a MirrorPair offline (EngineReplayTests) reproduces the live boards exactly, so a board that parts
// from the match can be chased in seconds instead of one real game per guess.
//
// Board data only: card ids, indices, seeds. No account names or ids reach these files.
// On whenever the engine is (OPENVERSE_ENGINE_RECORD=0 turns it off), into OPENVERSE_ENGINE_RECORD_DIR or
// /tmp/ov-engine-rec, keeping the newest MaxFiles matches.
public static class EngineRecorder
{
    const int MaxFiles = 40;
    static readonly bool Enabled = Environment.GetEnvironmentVariable("OPENVERSE_ENGINE_RECORD")?.Trim() != "0";
    static readonly string Dir = Environment.GetEnvironmentVariable("OPENVERSE_ENGINE_RECORD_DIR") is { Length: > 0 } d
        ? d : Path.Combine(Path.GetTempPath(), "ov-engine-rec");
    static readonly Dictionary<string, string> Files = new();
    static readonly object Gate = new();

    public static void Begin(string battleId, int seed, int[] aDeck, int[] bDeck, int[] aHand, int[] bHand,
                             int aIdxSeed, int bIdxSeed)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var path = Path.Combine(Dir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Safe(battleId)}.jsonl");
                Files[battleId] = path;
                var line = new JsonObject
                {
                    ["t"] = "begin",
                    ["seed"] = seed,
                    ["aDeck"] = Arr(aDeck), ["bDeck"] = Arr(bDeck),
                    ["aHand"] = Arr(aHand), ["bHand"] = Arr(bHand),
                    ["aIdxSeed"] = aIdxSeed, ["bIdxSeed"] = bIdxSeed,
                };
                File.WriteAllText(path, line.ToJsonString() + "\n");
                Prune();
            }
        }
        catch (Exception e) { Console.WriteLine($"[{battleId}] engine record failed: {e.Message}"); }
    }

    public static void Observe(string battleId, string uri, JsonObject body, bool fromA)
    {
        if (!Enabled) return;
        try
        {
            var line = new JsonObject { ["t"] = "obs", ["uri"] = uri, ["fromA"] = fromA, ["body"] = body.DeepClone() };
            lock (Gate)
            {
                if (Files.TryGetValue(battleId, out var path)) File.AppendAllText(path, line.ToJsonString() + "\n");
            }
        }
        catch { /* a missed line shows up as a replay that stops early, never as a relay fault */ }
    }

    public static void End(string battleId)
    {
        lock (Gate) Files.Remove(battleId);
    }

    static JsonArray Arr(int[] a) => new(a.Select(x => (JsonNode?)x).ToArray());

    static string Safe(string s) => new(s.Where(char.IsLetterOrDigit).Take(32).ToArray());

    static void Prune()
    {
        foreach (var old in new DirectoryInfo(Dir).GetFiles("*.jsonl").OrderByDescending(f => f.Name).Skip(MaxFiles))
            try { old.Delete(); } catch { }
    }
}
