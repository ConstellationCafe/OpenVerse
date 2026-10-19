using System.Collections.Concurrent;
using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Api;

// Shapes the client's open_room/* and open_room_battle/* responses. Where the rooms actually live is the backend's
// business (RoomBackend.cs): this process standalone, OpenVerseBE centrally
public sealed class RoomHandler
{
    readonly IRoomBackend _backend;
    readonly UserStore _users;
    readonly DeckStore _decks;
    // each participant's deck_no from set_deck, resolved against this PC's own deck store at do_matching
    readonly ConcurrentDictionary<string, int> _deckByUdid = new();

    public RoomHandler(IRoomBackend backend, UserStore users, DeckStore decks)
    {
        _backend = backend;
        _users = users;
        _decks = decks;
    }

    public static bool CanHandle(string path) =>
        path.StartsWith("/shadowverse/open_room/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/shadowverse/open_room_battle/", StringComparison.OrdinalIgnoreCase);

    // Answered with this result code when a room request cannot be completed (the matching server timed out, is down,
    // refused us, or has no room left). It is GuildHandler's NetworkDefine.MAINTENANCE_TYPE.GUILD_MAINTENANCE: the
    // client routes 2000-2999 to its each-function maintenance popup, which the player dismisses and which changes
    // nothing else. A result_code of 1 with an error inside the data is what left the client loading forever
    public const int RoomUnavailable = GuildHandler.GuildMaintenance;

    // Leaving must always work, or a player whose server went away is stuck in a room they cannot get out of
    static readonly HashSet<string> AlwaysSucceeds = ["leave_room", "close_room", "force_release_room"];

    public async Task<(int ResultCode, string Data)> HandleAsync(string path, string reqJson, string ownerUdid, string sourceIp = "")
    {
        _backend.NoteSourceIp(ownerUdid, sourceIp);
        var name = path[(path.LastIndexOf('/') + 1)..];
        try { return (1, await HandleCore(path, reqJson, ownerUdid)); }
        catch (Exception e) when (e is RoomServiceException or InvalidOperationException)
        {
            var kind = e is RoomServiceException rs ? rs.Kind.ToString() : "Error";
            // to the player this is only a dialog, so the reason has to be in the log
            Console.WriteLine($"  [room] {name} failed: {kind} ({e.Message})");
            if (AlwaysSucceeds.Contains(name))
                return (1, name == "close_room" ? "{}" : JsonSerializer.Serialize(new { result_reason = 0, room_result = 1 }));
            return (RoomUnavailable, "{}");
        }
    }

    async Task<string> HandleCore(string path, string reqJson, string udid)
    {
        using var doc = JsonDocument.Parse(reqJson);
        var root = doc.RootElement;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name switch
        {
            "create_room" => await Create(udid, root),
            "enter_room" => await Enter(udid, root),
            "leave_room" => await Leave(udid, root),
            "close_room" => await Close(udid),
            "force_release_room" => await ForceRelease(udid),
            "initialize_room_battle" => await InitializeBattle(udid, root),
            "do_matching" => await DoMatching(udid),
            // set_deck carries the participant's chosen deck_no. stash it so do_matching can resolve the real deck
            "set_deck" => SetDeck(udid, root),
            "deck_entry" or "ban_deck" or "finish_load"
                => JsonSerializer.Serialize(new { result_reason = 0 }),
            "finish" or "force_finish" => Finish(root),
            "kick_room" or "force_kick_room" => JsonSerializer.Serialize(new { result_reason = 0, room_result = 1 }),
            _ => "{}",
        };
    }

    string SetDeck(string udid, JsonElement root)
    {
        _deckByUdid[udid] = GetInt(root, "deck_no");
        return JsonSerializer.Serialize(new { result_reason = 0 });
    }

    // the client posts its own battle_result here and needs it echoed back: RoomBattleFinishTask.Parse bails unless
    // data is NON-EMPTY (FinishTaskBase.IsResponseDataExist rejects Count==0), leaving Data.RoomMatchFinish.data null,
    // which is what makes the result screen give up with the 3502 go-to-title popup instead of returning to the room
    string Finish(JsonElement root) =>
        JsonSerializer.Serialize(new
        {
            battle_result = GetInt(root, "battle_result"),
            get_class_experience = 0,
            class_experience = 0,
        });

    // open_room_battle/do_matching: the room already holds the battle server + id (from create/enter), so report success.
    // also resolve this participant's chosen deck and hand it to the battle server through the backend
    async Task<string> DoMatching(string udid)
    {
        var room = await _backend.Mine(udid);
        if (room is null)
            return JsonSerializer.Serialize(new { matching_state = 0, retry_period = 1, timeout_period = 60 });
        await WriteBattleDeck(room, udid);
        return JsonSerializer.Serialize(new
        {
            matching_state = room.IsOwner ? 3007 : 3004,
            timeout_period = 60,
            retry_period = 1,
            battle_id = room.BattleId,
            node_server_url = room.NodeServerUrl,
            // DoMatchingBase.SettingCardMasterId hands this to the battle. Master 1 is where the unlock flattens the
            // clan column, and 2 is untouched in either payload. Unconditional because switching off does not reload
            // the master, so that client is still holding the flattened one until it logs in again
            card_master_id = 2,
        });
    }

