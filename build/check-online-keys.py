#!/usr/bin/env python3
"""Checks the compiled-in service key sets (src/DbdPresence.Online/TrustedServiceKeys.json; ADR-0016, ADR-0012).

  check-online-keys.py            development: staging must be valid; production may still be empty. A staging entry marked
                                  "awaitingKey": true (its public key is not issued yet) is allowed and reported as a warning.
  check-online-keys.py --release  release: production must be non-empty, valid, free of awaiting-key entries, share nothing with staging
                                  and cover every purpose (licence, config, pack-index and roster, releases, key-replacement)

Both modes also check that service/wrangler.toml's OFFLINE_PUBLIC_KEYS of each environment lists exactly the release and recovery public
keys of the same set (V2-S6, ADR-0012): the service refuses an uploaded feed or key replacement that the app would refuse, and holds no
private key for either. Within one set, no public key may appear under two kids (security review 2026-10-25, L4): one key under two
names would widen what it may sign past its single purpose set.

  check-online-keys.py --self-test  runs the rule checks against built-in good and bad examples (CI runs it before the real check)
"""
import base64
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
FILE = ROOT / "src" / "DbdPresence.Online" / "TrustedServiceKeys.json"
WRANGLER = ROOT / "service" / "wrangler.toml"
PURPOSES = {"licence", "config", "pack-index", "roster", "releases", "key-replacement"}
# The content key signs the pack index and the character roster (ADR-0012 addendum, PLAN_V2_S5B section 2.3); no other
# key may sign the roster, and no key may sign anything else together with another purpose. The release key signs only
# release feeds and the recovery key only key replacements (V2-S6); both are made and kept offline by the owner.
ALLOWED_PURPOSE_SETS = [{"licence"}, {"config"}, {"pack-index", "roster"}, {"releases"}, {"key-replacement"}]
OFFLINE_PURPOSES = ({"releases"}, {"key-replacement"})
P256_SPKI_PREFIX = bytes.fromhex("3059301306072a8648ce3d020106082a8648ce3d030107")


def problems_in(name, keys, allow_awaiting):
    found = []
    seen = set()
    seen_spki = {}
    for key in keys:
        kid = key.get("kid", "")
        if not kid or kid in seen:
            found.append(f"{name}: missing or duplicate kid '{kid}'")
        seen.add(kid)
        if "uitest" in kid.lower():
            found.append(f"{name}: key '{kid}' looks like the UI-test key, which only the UI-test build may trust (D32)")
        if not key.get("purposes") or set(key["purposes"]) not in ALLOWED_PURPOSE_SETS or len(key["purposes"]) != len(set(key["purposes"])):
            found.append(f"{name}: key '{kid}' needs exactly one of the purpose sets {[sorted(s) for s in ALLOWED_PURPOSE_SETS]}")
        if key.get("awaitingKey") is True:
            if not allow_awaiting:
                found.append(f"{name}: key '{kid}' is awaiting its key; a real public key is required")
            elif "spki" in key:
                found.append(f"{name}: awaiting-key entry '{kid}' must not carry a key")
            else:
                print(f"WARNING {name}: key '{kid}' is awaiting its public key (it is added when the key is made)", file=sys.stderr)
            continue
        text = key.get("spki", "")
        if text in seen_spki:
            found.append(f"{name}: key '{kid}' has the same public key as '{seen_spki[text]}'; each key may appear once per set")
        seen_spki.setdefault(text, kid)
        try:
            der = base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))
        except ValueError:
            der = b""
        if len(der) != 91 or not der.startswith(P256_SPKI_PREFIX):
            found.append(f"{name}: key '{kid}' is not a P-256 SubjectPublicKeyInfo")
    return found


def offline_keys_in_wrangler(environment):
    """The OFFLINE_PUBLIC_KEYS value of [env.<environment>.vars] in service/wrangler.toml, parsed, or None when missing."""
    text = WRANGLER.read_text(encoding="utf-8")
    section = re.search(rf"^\[env\.{environment}\.vars\]\n(.*?)(?=^\[)", text, re.S | re.M)
    if not section:
        return None
    line = re.search(r"^OFFLINE_PUBLIC_KEYS\s*=\s*'(.*)'\s*$", section.group(1), re.M)
    return json.loads(line.group(1)) if line else None


def wrangler_problems(name, keys):
    expected = sorted(
        ({"kid": k["kid"], "purposes": k["purposes"], "spki": k["spki"]} for k in keys if set(k.get("purposes", [])) in OFFLINE_PURPOSES and "spki" in k),
        key=lambda k: k["kid"],
    )
    actual = offline_keys_in_wrangler(name)
    if actual is None:
        return [f"service/wrangler.toml [env.{name}.vars] has no OFFLINE_PUBLIC_KEYS line"]
    if sorted(actual, key=lambda k: k.get("kid", "")) != expected:
        return [f"service/wrangler.toml OFFLINE_PUBLIC_KEYS ({name}) must list exactly the {name} release and recovery public keys of TrustedServiceKeys.json"]
    return []


def self_test():
    """Checks the rules against fixed examples, so a check that silently stopped finding problems fails CI."""
    spki_a = base64.urlsafe_b64encode(P256_SPKI_PREFIX + bytes(range(1, 69))).decode().rstrip("=")
    spki_b = base64.urlsafe_b64encode(P256_SPKI_PREFIX + bytes(range(2, 70))).decode().rstrip("=")
    good = [{"kid": "release-x-1", "spki": spki_a, "purposes": ["releases"]}, {"kid": "recovery-x-1", "spki": spki_b, "purposes": ["key-replacement"]}]
    cases = [
        ("distinct keys", good, False),
        ("duplicate kid", good + [dict(good[0])], True),
        ("duplicate public key under another kid", good + [{"kid": "config-x-1", "spki": spki_a, "purposes": ["config"]}], True),
        ("mixed purposes", [{"kid": "mixed-x-1", "spki": spki_a, "purposes": ["releases", "config"]}], True),
        ("not P-256", [{"kid": "short-x-1", "spki": "AAAA", "purposes": ["config"]}], True),
    ]
    failed = [name for name, keys, bad in cases if bool(problems_in("self-test", keys, allow_awaiting=False)) != bad]
    for name in failed:
        print(f"ERROR self-test: the '{name}' case was not judged correctly", file=sys.stderr)
    if not failed:
        print(f"check-online-keys self-test: {len(cases)} cases passed")
    return 1 if failed else 0


def main():
    if "--self-test" in sys.argv[1:]:
        return self_test()
    release = "--release" in sys.argv[1:]
    data = json.loads(FILE.read_text(encoding="utf-8"))
    staging, production = data["staging"], data["production"]
    found = problems_in("staging", staging, allow_awaiting=True) + problems_in("production", production, allow_awaiting=False)
    found += wrangler_problems("staging", staging) + wrangler_problems("production", production)
    if not staging:
        found.append("staging key set is empty")
    shared = {k["kid"] for k in staging} & {k["kid"] for k in production}
    shared |= {k["spki"] for k in staging if "spki" in k} & {k["spki"] for k in production if "spki" in k}
    if shared:
        found.append(f"production shares keys with staging: {sorted(shared)}")
    if release:
        if not production:
            found.append("production key set is empty: run service/scripts/new-secret.mjs for production and fill it in")
        elif {p for k in production for p in k["purposes"]} != PURPOSES:
            found.append("production needs a licence key, a config key, a content key (pack-index and roster), a release key and a recovery key "
                         "(docs/owner/release-keys.md)")
    for line in found:
        print("ERROR " + line, file=sys.stderr)
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
