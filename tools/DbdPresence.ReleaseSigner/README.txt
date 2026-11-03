DBD Presence release signer (owner tool, never shipped with the app)

It makes the release key and the recovery key, signs release feeds and signs key replacements.
It never connects to the network. Run it on your own PC, ideally with the network cable unplugged
and Wi-Fi off, and keep the keys on the offline drive.

Step-by-step guide: docs/owner/release-keys.md in the repository.

Quick reference (run from a Command Prompt in this folder):

  DbdPresence.ReleaseSigner.exe keygen release  --kid release-prod-1  --out E:\dbdp-keys
  DbdPresence.ReleaseSigner.exe keygen recovery --kid recovery-prod-1 --out E:\dbdp-keys
  DbdPresence.ReleaseSigner.exe sign-feed --key E:\dbdp-keys\release-prod-1.private.pem --channel stable --set production --packages <folder with the .nupkg files> --out releases-stable.json
  (--set production refuses a package that is not a production build: take the package from the Release workflow)
  DbdPresence.ReleaseSigner.exe resign --key E:\dbdp-keys\release-prod-1.private.pem --feed releases-stable.json --out releases-stable-2.json
  DbdPresence.ReleaseSigner.exe sign-key-replacement --key E:\dbdp-keys\recovery-prod-1.private.pem --revoke <old key> --add <new key>.public.json --first --out key-replacement-1.json
  (every later replacement: --previous <the last one> instead of --first, so its revocations are kept)
  DbdPresence.ReleaseSigner.exe verify --document releases-stable.json --trust TrustedServiceKeys.json --set production --packages <folder with the .nupkg files>

Never put a private key (.private.pem) in the repository, in a chat, in e-mail or in cloud storage.
The tool refuses to write keys inside a repository folder.
