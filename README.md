# 7RHYTHM Offline Server

A local server that makes the PC rhythm game **7RHYTHM -ナナリズム-** (777Town, client Ver.2.3.06) playable
fully offline, with no network connection at all.

It replaces every online service the game talks to: the 7BEAT login, the HTTPS start-up check, the game API
and the asset (song file) server. Everything runs on the same PC from one launcher.

> **This repository contains no game files.** You need your own installed copy of the game, including its
> downloaded song files (`cdn\Files`).

---

## Features

- **Fully offline:** works the same with or without a network connection. No drivers, no hosts-file editing by
  hand, no extra programs.
- **Built-in asset server:** songs, movies and master tables are served straight from the game's own `cdn\Files`.
- **Everything unlocked:**
  - every member (rarity N and up) at max level, max skill level and max PERFECT / GREAT / drop stats
  - max rank (300) and full stamina
  - all story, member, rank and event episodes, including the 13 event chapters in the event tab
  - all titles
- **Scores save:** high scores, clear lamps, monthly high scores and the total-score ranking are kept in `offline\save`.
- **Free gacha:** each pull picks evenly from all 6,099 deck-usable members, and the rates dialog explains this.
- **Song helpers:** four max-rank helpers before every song (blue, red, yellow and mixed), each with three
  leader slots and full assist members.
- **Clean first start:** a new save starts as `GUEST` with a random user ID. The song list, cursor and difficulty
  reset to the defaults.
- **No dead links:** buttons that would open the old official site or support page are disabled, and website
  links in server replies are removed.

Fixed settings, with nothing to configure: coins stay at 10,000, continuing after a failed song is free, and the
home screen ticker reads `7RHYTHM -nanarhythm- Offline Version`.

---

## Requirements

- Windows 10 or 11, 64-bit.
- Windows PowerShell 5.1 (built in). The server is C# compiled at start-up with `Add-Type`, so no .NET SDK is needed.
- Administrator rights, to add the game's server addresses to the loopback adapter and set the hosts entry. The
  launcher asks for them.
- An installed copy of 7RHYTHM with its song files already downloaded (`cdn\Files\resource\1364\...`).

## Installation

1. Copy this repository's files **into the game folder**, so that `launch_offline.bat` sits next to `7rhythm.exe`.
2. Run `launch_offline.bat`.

The resulting layout:

```
7rhythm_nanarhythm\
├── 7rhythm.exe
├── 7rhythm_Data\
├── cdn\Files\...              (song files from your install)
├── launch_offline.bat
├── restore_buttons.bat
└── offline\
    ├── OfflineServer.cs ...
    └── data\
```

If songs show **ダウンロード** (download), open メニュー → データ管理 and use "all download". It downloads from the
local asset server, so no internet is needed.

## Usage

Run **`launch_offline.bat`**. It:

1. Asks for administrator rights.
2. Closes a running game, and any offline server left over from an earlier launch.
3. Disables the official-site and support buttons in `7rhythm_Data\resources.assets`. The original is saved once as
   `resources.assets.orig`; the step is skipped if already done or if the game version doesn't match.
4. Points `pcgame.7rhythm.jp` at the game's original address in the hosts file (`52.69.127.28 pcgame.7rhythm.jp`).
   The line is added if missing, or changed if it points anywhere else.
5. Allows local ports 45558–45560 in Windows Firewall.
6. Starts the server window **"7RHYTHM Offline Server"**, waits until it is ready, then starts the game.

Keep the server window open while you play. Closing it stops the server and removes the addresses it added.

| File | Purpose |
|---|---|
| `launch_offline.bat` | Starts everything (see above) |
| `restore_buttons.bat` | Turns the website buttons back on (the launcher turns them off again on its next start) |
| `offline\remove_addresses.bat` | Removes the server addresses from the loopback adapter by hand, only needed if the server was killed. A reboot also clears them, and the next server start does too. |
| `offline\logs\server.log` | Server log. `ASSET missing` lines are files the game asked for that aren't in `cdn\Files`. |
| `offline\save\state.json` | The offline save. Delete the `save` folder for a fresh start. |

---

## How it works

### Connections the game makes

| What | Original target | Handled by |
|---|---|---|
| 7BEAT login | TCP `210.128.4.130:58001` (fixed address in the client) | Login listener on `210.128.4.130:58001`. Replies with a self-built login reply: the protocol's 32-byte `MBH ` header with status 0 (plain payload), message type 2, and a 112-byte payload of zeros |
| Start-up check | `https://pcgame.7rhythm.jp/get/connection` (hosts entry → `52.69.127.28`) | TLS listener on `52.69.127.28:443` and `52.196.206.7:443` (self-signed certificate; the client does not check the name) |
| Game API | `http://<connection url>/7r/<endpoint>` | HTTP listener on port 45558. The start-up reply points the API at `http://127.0.0.1:45558/7r/` |
| Assets | `http://127.0.0.1:45557/Files/...` | Built-in static file server for `cdn\Files`, with Range support |

### Local server addresses (`LocalAddresses.cs`)

The client connects to fixed public addresses. While the server runs, it adds those addresses to Windows' loopback
adapter (`New-NetIPAddress`, active store only, `SkipAsSource`) and listens on them directly, so the game's
connections are ordinary local connections:

- `210.128.4.130` for the 7BEAT login (port 58001)
- `52.69.127.28` and `52.196.206.7` for `pcgame.7rhythm.jp` (ports 443 and 80)

