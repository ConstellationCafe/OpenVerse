### ![ja](https://flagcdn.com/20x15/jp.png) [日本語版](../server-design.md)

# OpenVerse server design

## Layout

| Server | Framework | Role |
| --- | --- | --- |
| API | C# (ASP.NET Core / Kestrel) | login, master data, decks, room management |
| Battle | C# (custom Socket.IO) | PvP real-time |
| CDN | static server (nginx / caddy) | asset delivery |
| DB | SQLite (PostgreSQL if needed) | decks, owned cards, sessions |

- API and battle can share one process or split later (they share a common library)
- A battle server is only needed for PvP. Solitaire (CP battle, story, quest) runs entirely client-side, engine and AI included

## API handlers

`Program.cs` dispatches to each handler.

- `card_master`: serves every card as owned
- `DeckHandler`: deck editing, deck introduction, starters
- `PracticeHandler`: CP battle setup and result recording
- `RoomHandler`: room management (Phase 4)
- `DeckCodeHandler`: self-hosted deck codes
- load/index etc. are stubs with dynamic splices (owned cards, sleeves, background ids)

Card names/effects live in the client's SystemText, so card_master only returns text ids.

## Language

Jpn and Eng, switched by the HTTP `LANGUAGE` header.

- Most text lives in the client's SystemText, so the server just returns ids
- `LOCALE`/`REGION_CODE` are unrelated to display language and can be ignored
- The CDN reads the language token in the path and serves `stubs/<lang>/`
- The server only holds per-language text for a few things (mail, missions, votes, etc.), in Phase 5 via an i18n table

## Network

Each machine points `utoongaize.shadowverse.jp` at its own 127.0.0.1 via hosts, and every player runs the API on their own PC: the card master, assets and decks come from their own client.

Room matches run one of two ways:

- Standalone: no `be_url.txt` beside the launcher. Rooms and the battle server stay on that machine (development and tests)
- Central: `be_url.txt` holds the address of an [OpenVerseBE](https://github.com/ConstellationCafe/OpenVerseBE) server. Only `open_room/*` goes there, rooms live in Redis, and battles go through that server's `OpenVerse.Battle`, which reads the decks from the same Redis. No VPN; the server needs ports 443 and 3001 open

Centrally, the source address used to tell the seats apart is recorded by OpenVerseBE (a request from a player's API comes from that player's public IP). `cafe_key.txt` must match the server's `OVBE_CAFE_KEY` and keeps registration to your own group.
- The endpoint is HTTPS, but the client does not validate the cert, so a self-signed one works as-is (no client patch or mkcert, the launcher generates it)

## Battle engine

The PvP relay passes a client's messages straight to the peer, but the values the original server used to fill in (costs, condition answers, `spin`) go missing. So the client's own battle engine runs headless alongside the match, replays the same game, and reads those values off its own board.

There are two engines per room. A client re-simulates what it receives rather than displaying it, so without a model of each client's view separately there is no way to compute `spin` or to run the receive check.

`release/server/engine.txt` raises how far the engine is trusted, in stages (`Observe` → `AdviseCost` → `AnswerBlanks`), and ships on `AnswerBlanks`. Below that no condition answers are injected, so something like a PP grant attached to the leader fires on one side only. [desync.md](desync.md) has the investigation and what is still open.

## Distribution

- Ship two exes, setup and launcher (setup needs no elevation, launcher runs as admin to rewrite hosts and start the server)
- Self-hosting also works via `dotnet run` (SQLite, so no DB server)
- Dockerize if always-on hosting is needed
