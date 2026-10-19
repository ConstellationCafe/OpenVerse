using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenVerse.Api;
using OpenVerse.Battle;
using OpenVerse.Common;
using StackExchange.Redis;

namespace OpenVerse.Tests;

// A stand-in for OpenVerseBE that speaks its wire contract (snake_case JSON, bearer tokens, 404/409 on enter). The
// real server's own behaviour (Lua enter, TTLs) is tested in the OpenVerseBE repo against Redis
sealed class FakeBe : HttpMessageHandler
{
    public readonly ConcurrentDictionary<string, (string Udid, long Vid)> Tokens = new();
    public readonly ConcurrentDictionary<string, long> Vids = new();
    public readonly ConcurrentDictionary<string, string> Names = new();
    public readonly ConcurrentDictionary<string, Dictionary<string, object?>> Rooms = new();
    public readonly ConcurrentDictionary<string, string> UserRoom = new();
    public readonly List<string> Decks = [];
    public int Registrations;
    public string? RequiredCafeKey;
    public bool Down;
    long _nextVid = 100000;

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    static HttpResponseMessage Reply(HttpStatusCode code, object? body = null) => new(code)
    {
        Content = body is null ? null : new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
    };

    object RoomBody(string roomId, string udid)
    {
        var r = Rooms[roomId];
        return new
        {
            room_id = roomId, battle_id = r["battle_id"], node_server_url = "be.example:3001",
            battle_type = r["battle_type"], battle_rule = 1, deck_format = 1, two_pick_type = 0,
            can_friend_watch = false, can_guild_watch = false,
            is_owner = (string)r["owner"]! == udid,
            owner = new { viewer_id = Vids[(string)r["owner"]!], name = Names[(string)r["owner"]!] },
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (Down) throw new HttpRequestException("connection refused");
        var path = req.RequestUri!.AbsolutePath;
        var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);

        if (path == "/v1/players/register")
        {
            if (RequiredCafeKey is not null &&
                (!req.Headers.TryGetValues("X-OV-Cafe-Key", out var k) || k.Single() != RequiredCafeKey))
                return Reply(HttpStatusCode.Forbidden);
            Interlocked.Increment(ref Registrations);
            var j = JsonDocument.Parse(body).RootElement;
            var udid = j.GetProperty("udid").GetString()!;
            var vid = Vids.GetOrAdd(udid, _ => Interlocked.Increment(ref _nextVid));
            Names[udid] = j.GetProperty("name").GetString()!;
            var token = Guid.NewGuid().ToString("N");
            Tokens[token] = (udid, vid);
            return Reply(HttpStatusCode.OK, new { viewer_id = vid, token });
        }

        var auth = req.Headers.Authorization?.Parameter;
        if (auth is null || !Tokens.TryGetValue(auth, out var me)) return Reply(HttpStatusCode.Unauthorized);

        if (path == "/v1/rooms" && req.Method == HttpMethod.Post)
        {
            var j = JsonDocument.Parse(body).RootElement;
            var id = Random.Shared.Next(10000, 100000).ToString();
            Rooms[id] = new()
            {
                ["battle_id"] = Guid.NewGuid().ToString("N"), ["owner"] = me.Udid, ["visitor"] = null,
                ["battle_type"] = j.GetProperty("battle_type").GetInt32(),
            };
            UserRoom[me.Udid] = id;
            return Reply(HttpStatusCode.OK, RoomBody(id, me.Udid));
        }
        if (path.StartsWith("/v1/rooms/") && path.EndsWith("/enter"))
        {
            var id = path.Split('/')[3];
            if (!Rooms.TryGetValue(id, out var r)) return Reply(HttpStatusCode.NotFound);
            if (r["visitor"] is string v && v != me.Udid) return Reply(HttpStatusCode.Conflict);
            r["visitor"] = me.Udid;
            UserRoom[me.Udid] = id;
            return Reply(HttpStatusCode.OK, RoomBody(id, me.Udid));
        }
        if (path == "/v1/rooms/mine" && req.Method == HttpMethod.Get)
            return UserRoom.TryGetValue(me.Udid, out var mine) && Rooms.ContainsKey(mine)
                ? Reply(HttpStatusCode.OK, RoomBody(mine, me.Udid))
                : Reply(HttpStatusCode.NotFound);
        if (path == "/v1/rooms/mine" && req.Method == HttpMethod.Delete)
        {
            if (UserRoom.TryGetValue(me.Udid, out var mine) && (string)Rooms[mine]["owner"]! == me.Udid) Rooms.TryRemove(mine, out _);
            return Reply(HttpStatusCode.NoContent);
        }
        if (path == "/v1/rooms/mine/deck")
        {
            lock (Decks) Decks.Add(body);
            return Reply(HttpStatusCode.NoContent);
        }
        if (path.StartsWith("/v1/rooms/") && path.EndsWith("/leave"))
        {
            if (Rooms.TryGetValue(path.Split('/')[3], out var r)) r["visitor"] = null;
            return Reply(HttpStatusCode.NoContent);
        }
        if (path.StartsWith("/v1/rooms/") && req.Method == HttpMethod.Get)
        {
            var id = path.Split('/')[3];
            return Rooms.ContainsKey(id) ? Reply(HttpStatusCode.OK, RoomBody(id, me.Udid)) : Reply(HttpStatusCode.NotFound);
        }
        return Reply(HttpStatusCode.NotFound);
    }
}