    async Task WriteBattleDeck(RoomInfo room, string udid)
    {
        if (!_deckByUdid.TryGetValue(udid, out var deckNo))
        {
            Console.WriteLine($"WriteBattleDeck: no set_deck for udid={udid}");
            return;
        }
        var deck = _decks.Get(udid, deckNo);
        Console.WriteLine($"WriteBattleDeck udid={udid} roomId={room.RoomId} isOwner={room.IsOwner} deckNo={deckNo} found={deck is not null} cards={deck?.CardIdArray.Length ?? 0}");
        if (deck is null) return;
        // Counting cards only guesses, and a deck that mixes classes makes the guess pick the majority, so the
        // leader turns into a class the player never chose
        var classId = deck.ClassId is >= 1 and <= 8 ? deck.ClassId : ClassOf(deck.CardIdArray);
        await _backend.SaveDeck(udid, room, new BattleDeck
        {
            ClassId = classId,
            SubClassId = deck.SubClassId,
            // default leader chara_id equals class_id for the 8 base classes, and GetClassPrm throws on 0/-1
            CharaId = classId,
            SleeveId = deck.SleeveId,
            LeaderSkinId = deck.LeaderSkinId,
            CardIds = deck.CardIdArray,
            UserName = _users.GetOrCreate(udid).Name,
        });
    }

    // Fallback only, for a deck that never recorded its class. A card's class is its 100,000s digit (verified against
    // the card master clan column), and the deck's is the most common nonzero one
    static int ClassOf(int[] cardIds)
    {
        var digits = cardIds.Select(c => c / 100000 % 10).Where(d => d is >= 1 and <= 8).ToList();
        return digits.Count > 0 ? digits.GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key : 1;
    }

    async Task<string> Create(string udid, JsonElement root)
    {
        _users.GetOrCreate(udid);
        var room = await _backend.Create(udid, new RoomSettings(
            GetInt(root, "battle_type"),
            GetInt(root, "battle_rule"),
            GetInt(root, "deck_format"),
            GetInt(root, "two_pick_type"),
            GetInt(root, "can_friend_watch") == 1,
            GetInt(root, "can_guild_watch") == 1));
        return JsonSerializer.Serialize(new
        {
            room_id = room.RoomId,
            display_room_id = room.RoomId,
            is_invitation_user = false,
            is_enabled_all_card = true,
            node_server_url = room.NodeServerUrl,
            battle_id = room.BattleId,
        });
    }

    async Task<string> Enter(string visitorUdid, JsonElement root)
    {
        var roomId = GetString(root, "room_id") ?? "";
        _users.GetOrCreate(visitorUdid);
        var (outcome, room) = await _backend.Enter(visitorUdid, roomId);
        if (outcome != EnterOutcome.Ok || room is null)
        {
            Console.WriteLine($"  [room] enter {roomId}: {outcome}");
            return JsonSerializer.Serialize(new { result_reason = -3 });
        }
        // only the owner's id and name cross machines; the rest of the card is the same blank profile it always was
        var owner = new UserProfile { ViewerId = room.Owner.ViewerId, Name = room.Owner.Name };
        return JsonSerializer.Serialize(new
        {
            result_reason = 0,
            is_friend = 0,
            guild_id = 0,
            oppo_guild_id = 0,
            oppo_info = new
            {
                oppoId = owner.ViewerId,
                battlePoint = owner.BattlePoint,
                degreeId = owner.DegreeId,
                emblemId = owner.EmblemId,
                country_code = owner.CountryCode,
                rank = owner.Rank,
                max_rank = owner.MaxRank,
                userName = owner.Name,
                isOfficial = owner.IsOfficial,
            },
            node_server_url = room.NodeServerUrl,
            battle_id = room.BattleId,
            is_invitation_user = false,
            is_enabled_all_card = true,
            battle_type = room.BattleType,
            battle_rule = room.BattleRule,
            deck_format = room.DeckFormat,
            two_pick_type = room.TwoPickType,
        });
    }

    async Task<string> Leave(string udid, JsonElement root)
    {
        await _backend.Leave(udid, GetString(root, "room_id") ?? "");
        return JsonSerializer.Serialize(new { result_reason = 0, room_result = 1 });
    }

    async Task<string> Close(string udid)
    {
        await _backend.CloseMine(udid);
        return "{}";
    }

    async Task<string> ForceRelease(string udid)
    {
        await _backend.CloseMine(udid);
        return JsonSerializer.Serialize(new { room_result = 1 });
    }

    async Task<string> InitializeBattle(string udid, JsonElement root)
    {
        var room = await _backend.Get(udid, GetString(root, "room_id") ?? "");
        return JsonSerializer.Serialize(new
        {
            battle_id = room?.BattleId ?? "",
            my_battle_result = new { },
            opponent_battle_result = new { },
            used_deck = 0,
            is_settled = 0,
        });
    }

    static int GetInt(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    static string? GetString(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
