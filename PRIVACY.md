# OpenVersus Privacy Policy

_Last updated: 2026-05-08_

OpenVersus is a community-run, donation-funded private game server for MultiVersus. This policy explains what data we collect, why, and what rights you have over it.

We aim to read like humans wrote this. If anything is unclear, ask in [Discord](https://discord.gg/openversus) — that's the same channel you use to reach us for any data request.

---

## 1. Who runs OpenVersus

OpenVersus is operated by a small group of volunteer maintainers. The project is fiscally hosted on Open Collective; donations cover server hosting, bandwidth, and incidental operational costs. The project does not have a corporate parent and does not monetize player data.

For purposes of GDPR, the OpenVersus operators are the **data controller** for any data described in this policy.

## 2. What we collect

We only collect what's needed to run multiplayer matches and a competitive ranked ladder. There is no advertising, no behavioral tracking, no third-party analytics, and no monetization of personal data.

**When you connect to play:**

- **IP address** — used to route matches to the closest rollback server, classify your home region, and disambiguate accounts that share an in-game name.
- **Platform identifiers** — your Steam ID, Epic Online Services ID, and/or hardware ID, sent by the game client at login. Used to identify your account across sessions, prevent ban evasion, and resolve same-household account conflicts. Phone numbers, email addresses, real names, and payment data are **not** collected — we don't ask for them and the game client does not transmit them.
- **In-game display name** — chosen by you in the game client.
- **Character and skin selection** — for matchmaking display and matchmaking history.

**While you play:**

- **Match results** — winners, losers, scores, character used, mode (1v1 / 2v2), region the match was hosted in.
- **Personal stats** — wins, losses, ringouts, damage dealt, character-specific stats. Used to render your profile and the public leaderboard.
- **ELO rating** — overall, and per-character. Used to match you with similar-skill opponents.
- **Friend list and block list** — entries you create explicitly through the in-game friend / block UI.
- **Network telemetry** — ping, packet loss percentage, and rollback corrections during a match. Used for desync detection and connection-quality logging. Not used for any kind of behavioral analysis.

**Match archives:**

For each completed game, we store a small compressed record (typically <20 KB) containing match metadata — players, characters, scores, and per-player network stats. We use this data to display fun, anonymized stats (most-played characters, win rates, leaderboard movement) and to investigate rollback / desync bugs after the fact. It does not include voice, chat, replay video, or input streams.

**On our website (openversus.org):**

- Server access logs (IP, user-agent, timestamp, URL) for normal web-server operation. Retained for ~30 days and used only for troubleshooting and abuse prevention.
- A short admin-login cookie if you happen to be an OpenVersus operator. This isn't set for normal players.
- We do not use any third-party analytics, ad networks, or trackers.

## 3. Why we collect it (legal basis)

Under GDPR Article 6, our lawful bases for processing are:

- **Performance of a contract (Art 6(1)(b))** — when you connect to play, we process your account data, IP, and match results because that is the service you've signed up for. Without it, there is no game.
- **Legitimate interest (Art 6(1)(f))** — for security logging, abuse prevention (e.g. blocklist enforcement), and post-hoc investigation of rollback / desync bugs. The legitimate interest is in keeping the service running and matches playable.
- **Consent (Art 6(1)(a))** — only relevant for opt-in features (e.g. friend invitations you choose to send). You can revoke any such consent by removing the entry in-game.

We do **not** rely on any other basis (no marketing consent, no profiling for automated decisions, no third-party data sales). There is no automated decision-making in the GDPR Art 22 sense — matchmaking pairs players by ELO and region, but no automated decision produces legal effects on you.

## 4. Where your data is stored

OpenVersus operates infrastructure in:

- **United States (Ashburn, VA region)** — primary host for North American players.
- **United Kingdom (Manchester region)** — host for European players.

Match data for EU-vs-EU matches stays on our EU infrastructure. When a match crosses regions (e.g. a NA player vs an EU player when matchmaking can't find a same-region opponent), match data is processed transiently on whichever host the match runs on. Persistent data (account, ELO, stats) is replicated across both hosts.

For European users, this means:

- Your account, friend list, ELO, and stats are processed and stored on infrastructure that includes the EU host.
- Match data for EU-vs-EU matches stays in the EU.
- For cross-region matches, transient match data may be processed on US infrastructure under Art 6(1)(b) ("performance of contract" — necessary to play the match you initiated).

## 5. How long we keep it

- **Account data** (your record, ELO, stats, friend/block lists, cosmetics): retained while your account is active.
- **Match archives**: retained indefinitely as part of the public competitive history. Anonymized when an account is deleted (your name and ID are replaced; the match record itself remains so the other participants' history stays intact).
- **Web access logs**: ~30 days.
- **Server-side rollback / debug logs**: typically ~14 days, longer for crash dumps under active investigation.
- **Redis transient state** (current connection, lobby, ongoing match): seconds to hours; cleared automatically when you disconnect.

We are working toward an automatic inactivity-based deletion (e.g. accounts inactive for 12+ months). Until that ships, you can request deletion at any time using the link below.

## 6. Who we share it with

We share **none** of your data with third parties for marketing, advertising, analytics, or any commercial purpose.

The only data leaving our infrastructure is:

- **Public leaderboards** — your in-game name, ELO, and rank are visible to anyone who views the leaderboard.
- **Donations via Open Collective** — if you donate, Open Collective processes payment under their own privacy policy. We never see your payment details.
- **Hosting providers** — our servers run on commercial cloud infrastructure (current providers operate the EU and US hosts). They process the data we put on their machines under standard data-processor agreements.

If we are ever legally required to disclose data (e.g. court order), we will, but we have never received such a request.

## 7. Your rights

Under GDPR, you have the right to:

- **Access** the data we hold about you (Art 15)
- **Correct** inaccurate data (Art 16)
- **Erase** your data — see section 8 (Art 17)
- **Restrict** processing while a complaint is being resolved (Art 18)
- **Port** your data to another service (Art 20) — limited; we'll provide an export of your account, ELO, stats, and friend list on request
- **Object** to processing on legitimate-interest grounds (Art 21)
- **Lodge a complaint** with your national data protection authority

Outside the EU, equivalent rights apply under your local law (CCPA in California, etc.) — we honor the same access and deletion rights regardless of jurisdiction.

## 8. How to exercise your rights

The fastest path for the most common request:

- **Delete your account and personal data** → use the [self-serve form at /delete-account](/delete-account). Submissions are reviewed by an OpenVersus operator and processed within ~7 days; in line with GDPR Art 12(3) we will respond within 30 days.

For other requests (access, correction, portability, objection, complaint):

- **Discord**: open a ticket in our Discord at <https://discord.gg/openversus> and tag a maintainer.
- **GitHub Issue**: <https://github.com/openversus> (best for non-urgent / technical questions; the tracker is public so don't include sensitive personal info in the issue body).

We will respond within 30 days. There is no charge for any data request unless it is manifestly unfounded or excessive (in which case we will tell you in advance and explain why).

## 9. Children's privacy

OpenVersus does not knowingly collect data from children under 13 (or under 16 in the EU). MultiVersus itself is rated for users 10+, and we rely on the platform-level age gates (Steam, Epic) to filter eligibility. If we become aware that a younger user has registered, we will delete their account.

## 10. Security

- All authentication tokens are JWT-signed; tokens never travel to third parties.
- Database access is restricted to OpenVersus maintainers via SSH keys.
- We do not store payment data of any kind.
- Production hosts run behind a firewall with non-essential ports closed.

In the (so far hypothetical) event of a data breach affecting personal data, we will notify affected users via Discord and an Open Collective update within 72 hours, in line with GDPR Art 33–34.

## 11. Changes to this policy

When we make material changes to this policy, we will:

- Update the "Last updated" date at the top.
- Post a notice in Discord and in an Open Collective update.
- For substantive changes (new categories of data, new transfer destinations, new lawful bases), give 30 days' notice before they take effect.

The full version history of this document lives in our public GitHub repository at <https://github.com/openversus> — every change is on the record.

## 12. Contact

- **Discord**: <https://discord.gg/openversus> (fastest)
- **GitHub**: <https://github.com/openversus>
- **Open Collective**: <https://opencollective.com/openversus>

---

_OpenVersus is a fan-made, non-commercial private server. It is not affiliated with, endorsed by, or sponsored by Warner Bros. Games, Player First Games, or the official MultiVersus development team. "MultiVersus" is a trademark of Warner Bros. Entertainment Inc._