[Collection("Sqlite")]
public class CentralMatchingTests : IDisposable
{
    readonly string _db = Path.Combine(Path.GetTempPath(), $"ov-central-{Guid.NewGuid():N}.db");
    readonly FakeBe _be = new();
    readonly UserStore _users = new();
    readonly DeckStore _decks;
    readonly RemoteRoomBackend _backend;
    readonly RoomHandler _rooms;

    public CentralMatchingTests()
    {
        _decks = new DeckStore(_db);
        _backend = new RemoteRoomBackend(new Uri("https://be.example/"), "", u => _users.GetOrCreate(u).Name, _be);
        _rooms = new RoomHandler(_backend, _users, _decks);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch { }
    }

    async Task<JsonElement> Call(string name, object req, string udid, string group = "open_room") =>
        JsonDocument.Parse(await _rooms.HandleAsync($"/shadowverse/{group}/{name}", JsonSerializer.Serialize(req), udid)).RootElement;

    static readonly object Settings = new
    {
        battle_type = 1, battle_rule = 1, can_friend_watch = 0, can_guild_watch = 0, deck_format = 1, two_pick_type = 0,
    };

    [Fact]
    public async Task CreateEnterAndMatchKeepTheClientShapes()
    {
        _users.GetOrCreate("owner-udid").Name = "Owner";
        var created = await Call("create_room", Settings, "owner-udid");
        var roomId = created.GetProperty("room_id").GetString();
        Assert.Matches(@"^\d{5}$", roomId);
        Assert.Equal("be.example:3001", created.GetProperty("node_server_url").GetString());
        Assert.Equal(roomId, created.GetProperty("display_room_id").GetString());

        var entered = await Call("enter_room", new { room_id = roomId }, "visitor-udid");
        Assert.Equal(0, entered.GetProperty("result_reason").GetInt32());
        var oppo = entered.GetProperty("oppo_info");
        Assert.Equal(_be.Vids["owner-udid"], oppo.GetProperty("oppoId").GetInt64());
        Assert.Equal("Owner", oppo.GetProperty("userName").GetString());
        Assert.Equal(created.GetProperty("battle_id").GetString(), entered.GetProperty("battle_id").GetString());

        var om = await Call("do_matching", new { }, "owner-udid", "open_room_battle");
        Assert.Equal(3007, om.GetProperty("matching_state").GetInt32());
        Assert.Equal("be.example:3001", om.GetProperty("node_server_url").GetString());
        var vm = await Call("do_matching", new { }, "visitor-udid", "open_room_battle");
        Assert.Equal(3004, vm.GetProperty("matching_state").GetInt32());
        Assert.Equal(om.GetProperty("battle_id").GetString(), vm.GetProperty("battle_id").GetString());
    }

    [Fact]
    public async Task EnteringAMissingOrFullRoomIsRefused()
    {
        Assert.Equal(-3, (await Call("enter_room", new { room_id = "00000" }, "visitor-0")).GetProperty("result_reason").GetInt32());

        var roomId = (await Call("create_room", Settings, "owner-udid")).GetProperty("room_id").GetString();
        Assert.Equal(0, (await Call("enter_room", new { room_id = roomId }, "visitor-1")).GetProperty("result_reason").GetInt32());
        Assert.Equal(-3, (await Call("enter_room", new { room_id = roomId }, "visitor-2")).GetProperty("result_reason").GetInt32());
    }

    [Fact]
    public async Task NoRoomMeansMatchingKeepsWaiting()
    {
        var m = await Call("do_matching", new { }, "nobody", "open_room_battle");
        Assert.Equal(0, m.GetProperty("matching_state").GetInt32());
    }