No packet driver and no routes are involved, and it works the same with or without a network connection. The
addresses are removed when the server window closes; leftovers from a killed server are cleared at the next start,
and a reboot clears them too.

### API format

- **Requests:** `request_id=<id>&param=<base64url(AES-128-CBC(JSON))>`, with an all-zero key and IV.
- **Replies:** a JSON envelope `{data, result, button_msg, jump_to_url, jump_to_title, server_time, request_id, key, iv}`.
  The endpoints listed in `offline/data/static/encrypted_endpoints.json` return it AES-encrypted and base64-encoded.
- **Master tables:** `M_*.json` files are base64url AES-128-CBC with a key and IV per table (`table_keys.json`).
  The server reads them for cards, levels, episodes, titles, ranks and so on.
- **Fresh installs:** a new install downloads every master table on first start. Two are served patched, built from
  the game's own files at request time: `M_const` with the song and medley continue cost set to 0 jewels, and
  `M_music_mission` emptied.

### Replies

- **Captured layouts:** `offline/data/templates/*.json` hold reply layouts captured from the game's online traffic and
  scrubbed of account data. `"$state"` placeholders are filled from the save.
- **Built in code:** endpoints with game logic (scores, gacha, episodes, decks, titles, helpers and so on) are built
  in `OfflineServer.cs`.
- **Empty but valid:** screens with no captured reply get replies built from the client's own data classes. The
  field names were read from `global-metadata.dat` with `tools/dev/meta.py`.
- **Generic reply:** anything else gets just the common user blocks. The request is saved to `offline/logs/unknown`.

### Website buttons (`patch_buttons.ps1`)

The five buttons that open external pages are the official page and contact buttons on the title screen menu and
the in-game menu, plus the window title bar's 公式ページ. Each is a `UnityEngine.UI.Button` in `resources.assets`.
The patch clears each button's `m_Interactable` byte, so the button shows but can't be clicked.
`tools/dev/find_buttons.py` finds the offsets.

---

## Endpoint status

<details>
<summary>Handled</summary>

Start-up, login and registration, home, login bonus, `init/*`, table updates, stage list/start/finish/continue,
total score ranking, decks, member dictionary, gacha list/play/rates, episodes and event episodes, titles, name
change, friend and helper lists, presents, trophies, notices, shops, jewel cost checks, friend and card box limit
extensions, and member sell, fusion and evolution (not reachable while all members are unlocked).

</details>

<details>
<summary>Still answered with the generic reply</summary>

Can be triggered in game: `refresh/stamina`, `boost/point`, `reach/results/point`, `finish/review/popup`,
`get/stage/ranking/list`, `get/monthly/ranking/list`, `get/top/ranker/data`, the medal, friend-point, song and jewel
shops, the starter shop, and the event top, challenge, points ranking and item shop.

Not reachable offline: medley mode (it was a limited-time event), friend actions (the friend list is empty),
present, trophy and trial-gacha rewards, account linking, age verification and starting-deck selection.

</details>

Captures of these screens are welcome. See [Development](#development).

---

## Development

`tools/` holds what was used to build the server.

| Tool | Purpose |
|---|---|
| `tools/capture/capture_start.bat`, `capture_stop.bat` | Capture the game's online traffic (API on TCP 80, login on TCP 58001) with Windows' built-in `pktmon` into `capture.pcapng` |
| `tools/capture/export_prefs.bat` | Export the game's local settings (PlayerPrefs, `HKCU\Software\SammyNetworks\nana-rhythm`) |
| `tools/dev/pairs.py` | Extract the HTTP requests and replies from a capture into `pairs.json` (needs `tshark`) |
| `tools/dev/show.py` | Decode the captured requests and replies for one endpoint |
| `tools/dev/replay.py` | Replay a capture against the running server and compare reply layouts (regression test) |
| `tools/dev/call.py` | Send one request to the running server and print the decoded reply |
| `tools/dev/probe_endpoints.py` | List the endpoints that still get the generic reply |
| `tools/dev/meta.py` | Dump string literals, or class field names, from IL2CPP `global-metadata.dat` (v24.1) |
| `tools/dev/decrypt_table.py` | Decrypt a master table `M_*.json` |
| `tools/dev/find_buttons.py` | Find the website buttons' `m_Interactable` offsets in `resources.assets` |
| `tools/dev/Host.cs` | Run the server under Mono (Linux) for testing, without adding the local addresses |

```sh
pip install -r tools/dev/requirements.txt

# test build and run (Linux / Mono)
mcs -langversion:5 -r:System.Web.Extensions.dll -out:server.exe offline/*.cs tools/dev/Host.cs
CDN=/path/to/cdn/Files mono server.exe ./offline 45558 45559

# regression test against a capture
python tools/dev/pairs.py capture.pcapng
python tools/dev/replay.py pairs.json
```

The server code must stay compatible with **C# 5**, because Windows PowerShell 5.1's `Add-Type` uses the old
compiler. Test with `mcs -langversion:5`.

> **Captures, `pairs.json` and `prefs.reg` contain your account IDs and tokens.** They are listed in
> `.gitignore`. Don't commit them.

---

## Notes

- **Game data:** `offline/data/` contains reply layouts and table keys derived from the game client. 7RHYTHM / ナナリズム and all game content belong to their respective owners. This project is
  unofficial and not affiliated with them.
- **Certificate:** `tls.pfx` is a throwaway self-signed certificate used only for the local start-up check.
