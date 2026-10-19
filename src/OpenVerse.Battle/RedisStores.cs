using OpenVerse.Common;
using StackExchange.Redis;

namespace OpenVerse.Battle;

// Central mode: OpenVerseBE owns the rooms and writes each player's deck into Redis; this side only reads. The key
// layout is OpenVerseBE's (Keys.cs there), so both have to agree on the prefix
public sealed class RedisBattleDeckStore(IConnectionMultiplexer redis, string prefix) : IBattleDeckStore
{
    string Key(string roomId, bool isOwner) => $"{prefix}bdeck:{roomId}:{(isOwner ? "owner" : "visitor")}";

    public BattleDeck? Get(string roomId, bool isOwner)
    {
        var raw = redis.GetDatabase().StringGet(Key(roomId, isOwner));
        if (raw.IsNullOrEmpty) return null;
        try { return BattleDeckJson.Deserialize(raw.ToString()); }
        catch (Exception e)
        {
            Console.WriteLine($"RedisBattleDeckStore: unreadable deck for room {roomId} ({e.Message})");
            return null;
        }
    }

    // OpenVerseBE is the writer. kept for the interface (and tests), never called by the hub
    public void Set(BattleDeck d) =>
        redis.GetDatabase().StringSet(Key(d.RoomId, d.IsOwner), BattleDeckJson.Serialize(d), TimeSpan.FromHours(3));
}

// The socket's BattleId header is the five-digit room id, and on a public port anyone can try all 90,000 of them.
// only a room OpenVerseBE actually opened gets a socket
public sealed class RedisRoomGate(IConnectionMultiplexer redis, string prefix)
{
    public Task<bool> RoomExists(string roomId) =>
        roomId.Length == 0 ? Task.FromResult(false) : redis.GetDatabase().KeyExistsAsync($"{prefix}room:{roomId}");
}

public static class RedisSetup
{
    // OPENVERSE_REDIS set = central mode. unset = standalone, the SQLite file shared with a local API as before
    public static (IBattleDeckStore? Decks, RedisRoomGate? Gate) FromEnvironment()
    {
        var conn = Environment.GetEnvironmentVariable("OPENVERSE_REDIS");
        if (string.IsNullOrWhiteSpace(conn)) return (null, null);
        var prefix = Environment.GetEnvironmentVariable("OPENVERSE_REDIS_PREFIX") is { Length: > 0 } p ? p : "ov:";
        var mux = ConnectionMultiplexer.Connect(conn);
        Console.WriteLine($"Battle: central mode, decks from redis {conn.Split(',')[0]} prefix={prefix}");
        return (new RedisBattleDeckStore(mux, prefix), new RedisRoomGate(mux, prefix));
    }
}
