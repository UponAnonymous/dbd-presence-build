#!/usr/bin/env python3
"""Maps the app version in Directory.Build.props to the four-part MSIX package version.

The Store requires the last part to be 0 (it reserves it), so SemVer X.Y.Z (a pre-release suffix such as -beta.1 is dropped) becomes X.Y.Z.0.
Every part must fit 0..65535. build/pack-msix.ps1 calls this script, so there is one rule and it is tested here.

  python3 build/msix_version.py [path-to-Directory.Build.props]   prints the MSIX version
  python3 build/msix_version.py --self-test                       runs the checks (CI Linux job)
"""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

SEMVER = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?$")


def to_msix(version: str) -> str:
    match = SEMVER.match(version.strip())
    if not match:
        raise ValueError(f"'{version}' is not SemVer (X.Y.Z or X.Y.Z-suffix)")
    parts = [int(g) for g in match.groups()]
    if any(p > 65535 for p in parts):
        raise ValueError(f"'{version}' has a part above 65535, which MSIX does not allow")
    return ".".join(str(p) for p in parts) + ".0"


def version_from_props(path: Path) -> str:
    for node in ET.parse(path).getroot().iter("Version"):
        if node.text and node.text.strip():
            return node.text.strip()
    raise ValueError(f"no <Version> in {path}")


def self_test() -> None:
    assert to_msix("0.1.0") == "0.1.0.0"
    assert to_msix("1.2.3") == "1.2.3.0"
    assert to_msix(" 10.20.30 ") == "10.20.30.0"
    assert to_msix("0.2.0-beta.1") == "0.2.0.0"
    assert to_msix("65535.65535.65535") == "65535.65535.65535.0"
    for bad in ("", "1", "1.2", "1.2.3.4", "v1.2.3", "1.2.x", "65536.0.0", "1.2.3-"):
        try:
            to_msix(bad)
        except ValueError:
            continue
        raise AssertionError(f"'{bad}' should be rejected")
    props = Path(__file__).resolve().parent.parent / "Directory.Build.props"
    assert re.fullmatch(r"\d+\.\d+\.\d+\.0", to_msix(version_from_props(props)))
    print("msix_version self-test OK")


if __name__ == "__main__":
    try:
        if len(sys.argv) > 1 and sys.argv[1] == "--self-test":
            self_test()
        else:
            props_path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("Directory.Build.props")
            print(to_msix(version_from_props(props_path)))
    except (ValueError, OSError, ET.ParseError) as error:
        print(f"msix_version: {error}", file=sys.stderr)
        sys.exit(1)