    // the deck is resolved from this PC's own deck store and only its contents travel; seat and address are the server's
    [Fact]
    public async Task TheChosenDeckIsSentToTheServer()
    {
        _users.GetOrCreate("owner-udid").Name = "Owner";
        _decks.Save(new Deck
        {
            UserKey = "owner-udid", DeckNo = 3, Format = 1, ClassId = 4, SubClassId = 10, SleeveId = 3000123L,
            CardIdArray = [104011010, 104011010, 104021020],
        });
        await Call("create_room", Settings, "owner-udid");
        await Call("set_deck", new { deck_no = 3 }, "owner-udid", "open_room_battle");
        await Call("do_matching", new { }, "owner-udid", "open_room_battle");

        var sent = JsonDocument.Parse(Assert.Single(_be.Decks)).RootElement;
        Assert.Equal(4, sent.GetProperty("class_id").GetInt32());
        Assert.Equal(4, sent.GetProperty("chara_id").GetInt32());
        Assert.Equal(3000123L, sent.GetProperty("sleeve_id").GetInt64());
        Assert.Equal(3, sent.GetProperty("card_ids").GetArrayLength());
        Assert.Equal("Owner", sent.GetProperty("user_name").GetString());
        Assert.False(sent.TryGetProperty("source_ip", out _));
        Assert.False(sent.TryGetProperty("is_owner", out _));
    }

    [Fact]
    public async Task AForgottenTokenReRegistersOnce()
    {
        await Call("create_room", Settings, "owner-udid");
        Assert.Equal(1, _be.Registrations);
        _be.Tokens.Clear();  // OpenVerseBE restarted or the token expired

        var m = await Call("do_matching", new { }, "owner-udid", "open_room_battle");
        Assert.Equal(3007, m.GetProperty("matching_state").GetInt32());
        Assert.Equal(2, _be.Registrations);
    }

    [Fact]
    public async Task SignupGetsTheServerWideViewerId()
    {
        var a = await _backend.Register("player-a");
        var b = await _backend.Register("player-b");
        Assert.NotEqual(a, b);
        Assert.Equal(a, await _backend.Register("player-a"));
    }

    [Fact]
    public async Task AWrongCafeKeyIsReportedNotThrown()
    {
        _be.RequiredCafeKey = "right";
        var r = await Call("create_room", Settings, "owner-udid");
        Assert.Equal(-1, r.GetProperty("result_reason").GetInt32());

        var keyed = new RoomHandler(new RemoteRoomBackend(new Uri("https://be.example/"), "right", u => u, _be), _users, _decks);
        var ok = JsonDocument.Parse(await keyed.HandleAsync("/shadowverse/open_room/create_room", JsonSerializer.Serialize(Settings), "owner-udid")).RootElement;
        Assert.True(ok.TryGetProperty("room_id", out _));
    }

    [Fact]
    public async Task AnUnreachableServerIsReportedNotThrown()
    {
        _be.Down = true;
        var r = await Call("create_room", Settings, "owner-udid");
        Assert.Equal(-1, r.GetProperty("result_reason").GetInt32());
    }
}

public class BattleDeckJsonTests
{
    // exactly what OpenVerseBE's RoomRepository.SaveDeck writes
    const string FromBe = """
        {"room_id":"48213","is_owner":false,"class_id":3,"sub_class_id":10,"chara_id":3,"sleeve_id":3000011,
         "leader_skin_id":0,"card_ids":[100311010,100311020],"user_name":"guest","source_ip":"203.0.113.7"}
        """;

    [Fact]
    public void ReadsTheServersFormat()
    {
        var d = BattleDeckJson.Deserialize(FromBe)!;
        Assert.Equal("48213", d.RoomId);
        Assert.False(d.IsOwner);
        Assert.Equal(3, d.ClassId);
        Assert.Equal(3, d.CharaId);
        Assert.Equal(3000011L, d.SleeveId);
        Assert.Equal([100311010, 100311020], d.CardIds);
        Assert.Equal("guest", d.UserName);
        Assert.Equal("203.0.113.7", d.SourceIp);
    }

    [Fact]
    public void RoundTrips()
    {
        var d = new BattleDeck { RoomId = "1", IsOwner = true, ClassId = 2, CharaId = 2, CardIds = [1, 2], SourceIp = "x" };
        var back = BattleDeckJson.Deserialize(BattleDeckJson.Serialize(d))!;
        Assert.Equal(d.RoomId, back.RoomId);
        Assert.True(back.IsOwner);
        Assert.Equal(d.CardIds, back.CardIds);
        Assert.Contains("\"source_ip\"", BattleDeckJson.Serialize(d));
    }

