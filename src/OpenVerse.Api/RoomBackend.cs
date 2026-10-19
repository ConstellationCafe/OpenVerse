using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Api;

public sealed record RoomSettings(int BattleType, int BattleRule, int DeckFormat, int TwoPickType, bool CanFriendWatch, bool CanGuildWatch);

public sealed record RoomPlayer(long ViewerId, string Name);

public sealed record RoomInfo(
    string RoomId,
    string BattleId,
    string NodeServerUrl,
    int BattleType,
    int BattleRule,
    int DeckFormat,
    int TwoPickType,
    bool IsOwner,
    RoomPlayer Owner);

public enum EnterOutcome { Ok, NotFound, Full }

// Where rooms live. Standalone they are in this process (LocalRoomBackend), as they always were. Central they are in
// OpenVerseBE (RemoteRoomBackend), so players on different PCs and networks can meet without a VPN.
// RoomHandler keeps the client-facing response shapes and talks only to this
public interface IRoomBackend
{
    Task<RoomInfo> Create(string udid, RoomSettings settings);
    Task<(EnterOutcome Outcome, RoomInfo? Room)> Enter(string udid, string roomId);
    Task Leave(string udid, string roomId);
    Task CloseMine(string udid);
    Task<RoomInfo?> Mine(string udid);
    Task<RoomInfo?> Get(string udid, string roomId);

    // The deck this player's own Api resolved for the room they are in. room, seat and source address are the
    // backend's to fill in
    Task SaveDeck(string udid, RoomInfo room, BattleDeck deck);

    // The address a request arrived from. only the local backend uses it: centrally OpenVerseBE sees the real one
    void NoteSourceIp(string udid, string ip);
}

public sealed class LocalRoomBackend(RoomStore rooms, UserStore users, IBattleDeckStore battleDecks) : IRoomBackend
{
    // the API is the only side that sees where a request came from, and it is the only discriminator both processes
    // can agree on: the client picks its own viewer_id and two installs have been seen picking the same one
    readonly ConcurrentDictionary<string, string> _ipByUdid = new();

    public void NoteSourceIp(string udid, string ip) { if (ip.Length > 0) _ipByUdid[udid] = ip; }

    RoomInfo Info(Room r, string udid)
    {
        var owner = users.GetOrCreate(r.OwnerUdid);
        return new RoomInfo(r.RoomId, r.BattleId, r.NodeServerUrl, r.BattleType, r.BattleRule, r.DeckFormat, r.TwoPickType,
            r.OwnerUdid == udid, new RoomPlayer(owner.ViewerId, owner.Name));
    }

    public Task<RoomInfo> Create(string udid, RoomSettings s)
    {
        users.GetOrCreate(udid);
        var r = rooms.Create(udid, s.BattleType, s.BattleRule, s.DeckFormat, s.TwoPickType, s.CanFriendWatch, s.CanGuildWatch);
        return Task.FromResult(Info(r, udid));
    }

    public Task<(EnterOutcome, RoomInfo?)> Enter(string udid, string roomId)
    {
        var r = rooms.Enter(udid, roomId);
        if (r is null) return Task.FromResult<(EnterOutcome, RoomInfo?)>((EnterOutcome.NotFound, null));
        users.GetOrCreate(udid);
        return Task.FromResult<(EnterOutcome, RoomInfo?)>((EnterOutcome.Ok, Info(r, udid)));
    }

    public Task Leave(string udid, string roomId) { rooms.Leave(roomId); return Task.CompletedTask; }

    public Task CloseMine(string udid)
    {
        if (rooms.FindByOwner(udid) is { } r) rooms.Close(r.RoomId);
        return Task.CompletedTask;
    }

    public Task<RoomInfo?> Mine(string udid) =>
        Task.FromResult(rooms.FindByUser(udid) is { } r ? Info(r, udid) : null);

    public Task<RoomInfo?> Get(string udid, string roomId) =>
        Task.FromResult(rooms.Get(roomId) is { } r ? Info(r, udid) : null);

    public Task SaveDeck(string udid, RoomInfo room, BattleDeck deck)
    {
        deck.RoomId = room.RoomId;
        deck.IsOwner = room.IsOwner;
        deck.SourceIp = _ipByUdid.GetValueOrDefault(udid, "");
        battleDecks.Set(deck);
        return Task.CompletedTask;
    }
}

