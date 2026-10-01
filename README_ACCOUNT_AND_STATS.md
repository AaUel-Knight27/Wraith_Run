# Account + player stats (2026-10-01)

One account per player: a name they choose, an avatar, and a lifetime record (main stats, best
weapons, medals) that is saved on their device and shown to everyone in the same match as a stat card.

**Written without the ability to compile or run Godot** (same caveat as the lobby and bots rounds).
Nothing existing was removed. First build may need small fixes; the checklist at the bottom is the
fastest way to find them.

## What the player sees
- **First launch:** CREATE ACCOUNT screen (name + emblem + colour). No way back until it is done.
- **Main menu:** profile chip top-left (avatar + name). Tap it -> **PROFILE** = your stat card, with EDIT.
- **In a match:** Esc menu -> **PLAYERS**. Everyone in the match, with avatar and a K/D line. Tap one ->
  their full stat card (BACK / Esc closes). Opening a card asks that player for a fresh copy.
- **Names everywhere:** kill feed, Tab scoreboard, results screen and the floating name tag now show
  account names instead of "Player 12345". Two people with the same name become "Ash" and "Ash (2)".
- **Medal feed:** notable medals (Revenge, Double/Triple Kill, Long Shot, Last Bullet, Team Wipe...) flash
  on your kill feed as `MEDAL  Double Kill`, only for you. Everyday ones (Clear Kill, Head shot...) just count.
- **Hosting:** the game name defaults to "<your name>'s game".
- **Debug builds:** PROFILE has DEBUG: PREVIEW SAMPLE CARD (the "Ash" numbers, never saved).

## The card
Header (avatar, name, K/D, win rate) / MAIN STATS (Matches Played, Wins, Ties, Losses, Kills, Deaths,
K/D, Head shots, Head shots %, Playtime) / BEST WEAPONS (name, K/D, kills, top 4 by K/D; a weapon needs
10+ kills to outrank others) / MEDALS / ACHIEVEMENTS (all 22, unearned ones dimmed, hover for how).

## How things are counted (all in `MedalRules.cs` / `CareerTracker.cs`)
Only the person at this machine is recorded - never bots, never other players. Bot kills DO count.

| Stat | Rule |
|---|---|
| Kills / Deaths | Every announced kill; a death by anyone (or yourself) counts. Team-kills (friendly fire) are not kills. |
| Weapon K/D | Kills with a weapon / times you died holding it. |
| Head shots | The Head shot medal count (kills by headshot). |
| Match result | Team Deathmatch that ends: your team won / lost / drew. Anything you leave early: the standing right now (team score, or points in Deathmatch): ahead = win, level = tie, behind = loss. |
| Counts as a match | You scored/died/assisted OR stayed 60 s, and at least one other player or bot was there. |
| Playtime | Seconds a match was running for you. |

| Medal | Rule (tunable constants are in `MedalRules`) |
|---|---|
| Clear Kill | plain kill: no headshot / knife / explosive / spin |
| Head shot, Knife, Explosive, 360 No Scope, Hip Fire, On Verge Of Death | the kill's style flag (same flags that score the kill) |
| Revenge | kill the player who last killed you |
| Major Streak | your streak reaches 5, 10, 15... |
| Stop Streak | kill someone on a streak of 3+ |
| Assist | you were the top non-killing damager |
| Double / Triple Kill | 2nd / 3rd kill inside 3 s (same window as the MULTI-KILL bonus) |
| King Slayer | kill the match leader (3+ kills, outright lead) |
| One Shot Multi Kill | 2+ kills from one trigger pull (once per pull) |
| Last Bullet | killing shot emptied the magazine |
| Team Wipe | TDM: your kill leaves the whole enemy team (2+) down |
| First Blood | first kill of the match |
| One Shot, One Kill | victim at full health, died to a single shot of yours |
| Close Combat / Long Shot | kill from <= 4 m / >= 30 m |
| Wall Bang | **cannot be earned yet** - shots stop at the first surface. The rule and the slot exist. |

## Saving
`user://account.json`. Written to `.tmp`, previous save copied to `.bak`, then renamed over - a crash
mid-save cannot corrupt it. Load order: main -> backup -> if both unreadable the damaged file is kept
as `account.corrupt.json` and a new account starts. Saved every 10 s while playing, when a match ends,
when you leave, on window close and when an Android app is backgrounded. The file is plain JSON: it
is trivially editable, so stats are honour-system (fine for LAN; a ranked mode would need a server).

## Networking
When a client connects it broadcasts its card; every existing player answers with theirs
(`CareerTracker.ReceiveCard`, reliable). Peers re-send at match end. The sender id comes from the
engine (`GetRemoteSenderId`), so nobody can post a card as someone else. Cards are untrusted:
16 KB cap, all numbers clamped, names sanitised (control / invisible / right-to-left-override
characters removed, 20 chars), 32-weapon cap, unknown medals ignored, <= ~3 cards/s per sender.
A card is ~1.5 KB; an avatar is two small numbers.

## Files
New: `scripts/account/` (Medal, CareerStats, MedalRules, PlayerCard, PlayerCardCodec, PlayerDirectory,
PlayerAccount, CareerTracker, SampleCard), `scripts/ui/` (AvatarView, StatCard, AccountScreen).
Everything in `scripts/account/` except PlayerAccount and CareerTracker is pure C# (no Godot types).
Edited (small hooks): `project.godot` (autoload CareerTracker), `BotRoster` (person names),
`GameWorld` (BeginMatch), `GameSession` (FinishMatch / EndSession), `WeaponSwitcher` (shot serial +
NoteHit), `TeamIndicator` (live name), `PlayerHud` (medal feed line), `LanMenu` (first-run, chip, PROFILE, account screens),
`PauseMenu` (PLAYERS), `MenuStyle` (PlayerRow), `GameUiState` (comment).

## Known limits / decisions
- The requested list had **"360 No Scope" twice** (190 and 26). One medal is kept; tell me what the
  second was meant to be and it is a one-line add (`Medal.cs` + a rule).
- Deathmatch has no end condition, so its result is decided when you leave (see table).
- Players only see each other's cards while in the same session; there is no LAN-wide "who is online"
  list outside a match yet (would need a UDP presence beacon like the game-discovery one).
- Avatars are 10 emblems x 8 colours (existing icons); photo avatars are not supported.
- No stat reset button yet (delete `account.json` in the user-data folder).

## Test checklist
1. Fresh install -> CREATE ACCOUNT appears; empty name is refused; CREATE -> main menu shows chip.
2. PROFILE shows zeros; EDIT changes name/avatar and the chip follows. DEBUG: PREVIEW SAMPLE CARD.
3. VS BOTS: kill bots -> Esc > PLAYERS > you -> kills, weapon row, Head shot / Clear Kill rise.
   Quit; relaunch; numbers are still there.
4. Two instances (Debug > Run Multiple Instances): host + join with different names. Kill feed,
   scoreboard and (TDM) name tags show the names; PLAYERS lists both; open the other card.
5. TDM to the score limit: Wins/Losses +1 on each side. Leave a Deathmatch while ahead -> Win.
6. Kill a few players inside 3 s -> Double / Triple Kill. Shotgun two players in one blast -> One Shot Multi Kill.
7. Delete `account.json` but keep `.bak` -> game restores from the backup.