    [Theory]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void IPv4MappedAddressesCompareAsIPv4(string raw, string expected) =>
        Assert.Equal(expected, BattleServer.NormalizeIp(IPAddress.Parse(raw)));
}

// Only with a Redis to talk to (CI provides one): OPENVERSE_TEST_REDIS=127.0.0.1:6379
public class RedisBattleStoreTests
{
    static readonly string? Conn = Environment.GetEnvironmentVariable("OPENVERSE_TEST_REDIS");

    [Fact]
    public async Task DecksAndRoomsAreReadFromOpenVerseBEsKeys()
    {
        if (Conn is null) return;
        var prefix = $"ovtest:{Guid.NewGuid():N}:";
        using var mux = await ConnectionMultiplexer.ConnectAsync(Conn);
        var db = mux.GetDatabase();
        try
        {
            var store = new RedisBattleDeckStore(mux, prefix);
            var gate = new RedisRoomGate(mux, prefix);

            Assert.Null(store.Get("48213", isOwner: false));
            Assert.False(await gate.RoomExists("48213"));
            Assert.False(await gate.RoomExists(""));

            await db.HashSetAsync($"{prefix}room:48213", "owner_udid", "o");
            await db.StringSetAsync($"{prefix}bdeck:48213:visitor", """
                {"room_id":"48213","is_owner":false,"class_id":5,"sub_class_id":10,"chara_id":5,"sleeve_id":3000011,
                 "leader_skin_id":0,"card_ids":[1],"user_name":"guest","source_ip":"198.51.100.2"}
                """);

            Assert.True(await gate.RoomExists("48213"));
            var d = store.Get("48213", isOwner: false)!;
            Assert.Equal(5, d.ClassId);
            Assert.Equal("198.51.100.2", d.SourceIp);
            Assert.Null(store.Get("48213", isOwner: true));
        }
        finally
        {
            await db.KeyDeleteAsync(new RedisKey[] { $"{prefix}room:48213", $"{prefix}bdeck:48213:visitor" });
        }
    }

    // the seat check reads source_ip off these decks, so it has to keep working with the Redis store underneath
    [Fact]
    public async Task SeatsAreSettledByTheAddressesTheServerRecorded()
    {
        if (Conn is null) return;
        var prefix = $"ovtest:{Guid.NewGuid():N}:";
        using var mux = await ConnectionMultiplexer.ConnectAsync(Conn);
        var store = new RedisBattleDeckStore(mux, prefix);
        try
        {
            store.Set(new BattleDeck { RoomId = "room1", IsOwner = true, ClassId = 1, CharaId = 1, SourceIp = "203.0.113.1" });
            store.Set(new BattleDeck { RoomId = "room1", IsOwner = false, ClassId = 2, CharaId = 2, SourceIp = "203.0.113.2" });

            var sessions = new SessionManager();
            var hub = new BattleHub(sessions, store, new(), new());
            // both clients claim the same cached viewer id, as copied installs do
            var a = SeatRig.Join(sessions, "sessA", "100001", "room1", "203.0.113.2");
            var b = SeatRig.Join(sessions, "sessB", "100001", "room1", "203.0.113.1");

            Assert.False(SeatRig.IsOwner(hub, a));
            Assert.True(SeatRig.IsOwner(hub, b));
        }
        finally
        {
            await mux.GetDatabase().KeyDeleteAsync(new RedisKey[] { $"{prefix}bdeck:room1:owner", $"{prefix}bdeck:room1:visitor" });
        }
    }
}

static class SeatRig
{
    const System.Reflection.BindingFlags MF = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    static void SetAuto(object obj, string prop, object val) =>
        obj.GetType().GetField($"<{prop}>k__BackingField", MF)!.SetValue(obj, val);

    public static Session Join(SessionManager sessions, string id, string viewerId, string battleId, string ip)
    {
        var s = (Session)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Session));
        SetAuto(s, "Id", id);
        SetAuto(s, "BattleId", battleId);
        SetAuto(s, "ViewerId", viewerId);
        SetAuto(s, "RemoteIp", ip);
        sessions.Add(s);
        return s;
    }

    public static bool IsOwner(BattleHub hub, Session s) =>
        (bool)typeof(BattleHub).GetMethod("IsOwner", MF)!.Invoke(hub, [s])!;
}
