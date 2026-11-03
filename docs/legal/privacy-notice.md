# DBD Presence — privacy notice

DBD Presence is an unofficial, fan-made Discord Rich Presence companion for Dead by Daylight. It is not affiliated with or endorsed by Behaviour Interactive or Discord.

## Using the app

You need to sign in with Discord to use DBD Presence. Until you have signed in (and, while access is by invitation, entered an invite code), the app sends nothing to Discord and shows only the sign-in screen. Once you are in, your chosen status text goes only to the Discord app on the same PC, and it keeps working if the DBD Presence service cannot be reached; it stops only if the service owner withdraws your access. There is no telemetry and no crash reporting.

A licence file, signed by the service and encrypted for your Windows account, is kept on your PC. It proves that you signed in on this PC and lets the app keep working offline. Signing out or deleting your account removes it.

**Automatic detection** is off unless you turn it on. When it is on, the app looks only at the Dead by Daylight window on your screen to recognise what is happening in the game. This happens on your PC. The pictures are never sent to the service or anyone else, and they are not saved unless you turn on test recording yourself, which shows where the files go and lets you delete them.

## When you sign in

Signing in is required and uses Discord's sign-in page in your browser. While access is by invitation you also enter an invite code once; if the service owner opens DBD Presence to everyone, only the sign-in is needed. The service receives your Discord user ID and display name from Discord (scope `identify` only). It does not receive or keep your email address, and it does not show your avatar anywhere.

**What the service stores**

- your Discord user ID and display name;
- the dates you joined and last used the service;
- your account status (active or withdrawn) and whether you redeemed an invite code;
- a public key for each PC you sign in on (the private key stays on that PC and cannot be exported);
- session records (hashes of session tokens, never the tokens themselves).

**What it does not store:** your email address, your IP address history, your game activity, or anything from the game.

**What it can see:** each daily check-in (licence refresh) shows the service your IP address for that request and updates your last-seen time. The IP address is used to handle the request and for rate limiting; it is not kept as a history.

**Pictures on your Discord status**

Discord can only show pictures that are online. When you save or import your own pictures (from a pack or your PC) while signed in, the app puts a copy of each on the DBD Presence service at an unlisted address, ready for your Discord status. Each person can store up to 60 MB. It is not listed or shared, but anyone who sees your status can see it. It is deleted when you remove it, delete your account, or the owner removes it; Discord may show a cached copy for a while, and backups keep it for up to 30 days.

## Why it is used

- To provide the service you signed in for (sign-in, invites, synced packs): this is needed to provide the service you asked for (UK GDPR Article 6(1)(b)).
- Security logs, rate limits and preventing misuse: our legitimate interest in keeping the service safe (Article 6(1)(f)).

## How long data is kept

- Accounts are kept until you delete them. Inactive accounts are deleted after 12 months.
- Rate-limit counters: 24 hours.
- Security and audit logs: 90 days. When you delete your account, user identifiers in those logs are pseudonymised.
- Backups: database restore points are kept for 7 days and encrypted exports for 30 days. Deleted data can remain in backups until they expire.

## Your rights

You can delete your account at any time from the Account page in the app. You can ask for a copy of your data, or for correction, by emailing caylen_tucker@icloud.com; requests are answered within one month. You can also object to how your data is used. If you are unhappy with the answer, you can complain to the Information Commissioner's Office (ico.org.uk).

## Processors

The service runs on Cloudflare, which acts as a processor. Cloudflare may handle data outside the UK; those transfers are covered by Cloudflare's data processing addendum, which uses the UK International Data Transfer Addendum to the EU standard contractual clauses. Content images are fetched by Discord's own servers, not by this app, and the app never loads images from the internet.

## Who is responsible

The controller is Arcavale, which runs DBD Presence. Contact: caylen_tucker@icloud.com.