// Talks to OpenVerseBE. Each udid this PC serves is registered once (lazily) for a token; a 401 means the server
// forgot it (restart, expiry, the same udid registering from another PC) and it registers again
public sealed class RemoteRoomBackend : IRoomBackend
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    readonly HttpClient _http;
    readonly string _cafeKey;
    readonly Func<string, string> _nameOf;
    readonly ConcurrentDictionary<string, Registration> _registered = new();

    sealed record Registration(long ViewerId, string Token);
    sealed record RegisterResponse(long ViewerId, string Token);
    sealed record PlayerDto(long ViewerId, string Name);
    sealed record RoomDto(string RoomId, string BattleId, string NodeServerUrl, int BattleType, int BattleRule,
        int DeckFormat, int TwoPickType, bool IsOwner, PlayerDto Owner);

    public RemoteRoomBackend(Uri baseUrl, string cafeKey, Func<string, string> nameOf, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseUrl;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _cafeKey = cafeKey;
        _nameOf = nameOf;
    }

    public void NoteSourceIp(string udid, string ip) { }

    // The server-wide viewer id for this player. signup hands it to the client, so ids stay distinct across PCs
    public async Task<long> Register(string udid)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "v1/players/register")
        {
            Content = JsonContent.Create(new { udid, name = _nameOf(udid) }, options: Json),
        };
        if (_cafeKey.Length > 0) req.Headers.Add("X-OV-Cafe-Key", _cafeKey);
        using var res = await _http.SendAsync(req);
        if (res.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("OpenVerseBE refused the cafe key (check cafe_key.txt)");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<RegisterResponse>(Json)
            ?? throw new InvalidOperationException("OpenVerseBE: empty register response");
        _registered[udid] = new Registration(body.ViewerId, body.Token);
        return body.ViewerId;
    }

    async Task<HttpResponseMessage> Send(string udid, HttpMethod method, string path, object? body = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!_registered.TryGetValue(udid, out var reg))
            {
                await Register(udid);
                reg = _registered[udid];
            }
            var req = new HttpRequestMessage(method, path);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", reg.Token);
            if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
            var res = await _http.SendAsync(req);
            if (res.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return res;
            res.Dispose();
            _registered.TryRemove(udid, out _);
        }
    }

    static RoomInfo Info(RoomDto d) => new(d.RoomId, d.BattleId, d.NodeServerUrl, d.BattleType, d.BattleRule,
        d.DeckFormat, d.TwoPickType, d.IsOwner, new RoomPlayer(d.Owner.ViewerId, d.Owner.Name));

    static async Task<RoomInfo> Read(HttpResponseMessage res) =>
        Info(await res.Content.ReadFromJsonAsync<RoomDto>(Json)
             ?? throw new InvalidOperationException("OpenVerseBE: empty room response"));

    public async Task<RoomInfo> Create(string udid, RoomSettings s)
    {
        using var res = await Send(udid, HttpMethod.Post, "v1/rooms", new
        {
            battle_type = s.BattleType, battle_rule = s.BattleRule, deck_format = s.DeckFormat,
            two_pick_type = s.TwoPickType, can_friend_watch = s.CanFriendWatch, can_guild_watch = s.CanGuildWatch,
        });
        res.EnsureSuccessStatusCode();
        return await Read(res);
    }

    public async Task<(EnterOutcome, RoomInfo?)> Enter(string udid, string roomId)
    {
        using var res = await Send(udid, HttpMethod.Post, $"v1/rooms/{Uri.EscapeDataString(roomId)}/enter");
        return res.StatusCode switch
        {
            HttpStatusCode.OK => (EnterOutcome.Ok, await Read(res)),
            HttpStatusCode.NotFound => (EnterOutcome.NotFound, null),
            HttpStatusCode.Conflict => (EnterOutcome.Full, null),
            _ => throw new HttpRequestException($"OpenVerseBE enter: {(int)res.StatusCode}"),
        };
    }

    public async Task Leave(string udid, string roomId)
    {
        using var res = await Send(udid, HttpMethod.Post, $"v1/rooms/{Uri.EscapeDataString(roomId)}/leave");
    }

    public async Task CloseMine(string udid)
    {
        using var res = await Send(udid, HttpMethod.Delete, "v1/rooms/mine");
    }

    public async Task<RoomInfo?> Mine(string udid)
    {
        using var res = await Send(udid, HttpMethod.Get, "v1/rooms/mine");
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await Read(res);
    }

    public async Task<RoomInfo?> Get(string udid, string roomId)
    {
        using var res = await Send(udid, HttpMethod.Get, $"v1/rooms/{Uri.EscapeDataString(roomId)}");
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await Read(res);
    }

    public async Task SaveDeck(string udid, RoomInfo room, BattleDeck deck)
    {
        using var res = await Send(udid, HttpMethod.Put, "v1/rooms/mine/deck", new
        {
            class_id = deck.ClassId, sub_class_id = deck.SubClassId, chara_id = deck.CharaId,
            sleeve_id = deck.SleeveId, leader_skin_id = deck.LeaderSkinId, card_ids = deck.CardIds,
            user_name = deck.UserName,
        });
        res.EnsureSuccessStatusCode();
    }
}
