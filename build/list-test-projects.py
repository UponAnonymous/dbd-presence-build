#!/usr/bin/env python3
"""Prints the test project directories to run on an OS ("linux" or "windows"), or one of the two Windows halves.

Every directory under tests/ with a matching .csproj is a test project. Projects whose TargetFramework is
Windows-specific run only on Windows; all others run on both. Keeping this in one place means a new test
project is picked up by CI automatically (and an empty one fails, because zero tests exit with code 8).

  list-test-projects.py linux            every project that runs on Linux
  list-test-projects.py windows          every project (the whole Windows evidence in one job, as the Release workflow runs it)
  list-test-projects.py windows-pc       every project except the ones that need a real desktop (they can run on the owner's PC)
  list-test-projects.py windows-desktop  only the projects that need a real desktop (they always run on a GitHub-hosted machine)

A project needs a real desktop when its .csproj says <RequiresInteractiveDesktop>true</RequiresInteractiveDesktop>: it
drives the app's windows, captures windows or writes screenshots. A job on the owner's PC runs on a private desktop with
no screen (docs/owner/free-ci.md), and a screenshot there could show the owner's screen. build/check-workflows.py checks
that a workflow job that runs these projects uses vars.HOSTED_WINDOWS_RUNNER.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
MODES = ("linux", "windows", "windows-pc", "windows-desktop")


def test_projects() -> list[tuple[pathlib.Path, bool, bool]]:
    """(directory, windows_only, needs_desktop) for every test project, in name order."""
    found = []
    for directory in sorted((ROOT / "tests").iterdir()):
        project = directory / f"{directory.name}.csproj"
        if not project.is_file():
            continue
        text = project.read_text(encoding="utf-8")
        tfm = re.search(r"<TargetFramework>(.*?)</TargetFramework>", text)
        windows_only = tfm is not None and ("windows" in tfm.group(1).lower() or "WindowsTfm" in tfm.group(1))
        needs_desktop = re.search(r"<RequiresInteractiveDesktop>\s*true\s*</RequiresInteractiveDesktop>", text, re.IGNORECASE) is not None
        found.append((directory, windows_only, needs_desktop))
    return found


def select(mode: str) -> list[str]:
    chosen = []
    for directory, windows_only, needs_desktop in test_projects():
        if mode == "linux":
            keep = not windows_only
        elif mode == "windows":
            keep = True
        elif mode == "windows-pc":
            keep = not needs_desktop
        else:
            keep = needs_desktop
        if keep:
            chosen.append(directory.relative_to(ROOT).as_posix())
    return chosen


def main() -> int:
    mode = sys.argv[1] if len(sys.argv) > 1 else "linux"
    if mode not in MODES:
        print(f"ERROR unknown mode '{mode}'; use one of: {', '.join(MODES)}", file=sys.stderr)
        return 2
    for line in select(mode):
        print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main())
