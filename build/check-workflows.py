#!/usr/bin/env python3
"""CI supply-chain rules for the GitHub Actions workflows (threat-model review 2026-10-25, TM-13).

Every file in .github/workflows (*.yml, *.yaml) must:

* pin every `uses:` to a full 40-character commit SHA (`owner/repo[/path]@<sha>`). A tag or branch can be moved to other
  code after review; a SHA cannot. Local actions (`./...`) are allowed, and a Docker action must name its image by digest
  (`docker://image@sha256:<64 hex>`);
* declare `permissions:` at the top level, so no job silently gets the repository's default token rights;
* never use the `pull_request_target` trigger, which runs a pull request's workflow with the base repository's secrets
  and a write token;
* take every job's runner from a repository variable with the GitHub-hosted image as the fallback
  (`runs-on: ${{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}` or `runs-on: ${{ vars.WINDOWS_RUNNER || 'windows-2025' }}`), so the
  owner can move the checks to a self-hosted runner on their own PC by setting two variables (D39, docs/owner/free-ci.md).
  A workflow that uses repository secrets or a deployment environment (deploy, backup, release) must use its own variables instead
  (`vars.SECRETS_LINUX_RUNNER` / `vars.SECRETS_WINDOWS_RUNNER`): a long-lived runner that builds with third-party packages
  must not also hold the Cloudflare token or the release secrets, so these stay on GitHub's fresh machines unless the
  owner deliberately points them elsewhere.
* run a job that needs a real desktop, or that builds the owner's release signer, on a GitHub-hosted machine only: a job that runs the app's
  screen tests or writes screenshots (`DBDP_SCREENSHOT_DIR`, `DbdPresence.UiTests`, `list-test-projects.py windows` or `windows-desktop`)
  or that publishes or uploads the release signer (`tools/DbdPresence.ReleaseSigner`, `signer-publish`, `release-signer-win-x64`) must take
  its runner from `${{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}`, a variable the owner's local CI program never sets. A screen job on
  the owner's PC would run on a private desktop with no screen, and its screenshots could show the owner's screen; the signer is what the
  owner's release keys trust, so it must not be built on a machine that has run other jobs (local CI round 2, red-team F11, D41). The
  hosted form is allowed for those jobs only, also in a workflow with secrets (a fresh GitHub-hosted machine). Jobs are told apart by their
  place under `jobs:` (security review 2026-10-30, H2 and M5). The same holds for the owner's local CI program itself
  (`tools/DbdPresence.LocalCi`, `local-ci-win-x64`): the owner runs it with administrator rights, so it is published by a job on a hosted machine only,
  never on the machine of the lane that runs third-party code (local CI round 3, red-team M6);
* use no cache in a job that can run on the owner's PC (`runs-on` from `vars.LINUX_RUNNER`): no `cache:` input of a setup action and no `actions/cache`. The
  Actions cache is shared by every job of the repository under the same key, so a job on the PC that writes a cache entry could hand modified packages to a
  job on GitHub's machines that holds secrets (local CI round 3, red-team L7);
* use no cache in a job that builds a program the owner trusts and runs with real rights: the release signer, the local CI program, the evaluator or the study
  capture tool (`tools/DbdPresence.ReleaseSigner`, `.LocalCi`, `.Eval`, `.StudyCapture` and their artifact names), and in no job of `release.yml` or `ci-windows.yml`.
  Those builds must restore every package from the lock files, because a cache entry written by a job that ran attacker-chosen code would otherwise end up in them
  (local CI round 3, commit review of the cache rule). `ci-windows.yml` is held to it as a whole file since round 4 (red-team F4): its `windows` job publishes the
  development installer, the portable build and the spike tool that the owner downloads and runs, and a rule by file is simpler to keep true than a rule by job;
* allow only a short list of actions (`actions/checkout`, `actions/upload-artifact`, `actions/setup-node`, `actions/setup-dotnet`) in every job that is held to
  the no-cache rule above, and in every job of a workflow that holds secrets, and keep every cache out of those jobs by their inputs (any input with `cache` in its
  name that is not `false`, such as `cache`, `package-manager-cache`, `bundler-cache`, `cache-from`) and by the text `type=gha` and the Actions cache variables. A list of
  spellings cannot keep the cache out of a job (an action can use the shared cache by itself: `docker/build-push-action` with `cache-from: type=gha`, `ruby/setup-ruby` with
  `bundler-cache`, gradle's setup action, a composite action); a list of the actions allowed in the job can. A local action in such a job is read and held to the same rules.
  `actions/setup-node` must say `package-manager-cache: false` there, because setup-node caches by itself once a `package.json` names a `packageManager` (red-team round 4, F10;
  QA round 4, minor 7). A job that holds secrets is held to this too: it is the job a poisoned cache entry would be restored into;
* write every `uses:` with its value on the same line, so the pin can be read.

  check-workflows.py              checks .github/workflows
  check-workflows.py FILE|DIR...  checks the given files or folders instead
  check-workflows.py --self-test  runs the rules against built-in good and bad examples (CI runs it before the real check)

The check reads the files as text, line by line (no YAML library needed). Comments are ignored. It also reads the other spellings
YAML allows: quoted keys (`"runs-on":`), flow-style jobs and steps (`jobs: {a: {runs-on: ...}}`, `steps: [{uses: ...}]`), and these
signs that a workflow holds secrets: `secrets.NAME`, `secrets['NAME']`, `toJSON(secrets)`, `secrets: inherit`, `id-token: write` and
an `environment:`.

This is accident prevention, not a security boundary. Anyone who can push a branch can change a workflow file (or add a step that
runs any command) and run it as the runner's user, whatever this script says. The boundary is who may push to the repository and
how the runner is isolated (docs/owner/free-ci.md).
"""
import pathlib
import re
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
WORKFLOWS = ROOT / ".github" / "workflows"
# `uses:` as a block line, as a list item, or inside a flow mapping (`- {uses: x@sha, with: {...}}`); the key may be quoted.
USES = re.compile(r"""(?:^\s*(?:-\s+)?|[{,]\s*)["']?uses["']?\s*:\s*(?P<value>"[^"]*"|'[^']*'|[^\s,}]+)""")
PINNED = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_./-]+)?@[0-9a-f]{40}$")
DOCKER_PINNED = re.compile(r"^docker://[^@\s]+@sha256:[0-9a-f]{64}$")
TOP_LEVEL_PERMISSIONS = re.compile(r"""^["']?permissions["']?\s*:""")
FORBIDDEN_TRIGGER = re.compile(r"\bpull_request_target\b")
# `runs-on` anywhere in a line (block or flow style, key quoted or not); the value is everything after the colon.
RUNS_ON = re.compile(r"""(?:^\s*(?:-\s+)?|[{,]\s*)["']?runs-on["']?\s*:\s*(?P<value>.*)$""")
_END = r"\s*(?:[,}\]]|$)"
RUNNER_FROM_VARIABLE = re.compile(r"^\$\{\{ vars\.(LINUX_RUNNER \|\| 'ubuntu-[0-9.]+'|WINDOWS_RUNNER \|\| 'windows-[0-9]+') \}\}" + _END)
SECRETS_RUNNER_FROM_VARIABLE = re.compile(r"^\$\{\{ vars\.(SECRETS_LINUX_RUNNER \|\| 'ubuntu-[0-9.]+'|SECRETS_WINDOWS_RUNNER \|\| 'windows-[0-9]+') \}\}" + _END)
# A hosted image, for a job that needs a real desktop; the variable is never set by the owner's local CI program.
HOSTED_RUNNER_FROM_VARIABLE = re.compile(r"^\$\{\{ vars\.HOSTED_WINDOWS_RUNNER \|\| 'windows-[0-9]+' \}\}" + _END)
# What marks a job as one that needs a real desktop: the screenshot folder, the UI test project, and the test-project lists that include it
# (`windows` is every project, `windows-desktop` only the screen-test ones; `windows-pc` is the half that needs no screen).
UI_MARKERS = re.compile(r"\bDBDP_SCREENSHOT_DIR\b|DbdPresence\.UiTests|DbdPresence\.Platform\.Windows\.Tests|list-test-projects\.py\s+windows(?:-desktop)?(?![\w-])")
# What marks a job as one that publishes (or uploads) the release signer, the program the owner's release keys trust: it may only run on a hosted machine.
SIGNER_MARKERS = re.compile(r"tools/DbdPresence\.ReleaseSigner\b|\bsigner-publish\b|\brelease-signer-win-x64\b")
# The same for the owner's local CI program (DbdLocalCi.exe): the owner runs it elevated, so it is built on a hosted machine only (local CI round 3, red-team M6).
LOCAL_CI_MARKERS = re.compile(r"tools/DbdPresence\.LocalCi\b|\blocal-ci-publish\b|\blocal-ci-win-x64\b")
HOSTED_MARKERS = re.compile(UI_MARKERS.pattern + "|" + SIGNER_MARKERS.pattern + "|" + LOCAL_CI_MARKERS.pattern)
# The owner's other tools, built by the same job as the signer and the local CI program (no cache there either).
OWNER_TOOL_MARKERS = re.compile(r"tools/DbdPresence\.Eval\b|\beval-publish\b|\beval-win-x64\b|tools/DbdPresence\.StudyCapture\b|\bstudy-publish\b|\bstudy-capture-win-x64\b")
# A job with one of these builds something the owner trusts and runs with real rights: it may not use any cache.
NO_CACHE_MARKERS = re.compile(SIGNER_MARKERS.pattern + "|" + LOCAL_CI_MARKERS.pattern + "|" + OWNER_TOOL_MARKERS.pattern)
# An input that switches a cache on: any key with `cache` in its name (`cache`, `package-manager-cache`, `bundler-cache`, `enable-cache`, `cache-from`, `cache-to`, ...), in block or flow
# style, quoted or not, whatever its case. Its value is read by the code (`false` is the only value that keeps the cache off).
CACHE_KEY = re.compile(r"""(?:^\s*(?:-\s+)?|[{,]\s*)["']?(?P<key>[A-Za-z0-9_.-]*cache[A-Za-z0-9_.-]*)["']?\s*:\s*(?P<value>"[^"]*"|'[^']*'|[^\s,}]*)""", re.IGNORECASE)
# Inputs with `cache` in their name that name files a cache would read but do not switch anything on.
NON_ENABLING_CACHE_KEYS = frozenset({"cache-dependency-path", "cache-dependency-glob"})
# The Actions cache reached from a shell command: the buildx cache backend and the variables that carry the cache service's address and token.
CACHE_TEXT = re.compile(r"""\btype=gha\b|\bACTIONS_(?:CACHE_URL|RUNTIME_TOKEN|RESULTS_URL|CACHE_SERVICE_V2)\b""", re.IGNORECASE)
# The actions a job may use when it can run on the owner's PC, builds a program the owner trusts or holds secrets. Lower case: an action's name does not depend on its case.
RESTRICTED_ACTIONS = frozenset({"actions/checkout", "actions/upload-artifact", "actions/setup-node", "actions/setup-dotnet"})
SETUP_NODE = "actions/setup-node"
PACKAGE_MANAGER_CACHE_OFF = re.compile(r"""["']?package-manager-cache["']?\s*:\s*["']?false["']?\s*(?:[,}\]]|$)""", re.IGNORECASE)
# `uses:` with nothing after it (or only a block-scalar sign): the value is on a later line, where the pin cannot be read.
USES_VALUE_ELSEWHERE = re.compile(r"""(?:^\s*(?:-\s+)?|[{,]\s*)["']?uses["']?\s*:\s*(?:[|>][-+0-9]*)?$""")
LIST_ITEM = re.compile(r"""^\s*-\s""")
MOST_NESTED_LOCAL_ACTIONS = 3
# A job that can run on the owner's PC: its runner comes from LINUX_RUNNER.
PC_LANE_RUNNER = re.compile(r"""\bvars\.LINUX_RUNNER\b""")
# Files in which no job may use a cache, whatever the job builds.
NO_CACHE_FILES = ("release.yml", "release.yaml", "ci-windows.yml", "ci-windows.yaml")
JOBS_KEY = re.compile(r"""^["']?jobs["']?\s*:\s*(?P<rest>.*)$""")
JOB_KEY = re.compile(r"""^\s+["']?[A-Za-z0-9_-]+["']?\s*:""")
# Signs that a workflow holds secrets or can mint credentials (so it must use the SECRETS_ runner variables): a named secret, an indexed
# secret, the whole secrets object, inherited secrets, an OIDC token, or a deployment environment.
USES_SECRETS = re.compile(
    r"""\bsecrets\.(?!GITHUB_TOKEN\b)[A-Za-z_]|\bsecrets\s*\[|\btoJSON\s*\(\s*secrets\s*\)|\bsecrets\s*:\s*inherit\b|\bid-token\s*:\s*write\b"""
    r"""|(?:^\s*|[{,]\s*)["']?environment["']?\s*:""",
    re.IGNORECASE,
)


def strip_comment(line: str) -> str:
    """The line without a trailing `# comment` (a `#` inside quotes is kept)."""
    quote = None
    for i, c in enumerate(line):
        if quote:
            if c == quote:
                quote = None
        elif c in ("'", '"'):
            quote = c
        elif c == "#" and (i == 0 or line[i - 1] in " \t"):
            return line[:i].rstrip()
    return line.rstrip()


def unquote(value: str) -> str:
    """The value without one pair of surrounding quotes (a runner expression may be written in quotes)."""
    if len(value) >= 2 and value[0] == value[-1] and value[0] in ("'", '"'):
        return value[1:-1]
    # A quoted value that continues with flow punctuation (`"${{ ... }}", ...`): keep only the quoted part.
    if value[:1] in ("'", '"'):
        end = value.find(value[0], 1)
        if end > 0:
            return value[1:end]
    return value


def marker_job_lines(lines: list[str], markers: re.Pattern[str]) -> dict[int, bool]:
    """For every line number (1-based): whether the job it belongs to has a line (comments ignored) that matches `markers`.

    Block-style jobs are told apart by their indentation under the top-level `jobs:` key. A marker outside the `jobs:` block (workflow-level
    `env:`) counts for every job. A flow-style `jobs:` (everything on one line), or a file without a readable `jobs:` block, is one single job.
    """
    cleaned = [strip_comment(raw) for raw in lines]
    one_job = {number: any(markers.search(c) for c in cleaned) for number in range(1, len(lines) + 1)}
    jobs_at = next((i for i, c in enumerate(cleaned) if (m := JOBS_KEY.match(c)) and not m.group("rest").strip()), None)
    if jobs_at is None:
        return one_job
    child_indent = None
    starts: list[int] = []
    end_of_jobs = len(cleaned)
    for i in range(jobs_at + 1, len(cleaned)):
        line = cleaned[i]
        if not line.strip():
            continue
        indent = len(line) - len(line.lstrip())
        if indent == 0:
            end_of_jobs = i
            break
        if child_indent is None:
            child_indent = indent
        if indent == child_indent and JOB_KEY.match(line):
            starts.append(i)
    if not starts:
        return one_job
    outside = [c for i, c in enumerate(cleaned) if i <= jobs_at or i >= end_of_jobs]
    everywhere = any(markers.search(c) for c in outside)
    flags = dict.fromkeys(range(1, len(lines) + 1), everywhere)
    bounds = [*starts, end_of_jobs]
    for first, last in zip(bounds, bounds[1:]):
        has_marker = everywhere or any(markers.search(cleaned[i]) for i in range(first, last))
        for i in range(first, last):
            flags[i + 1] = has_marker
    return flags


def screen_job_lines(lines: list[str]) -> dict[int, bool]:
    """For every line number (1-based): whether the job it belongs to must run on a hosted machine (needs a real desktop or builds the signer or the local CI program: see HOSTED_MARKERS)."""
    return marker_job_lines(lines, HOSTED_MARKERS)


def pc_lane_lines(lines: list[str]) -> dict[int, bool]:
    """For every line number (1-based): whether the job it belongs to can run on the owner's PC (its `runs-on` takes the runner from `vars.LINUX_RUNNER`).

    Jobs are told apart as in screen_job_lines. A flow-style `jobs:` or a file without a readable `jobs:` block is one single job.
    """
    cleaned = [strip_comment(raw) for raw in lines]

    def lane_of(first: int, last: int) -> bool:
        for i in range(first, last):
            match = RUNS_ON.search(cleaned[i])
            if match and PC_LANE_RUNNER.search(match.group("value")):
                return True
        return False

    flags = dict.fromkeys(range(1, len(lines) + 1), False)
    jobs_at = next((i for i, c in enumerate(cleaned) if (m := JOBS_KEY.match(c)) and not m.group("rest").strip()), None)
    if jobs_at is None:
        whole = lane_of(0, len(cleaned))
        return dict.fromkeys(flags, whole)
    child_indent = None
    starts: list[int] = []
    end_of_jobs = len(cleaned)
    for i in range(jobs_at + 1, len(cleaned)):
        line = cleaned[i]
        if not line.strip():
            continue
        indent = len(line) - len(line.lstrip())
        if indent == 0:
            end_of_jobs = i
            break
        if child_indent is None:
            child_indent = indent
        if indent == child_indent and JOB_KEY.match(line):
            starts.append(i)
    if not starts:
        return dict.fromkeys(flags, lane_of(0, len(cleaned)))
    bounds = [*starts, end_of_jobs]
    for first, last in zip(bounds, bounds[1:]):
        lane = lane_of(first, last)
        for i in range(first, last):
            flags[i + 1] = lane
    return flags


def indent_of(line: str) -> int:
    return len(line) - len(line.lstrip())


def step_range(cleaned: list[str], index: int, flow: bool) -> range:
    """The lines (0-based) of the step that holds line `index`: from its `- ` list item to the line before the next line that is indented no deeper than that item.

    A step written in flow style (`- {uses: ..., with: {...}}`) is its own line.
    """
    if flow:
        return range(index, index + 1)
    start = index
    if not LIST_ITEM.match(cleaned[index]):
        here = indent_of(cleaned[index])
        for j in range(index - 1, -1, -1):
            if cleaned[j].strip() and indent_of(cleaned[j]) < here:
                if LIST_ITEM.match(cleaned[j]):
                    start = j
                break
    first = indent_of(cleaned[start])
    end = len(cleaned)
    for j in range(start + 1, len(cleaned)):
        if cleaned[j].strip() and indent_of(cleaned[j]) <= first:
            end = j
            break
    return range(start, end)


def local_action_problems(name: str, number: int, value: str, reason: str, root: pathlib.Path, depth: int) -> list[str]:
    """A local action in a restricted job is read and held to the same rules as the job (a composite action can hide a cache or a second action)."""
    if depth >= MOST_NESTED_LOCAL_ACTIONS:
        return [f"{name}:{number}: the local action {value} is nested {MOST_NESTED_LOCAL_ACTIONS} levels deep or more, so it is not read, and this job {reason}"]
    base = root.resolve()
    target = (base / value[2:]).resolve()
    if not target.is_relative_to(base):
        return [f"{name}:{number}: the local action {value} is outside the repository, and this job {reason}"]
    candidates = [target] if target.is_file() else [target / "action.yml", target / "action.yaml"]
    action_file = next((c for c in candidates if c.is_file()), None)
    if action_file is None:
        return [f"{name}:{number}: the local action {value} has no action.yml or action.yaml to read, so it cannot be checked, and this job {reason}"]
    return restricted_problems(f"{name} > {value}", action_file.read_text(encoding="utf-8").splitlines(), lambda _number: reason, root, depth + 1)


def restricted_problems(name: str, lines: list[str], reason_of, root: pathlib.Path, depth: int = 0) -> list[str]:
    """The cache and action problems of the lines that belong to a restricted job (`reason_of(line number)` is why, or None for a line that is not in one).

    A restricted job can run on the owner's PC, builds a program the owner trusts and runs with real rights, or holds secrets. Keeping the shared Actions cache out of it is done by
    what it may use, not by a list of spellings: only the actions of RESTRICTED_ACTIONS, no input with `cache` in its name that is not `false`, no `type=gha` and no cache variable
    in a command, `package-manager-cache: false` on setup-node, and a local action read and held to the same rules (red-team round 4, F10; QA round 4, minor 7).
    """
    found: list[str] = []
    cleaned = [strip_comment(raw) for raw in lines]
    cache_lines: set[int] = set()
    setup_node_steps: list[tuple[int, str, bool]] = []
    for index, line in enumerate(cleaned):
        reason = reason_of(index + 1)
        if reason is None or not line.strip():
            continue
        number = index + 1
        for match in CACHE_KEY.finditer(line):
            value = unquote(match.group("value").strip())
            if match.group("key").lower() in NON_ENABLING_CACHE_KEYS or value in ("", "false"):
                continue
            cache_lines.add(index)
            found.append(f"{name}:{number}: '{match.group('key')}: {value}' switches a cache on, but this job {reason}. The Actions cache is shared by every job of the repository, so an entry written by a job that ran attacker-chosen code could end up in a job that holds secrets or in a program the owner runs; restore every package from the lock files instead (local CI rounds 3 and 4)")
        if index not in cache_lines and CACHE_TEXT.search(line):
            cache_lines.add(index)
            found.append(f"{name}:{number}: this line reaches the Actions cache (type=gha or a cache service variable), and this job {reason}; restore every package from the lock files instead (local CI round 4)")
        if USES_VALUE_ELSEWHERE.search(line):
            continue
        for match in USES.finditer(line):
            value = unquote(match.group("value").strip())
            action = value.split("@", 1)[0].lower()
            if value.startswith("./"):
                found += local_action_problems(name, number, value, reason, root, depth)
            elif action not in RESTRICTED_ACTIONS:
                found.append(f"{name}:{number}: '{value}' is not an action this job may use: it {reason}, so it may use only {', '.join(sorted(RESTRICTED_ACTIONS))}. An action can use the shared Actions cache by itself (a docker build with cache-from type=gha, bundler-cache, gradle's setup action, a composite action), and a list of cache spellings cannot catch that (local CI round 4)")
            elif action == SETUP_NODE:
                setup_node_steps.append((index, reason, match.group(0).lstrip()[:1] in ("{", ",")))
    for index, reason, flow in setup_node_steps:
        step = step_range(cleaned, index, flow)
        if any(i in cache_lines for i in step):
            continue
        if not any(PACKAGE_MANAGER_CACHE_OFF.search(cleaned[i]) for i in step):
            found.append(f"{name}:{index + 1}: this actions/setup-node step must say 'package-manager-cache: false': setup-node switches a cache on by itself once a package.json names a packageManager, and this job {reason} (local CI round 4)")
    return found


def problems_in(name: str, text: str, root: pathlib.Path = ROOT) -> list[str]:
    found = []
    has_permissions = False
    with_secrets = any(USES_SECRETS.search(strip_comment(raw)) for raw in text.splitlines())
    runner_rule = SECRETS_RUNNER_FROM_VARIABLE if with_secrets else RUNNER_FROM_VARIABLE
    screen_job = screen_job_lines(text.splitlines())
    pc_lane = pc_lane_lines(text.splitlines())
    trusted_build = marker_job_lines(text.splitlines(), NO_CACHE_MARKERS)
    release_file = pathlib.PurePath(name).name in NO_CACHE_FILES

    def restriction(number: int) -> str | None:
        """Why the job that holds this line may use only the allowed actions and no cache (None for a job that is not held to it)."""
        if pc_lane.get(number, False):
            return "can run on the owner's PC (its runs-on comes from vars.LINUX_RUNNER)"
        if trusted_build.get(number, False):
            return "builds a program the owner trusts and runs with real rights (the release signer, the local CI program, the evaluator or the study capture tool)"
        if release_file:
            return f"is a job of {pathlib.PurePath(name).name}, whose jobs build what the owner runs"
        if with_secrets:
            return "belongs to a workflow that holds secrets"
        return None

    for number, raw in enumerate(text.splitlines(), start=1):
        line = strip_comment(raw)
        if not line.strip():
            continue
        if TOP_LEVEL_PERMISSIONS.match(line):
            has_permissions = True
        if FORBIDDEN_TRIGGER.search(line):
            found.append(f"{name}:{number}: the pull_request_target trigger is not allowed (it gives a pull request the repository's secrets)")
        runner = RUNS_ON.search(line)
        if runner:
            value = unquote(runner.group("value").strip())
            hosted = HOSTED_RUNNER_FROM_VARIABLE.match(value) is not None
            if not hosted and not runner_rule.match(value):
                if with_secrets:
                    found.append(f"{name}:{number}: this workflow uses secrets, so runs-on must be ${{{{ vars.SECRETS_LINUX_RUNNER || 'ubuntu-…' }}}} or ${{{{ vars.SECRETS_WINDOWS_RUNNER || 'windows-…' }}}} (never the shared build runner, D39), or ${{{{ vars.HOSTED_WINDOWS_RUNNER || 'windows-…' }}}} for a job that needs a real desktop")
                else:
                    found.append(f"{name}:{number}: runs-on must be ${{{{ vars.LINUX_RUNNER || 'ubuntu-…' }}}} or ${{{{ vars.WINDOWS_RUNNER || 'windows-…' }}}} so the job can run on the owner's runner (D39)")
            elif screen_job.get(number, False) and not hosted:
                found.append(f"{name}:{number}: this job runs screen tests, writes screenshots or publishes the release signer or the owner's local CI program, so runs-on must be ${{{{ vars.HOSTED_WINDOWS_RUNNER || 'windows-…' }}}} (a GitHub-hosted machine: the owner's PC has no real desktop for screen tests, a screenshot there could show the owner's screen, and the release signer must not be built on a machine that has run other jobs)")
            elif hosted and not screen_job.get(number, False):
                found.append(f"{name}:{number}: ${{{{ vars.HOSTED_WINDOWS_RUNNER || 'windows-…' }}}} is only for jobs that need a real desktop (screen tests, screenshots) or publish the release signer or the owner's local CI program; use the WINDOWS_RUNNER or SECRETS_ form for any other job")
        if USES_VALUE_ELSEWHERE.search(line):
            found.append(f"{name}:{number}: write the action after 'uses:' on the same line, so that its pin can be read")
            continue
        for match in USES.finditer(line):
            value = unquote(match.group("value").strip())
            if value.startswith("./"):
                continue
            if value.startswith("docker://"):
                if not DOCKER_PINNED.match(value):
                    found.append(f"{name}:{number}: '{value}' must name its image by digest (docker://image@sha256:<64 hex>)")
            elif not PINNED.match(value):
                found.append(f"{name}:{number}: '{value}' must be pinned to a full 40-character commit SHA (owner/repo@<sha> # vX.Y.Z)")
    if not has_permissions:
        found.append(f"{name}: no top-level 'permissions:' block (start with 'permissions:\\n  contents: read' and widen per job only where needed)")
    found += restricted_problems(name, text.splitlines(), restriction, root)
    return found


def workflow_files(targets: list[pathlib.Path]) -> list[pathlib.Path]:
    files = []
    for target in targets:
        if target.is_dir():
            files += sorted(p for p in target.iterdir() if p.suffix in (".yml", ".yaml") and p.is_file())
        elif target.is_file():
            files.append(target)
        else:
            raise FileNotFoundError(f"{target} does not exist")
    return files


SHA = "3d3c42e5aac5ba805825da76410c181273ba90b1"
GOOD = f"""name: Good
on:
  push:
    branches: [main]
permissions:
  contents: read
jobs:
  a:
    runs-on: ${{{{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}}}
    steps:
      - uses: actions/checkout@{SHA} # v7.0.1
      - uses: "actions/setup-node@{SHA}"
        with:
          package-manager-cache: false
      - run: echo "uses: not-a-step@v1 inside a string is ignored"   # pull_request_target in a comment is ignored
"""

# Every form of `uses:` the pin rule reads, in a job that is not held to the list of allowed actions (a Windows job of a workflow without secrets).
PINS = f"""name: Pins
on:
  push:
    branches: [main]
permissions:
  contents: read
jobs:
  a:
    runs-on: ${{{{ vars.WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/checkout@{SHA} # v7.0.1
      - uses: "actions/setup-node@{SHA}"
      - uses: ./.github/actions/local
      - uses: owner/repo/path/to/action@{SHA}
      - uses: docker://alpine@sha256:{'a' * 64}
      - run: echo "uses: not-a-step@v1 inside a string is ignored"   # pull_request_target in a comment is ignored
"""


FLOW = f"""name: Flow
on: [push]
permissions: {{contents: read}}
jobs: {{a: {{runs-on: ${{{{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}}}, steps: [{{uses: "actions/checkout@{SHA}"}}, {{run: make}}]}}}}
"""


TWO_JOBS = f"""name: Two
on: [push]
permissions:
  contents: read
jobs:
  pc:
    runs-on: ${{{{ vars.WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/checkout@{SHA}
      - run: python build/list-test-projects.py windows-pc
  screen:
    runs-on: ${{{{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}}}
    env:
      DBDP_SCREENSHOT_DIR: shots
    steps:
      - uses: actions/checkout@{SHA}
      - run: python build/list-test-projects.py windows-desktop
"""

SCREEN_RUNNER = "runs-on: ${{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}"
PC_RUNNER = "runs-on: ${{ vars.WINDOWS_RUNNER || 'windows-2025' }}"

SIGNER_JOBS = f"""name: Signer
on: [push]
permissions:
  contents: read
jobs:
  pc:
    runs-on: ${{{{ vars.WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/checkout@{SHA}
      - run: python build/list-test-projects.py windows-pc
  signer:
    runs-on: ${{{{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/checkout@{SHA}
      - run: dotnet publish tools/DbdPresence.ReleaseSigner/DbdPresence.ReleaseSigner.csproj -c Release -r win-x64 -o signer-publish/win-x64
      - uses: actions/upload-artifact@{SHA}
        with:
          name: release-signer-win-x64
          path: signer-publish/win-x64
"""

LOCAL_CI_JOBS = f"""name: LocalCi
on: [push]
permissions:
  contents: read
jobs:
  pc:
    runs-on: ${{{{ vars.WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/checkout@{SHA}
      - run: python build/list-test-projects.py windows-pc
  program:
    runs-on: ${{{{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/checkout@{SHA}
      - run: dotnet publish tools/DbdPresence.LocalCi/DbdPresence.LocalCi.csproj -c Release -r win-x64 -o local-ci-publish/win-x64
      - uses: actions/upload-artifact@{SHA}
        with:
          name: local-ci-win-x64
          path: local-ci-publish/win-x64
"""

OWNER_TOOL_JOBS = f"""name: OwnerTools
on: [push]
permissions:
  contents: read
jobs:
  screen:
    runs-on: ${{{{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/checkout@{SHA}
      - run: python build/list-test-projects.py windows-desktop
  tools:
    runs-on: ${{{{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/checkout@{SHA}
      - run: dotnet publish tools/DbdPresence.ReleaseSigner/DbdPresence.ReleaseSigner.csproj -c Release -r win-x64 -o signer-publish/win-x64
      - run: dotnet publish tools/DbdPresence.LocalCi/DbdPresence.LocalCi.csproj -c Release -r win-x64 -o local-ci-publish/win-x64
      - run: dotnet publish tools/DbdPresence.Eval/DbdPresence.Eval.csproj -c Release -r win-x64 -o eval-publish/win-x64
      - run: dotnet publish tools/DbdPresence.StudyCapture/DbdPresence.StudyCapture.csproj -c Release -r win-x64 -o study-publish/win-x64
"""

PC_AND_OTHER_CACHE = f"""name: Caches
on: [push]
permissions:
  contents: read
jobs:
  first:
    runs-on: ${{{{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}}}
    steps:
      - uses: actions/setup-dotnet@{SHA}
        with:
          cache: false
  second:
    runs-on: ${{{{ vars.WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/setup-node@{SHA}
        with:
          cache: npm
"""

SECRETS_AND_SCREEN = f"""name: Release
on: [push]
permissions:
  contents: read
jobs:
  build:
    runs-on: ${{{{ vars.SECRETS_WINDOWS_RUNNER || 'windows-2025' }}}}
    environment: release
    steps:
      - uses: actions/checkout@{SHA}
  screen:
    runs-on: ${{{{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/checkout@{SHA}
      - run: dotnet test --project tests/DbdPresence.UiTests
"""

# The shape of ci-windows.yml: a build job that can run on a Windows runner variable, and a screen-test job on the hosted form.
WINDOWS_FILE = f"""name: CI (Windows)
on: [push]
permissions:
  contents: read
jobs:
  windows:
    runs-on: ${{{{ vars.WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/checkout@{SHA}
      - uses: actions/setup-dotnet@{SHA}
        with:
          global-json-file: global.json
      - run: python build/list-test-projects.py windows-pc
  windows-desktop:
    runs-on: ${{{{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}}}
    steps:
      - uses: actions/checkout@{SHA}
      - run: python build/list-test-projects.py windows-desktop
"""

FLOW_SCREEN = f"""name: Flow
on: [push]
permissions: {{contents: read}}
jobs: {{a: {{runs-on: ${{{{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}}}, env: {{DBDP_SCREENSHOT_DIR: shots}}, steps: [{{uses: "actions/checkout@{SHA}"}}, {{run: make}}]}}}}
"""


PC_LANE = "${{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}"


def pc_job(extra: str, runner: str = PC_LANE) -> str:
    """A workflow with one job on the given runner that checks out, followed by the given steps (already indented as steps of that job)."""
    return f"""name: T
on: [push]
permissions:
  contents: read
jobs:
  a:
    runs-on: {runner}
    steps:
      - uses: actions/checkout@{SHA}
{extra}
"""


def node(with_lines: str = "") -> str:
    """A setup-node step; `with_lines` are the lines of its `with:` block (indented as such)."""
    return f"      - uses: actions/setup-node@{SHA}\n" + (f"        with:\n{with_lines}" if with_lines else "")


def make_local_actions(root: pathlib.Path) -> None:
    """The local actions the self-test cases use, under `root/.github/actions`."""

    def write(relative: str, text: str) -> None:
        path = root / ".github" / "actions" / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def composite(*steps: str) -> str:
        return "name: x\nruns:\n  using: composite\n  steps:\n" + "".join(f"    - {step}\n" for step in steps)

    write("clean/action.yml", composite(f"uses: actions/checkout@{SHA}", "run: echo hi\n      shell: bash"))
    write("cached/action.yml", composite(f"uses: actions/cache@{SHA}\n      with:\n        path: x\n        key: y"))
    write("nested/action.yml", composite("uses: ./.github/actions/cached"))
    write("yaml-extension/action.yaml", composite(f"uses: actions/cache@{SHA}"))
    write("node-bare/action.yml", composite(f"uses: actions/setup-node@{SHA}"))
    write("docker-step/action.yml", composite("uses: docker://alpine@sha256:" + "a" * 64))
    write("chain1/action.yml", composite("uses: ./.github/actions/chain2"))
    write("chain2/action.yml", composite("uses: ./.github/actions/chain3"))
    write("chain3/action.yml", composite("uses: ./.github/actions/chain4"))
    write("chain4/action.yml", composite(f"uses: actions/checkout@{SHA}"))


def self_test() -> int:
    """Checks the rules against fixed examples, so a check that silently stopped finding problems fails CI."""
    cases = [
        ("good workflow", GOOD, 0),
        ("tag pin", GOOD.replace(f"actions/checkout@{SHA}", "actions/checkout@v7"), 1),
        ("short SHA", GOOD.replace(f"actions/checkout@{SHA}", "actions/checkout@3d3c42e"), 1),
        ("every form of a pinned uses in a job that is not held to the list of allowed actions", PINS, 0),
        ("branch pin", PINS.replace(f"owner/repo/path/to/action@{SHA}", "owner/repo/path/to/action@main"), 1),
        ("unpinned docker image", PINS.replace(f"docker://alpine@sha256:{'a' * 64}", "docker://alpine:3"), 1),
        ("no top-level permissions", GOOD.replace("permissions:\n  contents: read\n", ""), 1),
        ("job-level permissions only", GOOD.replace("permissions:\n  contents: read\n", "").replace("    runs-on:", "    permissions:\n      contents: read\n    runs-on:"), 1),
        ("pull_request_target trigger", GOOD.replace("  push:\n", "  pull_request_target:\n"), 1),
        ("fixed hosted runner", GOOD.replace("runs-on: ${{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}", "runs-on: ubuntu-24.04"), 1),
        ("fixed self-hosted runner", GOOD.replace("runs-on: ${{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}", "runs-on: [self-hosted, linux]"), 1),
        ("secrets on the shared build runner", GOOD.replace('      - run: echo', '      - run: echo "${{ secrets.CLOUDFLARE_API_TOKEN }}"\n      - run: echo'), 1),
        ("secrets on their own runner variable", GOOD.replace('      - run: echo', '      - run: echo "${{ secrets.CLOUDFLARE_API_TOKEN }}"\n      - run: echo').replace("vars.LINUX_RUNNER", "vars.SECRETS_LINUX_RUNNER"), 0),
        ("a deployment environment counts as secrets", GOOD.replace("    steps:", "    environment: release\n    steps:"), 1),
        ("GITHUB_TOKEN alone is not a secret workflow", GOOD.replace('      - run: echo', '      - run: echo "${{ secrets.GITHUB_TOKEN }}"\n      - run: echo'), 0),
        ("pull_request_target in a list", GOOD.replace("on:\n  push:\n    branches: [main]\n", "on: [push, pull_request_target]\n"), 1),
        # Other spellings of the same rules (security review of automatic labelling, L7).
        ("quoted runs-on key with a fixed runner", GOOD.replace("runs-on: ${{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}", '"runs-on": ubuntu-24.04'), 1),
        ("single-quoted runs-on key with a fixed runner", GOOD.replace("runs-on: ${{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}", "'runs-on': [self-hosted]"), 1),
        ("quoted runs-on key with the variable", GOOD.replace("runs-on: ${{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}", "\"runs-on\": \"${{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}\""), 0),
        ("runs-on a list item", GOOD.replace("    runs-on: ${{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}\n", "    steps2:\n      - runs-on: self-hosted\n"), 1),
        ("flow-style jobs with a fixed runner", FLOW.replace("${{ vars.LINUX_RUNNER || 'ubuntu-24.04' }}", "ubuntu-24.04"), 1),
        ("flow-style jobs with the variable", FLOW, 0),
        ("flow-style jobs with an unpinned step", FLOW.replace(f"actions/checkout@{SHA}", "actions/checkout@v7"), 1),
        ("flow-style runner followed by more keys", FLOW.replace("}}, steps", "}}, timeout-minutes: 5, steps"), 0),
        ("secrets by index", GOOD.replace('      - run: echo', '      - run: echo "${{ secrets[\'CLOUDFLARE_API_TOKEN\'] }}"\n      - run: echo'), 1),
        ("secrets by index on their own runner variable", GOOD.replace('      - run: echo', '      - run: echo "${{ secrets[\'X\'] }}"\n      - run: echo').replace("vars.LINUX_RUNNER", "vars.SECRETS_LINUX_RUNNER"), 0),
        ("the whole secrets object", GOOD.replace('      - run: echo', '      - run: echo \'${{ toJSON(secrets) }}\'\n      - run: echo'), 1),
        ("the whole secrets object, spaced", GOOD.replace('      - run: echo', '      - run: echo \'${{ toJson( secrets ) }}\'\n      - run: echo'), 1),
        ("inherited secrets", GOOD.replace("    steps:", "    secrets: inherit\n    steps:"), 1),
        ("an OIDC token counts as secrets", GOOD.replace("  contents: read\n", "  contents: read\n  id-token: write\n"), 1),
        ("an OIDC token on their own runner variable", GOOD.replace("  contents: read\n", "  contents: read\n  id-token: write\n").replace("vars.LINUX_RUNNER", "vars.SECRETS_LINUX_RUNNER"), 0),
        ("id-token read is not secrets", GOOD.replace("  contents: read\n", "  contents: read\n  id-token: read\n"), 0),
        ("a quoted environment key counts as secrets", GOOD.replace("    steps:", '    "environment": release\n    steps:'), 1),
        ("a flow-style environment counts as secrets", FLOW.replace("steps:", "environment: release, steps:"), 1),
        ("quoted permissions key is accepted", GOOD.replace("permissions:\n", '"permissions":\n'), 0),
        ("uses inside a run string is ignored", GOOD.replace('      - run: echo', '      - run: echo "- uses: x/y@v1 and runs-on: ubuntu"\n      - run: echo'), 0),
        # Jobs that need a real desktop run on a GitHub-hosted machine only (security review 2026-10-30, H2 and M5).
        ("a PC job and a screen job, each on its own runner variable", TWO_JOBS, 0),
        ("a screen job on the PC runner variable", TWO_JOBS.replace(SCREEN_RUNNER, PC_RUNNER), 1),
        ("a screen job on a fixed hosted image", TWO_JOBS.replace(SCREEN_RUNNER, "runs-on: windows-2025"), 1),
        ("a screen job on a self-hosted label", TWO_JOBS.replace(SCREEN_RUNNER, "runs-on: [self-hosted, windows]"), 1),
        ("a job without screen tests on the hosted variable", TWO_JOBS.replace(PC_RUNNER, SCREEN_RUNNER), 1),
        ("the job after a screen job is not a screen job", TWO_JOBS.replace("  pc:", "  zzz:").replace("  screen:", "  pc:").replace("  zzz:", "  screen:"), 0),
        ("a screen marker in a comment is ignored", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", "      # DBDP_SCREENSHOT_DIR and DbdPresence.UiTests are not run here\n      - run: python build/list-test-projects.py windows-pc"), 0),
        ("every project in one job counts as screen tests", TWO_JOBS.replace("list-test-projects.py windows-pc", "list-test-projects.py windows"), 1),
        ("the UI test project in a PC job", TWO_JOBS.replace("python build/list-test-projects.py windows-pc", "dotnet test --project tests/DbdPresence.UiTests"), 1),
        ("the screenshot folder in a PC job", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", "      - run: echo $DBDP_SCREENSHOT_DIR"), 1),
        ("the Windows platform tests (they need a desktop) in a PC job", TWO_JOBS.replace("python build/list-test-projects.py windows-pc", "dotnet test --project tests/DbdPresence.Platform.Windows.Tests"), 1),
        ("the Windows platform tests in the hosted job", TWO_JOBS.replace("python build/list-test-projects.py windows-desktop", "dotnet test --project tests/DbdPresence.Platform.Windows.Tests"), 0),
        ("a hosted fallback that is not a hosted image", TWO_JOBS.replace("'windows-2025' }}\n    env:", "'self-hosted' }}\n    env:"), 1),
        ("a workflow-level screenshot folder makes every job a screen job", TWO_JOBS.replace("jobs:\n", "env:\n  DBDP_SCREENSHOT_DIR: shots\njobs:\n"), 1),
        ("a secrets job and a hosted screen job in one workflow", SECRETS_AND_SCREEN, 0),
        ("a screen job on the secrets runner variable", SECRETS_AND_SCREEN.replace(SCREEN_RUNNER, "runs-on: ${{ vars.SECRETS_WINDOWS_RUNNER || 'windows-2025' }}"), 1),
        ("a flow-style screen job on the hosted variable", FLOW_SCREEN, 0),
        ("a flow-style screen job on the PC runner variable", FLOW_SCREEN.replace("HOSTED_WINDOWS_RUNNER", "WINDOWS_RUNNER"), 1),
        ("a quoted runs-on key with the hosted variable in a screen job", TWO_JOBS.replace(SCREEN_RUNNER, '"runs-on": "${{ vars.HOSTED_WINDOWS_RUNNER || \'windows-2025\' }}"'), 0),
        # The release signer is built on a hosted machine only (local CI round 2, red-team F11).
        ("the release signer published in a hosted job", SIGNER_JOBS, 0),
        ("the release signer published in a job on the PC runner variable", SIGNER_JOBS.replace(SCREEN_RUNNER, PC_RUNNER), 1),
        ("the release signer published in a job on a fixed hosted image", SIGNER_JOBS.replace(SCREEN_RUNNER, "runs-on: windows-2025"), 1),
        ("the release signer published in a job on a self-hosted label", SIGNER_JOBS.replace(SCREEN_RUNNER, "runs-on: [self-hosted, windows]"), 1),
        ("the release signer published in a job on the secrets runner variable", SIGNER_JOBS.replace(SCREEN_RUNNER, "runs-on: ${{ vars.SECRETS_WINDOWS_RUNNER || 'windows-2025' }}"), 1),
        ("only the signer's upload step in a PC job", SIGNER_JOBS.replace("tools/DbdPresence.ReleaseSigner/DbdPresence.ReleaseSigner.csproj", "x.csproj").replace(SCREEN_RUNNER, PC_RUNNER), 1),
        ("the signer folder name in a PC job", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", "      - run: ls signer-publish/win-x64"), 1),
        ("a signer mention in a comment is ignored", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", "      # tools/DbdPresence.ReleaseSigner is published in the other job\n      - run: python build/list-test-projects.py windows-pc"), 0),
        ("a signer-like name that is another tool is not the signer", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", "      - run: dotnet publish tools/DbdPresence.ReleaseSignerHelper2/x.csproj"), 0),
        ("a workflow-level signer mention makes every job a hosted job", TWO_JOBS.replace("jobs:\n", "env:\n  SIGNER: tools/DbdPresence.ReleaseSigner\njobs:\n"), 1),
        ("the signer in a flow-style job on the hosted variable", FLOW_SCREEN.replace("env: {DBDP_SCREENSHOT_DIR: shots}", "env: {SIGNER: tools/DbdPresence.ReleaseSigner}"), 0),
        ("the signer in a flow-style job on the PC runner variable", FLOW_SCREEN.replace("env: {DBDP_SCREENSHOT_DIR: shots}", "env: {SIGNER: tools/DbdPresence.ReleaseSigner}").replace("HOSTED_WINDOWS_RUNNER", "WINDOWS_RUNNER"), 1),
        ("a PC job before the signer job is not made a hosted job by it", SIGNER_JOBS.replace("  signer:", "  zzz:").replace("  pc:", "  first:").replace("  zzz:", "  signer:"), 0),
        ("the signer in a secrets workflow, in a hosted job", SECRETS_AND_SCREEN.replace("dotnet test --project tests/DbdPresence.UiTests", "dotnet publish tools/DbdPresence.ReleaseSigner/x.csproj"), 0),
        # The owner's local CI program is published on a hosted machine only (local CI round 3, red-team M6).
        ("the local CI program published in a hosted job", LOCAL_CI_JOBS, 0),
        ("the local CI program published in a job on the PC runner variable", LOCAL_CI_JOBS.replace(SCREEN_RUNNER, PC_RUNNER), 1),
        ("the local CI program published in a job on a fixed hosted image", LOCAL_CI_JOBS.replace(SCREEN_RUNNER, "runs-on: windows-2025"), 1),
        ("the local CI program published in a job on the secrets runner variable", LOCAL_CI_JOBS.replace(SCREEN_RUNNER, "runs-on: ${{ vars.SECRETS_WINDOWS_RUNNER || 'windows-2025' }}"), 1),
        ("only the local CI program's upload step in a PC job", LOCAL_CI_JOBS.replace("tools/DbdPresence.LocalCi/DbdPresence.LocalCi.csproj", "x.csproj").replace(SCREEN_RUNNER, PC_RUNNER), 1),
        ("a local CI mention in a comment is ignored", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", "      # tools/DbdPresence.LocalCi is published in the other job\n      - run: python build/list-test-projects.py windows-pc"), 0),
        ("the local CI tests are not the local CI program", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", "      - run: dotnet test --project tests/DbdPresence.LocalCi.Tests"), 0),
        # No cache in a job that can run on the owner's PC (local CI round 3, red-team L7).
        ("a cache input in a PC-lane job", GOOD.replace("      - uses: actions/checkout", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n      - uses: actions/checkout", 1), 1),
        ("a named package cache in a PC-lane job", GOOD.replace("      - uses: actions/checkout", "      - uses: actions/setup-node@" + SHA + "\n        with:\n          cache: npm\n      - uses: actions/checkout", 1), 1),
        ("a quoted cache input in a PC-lane job", GOOD.replace("      - uses: actions/checkout", "      - uses: actions/setup-node@" + SHA + "\n        with:\n          \"cache\": 'pip'\n      - uses: actions/checkout", 1), 1),
        ("cache false in a PC-lane job is fine", GOOD.replace("      - uses: actions/checkout", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: false\n      - uses: actions/checkout", 1), 0),
        ("the cache action in a PC-lane job", GOOD.replace("      - uses: actions/checkout", "      - uses: actions/cache@" + SHA + "\n        with:\n          path: x\n      - uses: actions/checkout", 1), 1),
        ("the cache restore action in a PC-lane job", GOOD.replace("      - uses: actions/checkout", "      - uses: actions/cache/restore@" + SHA + "\n      - uses: actions/checkout", 1), 1),
        ("a cache input in a flow-style PC-lane job", FLOW.replace("{run: make}", "{uses: \"actions/setup-node@" + SHA + "\", with: {cache: npm}}"), 1),
        ("a cache in a comment is ignored", GOOD.replace("      - uses: actions/checkout", "      # cache: true would be wrong here\n      - uses: actions/checkout", 1), 0),
        ("a cache in a hosted-only job is fine", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-desktop", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n      - run: python build/list-test-projects.py windows-desktop"), 0),
        ("a cache in a secrets job is refused: it is the job a poisoned entry would be restored into (round 4)", GOOD.replace('      - run: echo', '      - run: echo "${{ secrets.CLOUDFLARE_API_TOKEN }}"\n      - uses: actions/setup-node@' + SHA + '\n        with:\n          cache: npm\n      - run: echo').replace("vars.LINUX_RUNNER", "vars.SECRETS_LINUX_RUNNER"), 1),
        ("a cache in the second job when only the first job runs on the Linux lane", PC_AND_OTHER_CACHE, 0),
        ("a cache in the first job, which runs on the Linux lane", PC_AND_OTHER_CACHE.replace("cache: false", "cache: true"), 1),
        ("a cache named like a path input is not a cache", GOOD.replace("      - uses: actions/checkout", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache-dependency-path: '**/packages.lock.json'\n      - uses: actions/checkout", 1), 0),
        # No cache in a job that builds a program the owner trusts and runs with real rights, nor in release.yml (commit review of the cache rule).
        ("the signer built in a hosted job with a cache input", SIGNER_JOBS.replace("      - run: dotnet publish tools/DbdPresence.ReleaseSigner", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n      - run: dotnet publish tools/DbdPresence.ReleaseSigner"), 1),
        ("the signer built in a hosted job with the cache action", SIGNER_JOBS.replace("      - run: dotnet publish tools/DbdPresence.ReleaseSigner", "      - uses: actions/cache@" + SHA + "\n        with:\n          path: x\n      - run: dotnet publish tools/DbdPresence.ReleaseSigner"), 1),
        ("the signer built in a hosted job without any cache", SIGNER_JOBS, 0),
        ("the local CI program built in a hosted job with a cache input", LOCAL_CI_JOBS.replace("      - run: dotnet publish tools/DbdPresence.LocalCi", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n      - run: dotnet publish tools/DbdPresence.LocalCi"), 1),
        ("the local CI program built in a hosted job without any cache", LOCAL_CI_JOBS, 0),
        ("the evaluator built in a hosted job with a cache input", OWNER_TOOL_JOBS.replace("      - run: dotnet publish tools/DbdPresence.Eval", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n      - run: dotnet publish tools/DbdPresence.Eval"), 1),
        ("the study capture tool built in a hosted job with a cache input", OWNER_TOOL_JOBS.replace("      - run: dotnet publish tools/DbdPresence.StudyCapture", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: npm\n      - run: dotnet publish tools/DbdPresence.StudyCapture"), 1),
        ("the owner's tools built in a hosted job without any cache", OWNER_TOOL_JOBS, 0),
        ("a cache in the screen-test job next to a cache-free tools job", OWNER_TOOL_JOBS.replace("  screen:\n    runs-on: ${{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}\n    steps:\n      - uses: actions/checkout@" + SHA + "\n", "  screen:\n    runs-on: ${{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}\n    env:\n      DBDP_SCREENSHOT_DIR: shots\n    steps:\n      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n"), 0),
        ("a cache in the same job as the tools build is refused whatever its place", OWNER_TOOL_JOBS.replace("  tools:\n    runs-on: ${{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}\n    steps:\n      - uses: actions/checkout@" + SHA + "\n", "  tools:\n    runs-on: ${{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}\n    steps:\n      - uses: actions/checkout@" + SHA + "\n      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n"), 1),
        ("the cache of a trusted-build job in a flow-style job", FLOW_SCREEN.replace("env: {DBDP_SCREENSHOT_DIR: shots}", "env: {SIGNER: tools/DbdPresence.ReleaseSigner}").replace("{run: make}", "{uses: \"actions/setup-dotnet@" + SHA + "\", with: {cache: true}}"), 1),
        ("a workflow-level tool marker makes every job a no-cache job", OWNER_TOOL_JOBS.replace("jobs:\n", "env:\n  SIGNER: tools/DbdPresence.ReleaseSigner\njobs:\n").replace("  screen:\n    runs-on: ${{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}\n    steps:\n      - uses: actions/checkout@" + SHA + "\n", "  screen:\n    runs-on: ${{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}\n    env:\n      DBDP_SCREENSHOT_DIR: shots\n    steps:\n      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n"), 1),
        ("a tools mention in a comment does not make a job a no-cache job", GOOD.replace("      - uses: actions/checkout", "      # tools/DbdPresence.ReleaseSigner is built elsewhere\n      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: false\n      - uses: actions/checkout", 1), 0),
        ("any job of release.yml with a cache input", SECRETS_AND_SCREEN.replace("      - uses: actions/checkout@" + SHA + "\n  screen:", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n  screen:", 1), 1, "release.yml"),
        ("any job of release.yml with the cache action", SECRETS_AND_SCREEN.replace("      - uses: actions/checkout@" + SHA + "\n  screen:", "      - uses: actions/cache@" + SHA + "\n  screen:", 1), 1, "release.yml"),
        ("release.yml without any cache", SECRETS_AND_SCREEN, 0, "release.yml"),
        ("the same cache in a secrets file that is not release.yml is refused too (round 4)", SECRETS_AND_SCREEN.replace("      - uses: actions/checkout@" + SHA + "\n  screen:", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n  screen:", 1), 1, "deploy.yml"),
        # No job of ci-windows.yml uses a cache, whatever it builds (red-team round 4, F4): its windows job publishes the development installer, the portable build and the spike tool the owner runs.
        ("a cache in the windows job of ci-windows.yml", WINDOWS_FILE.replace("      - run: python build/list-test-projects.py windows-pc", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n      - run: python build/list-test-projects.py windows-pc"), 1, "ci-windows.yml"),
        ("a cache in the screen-test job of ci-windows.yml", WINDOWS_FILE.replace("      - run: python build/list-test-projects.py windows-desktop", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: npm\n      - run: python build/list-test-projects.py windows-desktop"), 1, "ci-windows.yml"),
        ("the cache action in ci-windows.yml", WINDOWS_FILE.replace("      - run: python build/list-test-projects.py windows-pc", "      - uses: actions/cache@" + SHA + "\n        with:\n          path: x\n      - run: python build/list-test-projects.py windows-pc"), 1, "ci-windows.yml"),
        ("cache false in ci-windows.yml is fine", WINDOWS_FILE.replace("      - run: python build/list-test-projects.py windows-pc", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: false\n      - run: python build/list-test-projects.py windows-pc"), 0, "ci-windows.yml"),
        ("ci-windows.yml without any cache", WINDOWS_FILE, 0, "ci-windows.yml"),
        ("the same cache in a file that is not ci-windows.yml", WINDOWS_FILE.replace("      - run: python build/list-test-projects.py windows-pc", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n      - run: python build/list-test-projects.py windows-pc"), 0, "other.yml"),
        ("a path-qualified ci-windows.yml is still that file", WINDOWS_FILE.replace("      - run: python build/list-test-projects.py windows-pc", "      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n      - run: python build/list-test-projects.py windows-pc"), 1, ".github/workflows/ci-windows.yml"),
        # Only the allowed actions in a job that can run on the owner's PC, builds what the owner runs or holds secrets, and every cache out of it by what it may use, not by spelling
        # (red-team round 4, F10; QA round 4, minor 7: the cache action in upper case, a docker build with cache-from type=gha, bundler-cache, package-manager-cache, gradle, a local action).
        ("the cache action written in upper case in a PC-lane job", pc_job(f"      - uses: Actions/Cache@{SHA}\n        with:\n          path: x\n          key: y"), 1),
        ("an allowed action written in upper case is still allowed", pc_job(f"      - uses: Actions/Setup-Dotnet@{SHA}"), 0),
        ("a docker build with the GitHub cache backend in a PC-lane job", pc_job(f"      - uses: docker/build-push-action@{SHA}\n        with:\n          cache-from: type=gha\n          cache-to: type=gha,mode=max"), 3),
        ("the buildx cache backend in a command of a PC-lane job", pc_job("      - run: docker buildx build --cache-to type=gha ."), 1),
        ("the cache service address in a PC-lane job", pc_job("      - run: echo $ACTIONS_CACHE_URL"), 1),
        ("the runtime token in a PC-lane job", pc_job("      - run: echo $ACTIONS_RUNTIME_TOKEN"), 1),
        ("the results service address in a PC-lane job", pc_job("      - run: echo $ACTIONS_RESULTS_URL"), 1),
        ("the cache service address in a job that is not held to the list", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", "      - run: echo $ACTIONS_CACHE_URL"), 0),
        ("bundler-cache on the ruby setup action in a PC-lane job", pc_job(f"      - uses: ruby/setup-ruby@{SHA}\n        with:\n          bundler-cache: true"), 2),
        ("gradle's setup action caches by default and is not an allowed action", pc_job(f"      - uses: gradle/actions/setup-gradle@{SHA}"), 1),
        ("a path below an allowed action is not that action", pc_job(f"      - uses: actions/checkout/sub@{SHA}"), 1),
        ("a docker action in a PC-lane job", pc_job("      - uses: docker://alpine@sha256:" + "a" * 64), 1),
        ("an upper-case cache key in a PC-lane job", pc_job(f"      - uses: actions/setup-dotnet@{SHA}\n        with:\n          Cache: npm"), 1),
        ("a cache input with other inputs beside it in flow style", pc_job(f"      - {{uses: actions/setup-node@{SHA}, with: {{node-version: 22, cache: npm, package-manager-cache: false}}}}"), 1),
        ("enable-cache in a PC-lane job", pc_job(f"      - uses: actions/setup-dotnet@{SHA}\n        with:\n          enable-cache: true"), 1),
        ("the glob a cache would read is not a cache", pc_job(f"      - uses: actions/setup-dotnet@{SHA}\n        with:\n          cache-dependency-glob: '**/packages.lock.json'"), 0),
        ("setup-node with package-manager-cache true in a PC-lane job", pc_job(node("          package-manager-cache: true\n")), 1),
        ("setup-node without package-manager-cache in a PC-lane job", pc_job(node("          node-version: 22\n")), 1),
        ("setup-node without any inputs in a PC-lane job", pc_job(node()), 1),
        ("setup-node with package-manager-cache false in a PC-lane job", pc_job(node("          package-manager-cache: false\n")), 0),
        ("setup-node with the key and the value quoted", pc_job(node("          \"package-manager-cache\": 'false'\n")), 0),
        ("setup-node with package-manager-cache false among other inputs", pc_job(node("          node-version: 22\n          package-manager-cache: false\n          registry-url: https://registry.npmjs.org\n")), 0),
        ("setup-node in flow style with package-manager-cache false", pc_job(f"      - {{uses: actions/setup-node@{SHA}, with: {{node-version: 22, package-manager-cache: false}}}}"), 0),
        ("setup-node in flow style without it", pc_job(f"      - {{uses: actions/setup-node@{SHA}, with: {{node-version: 22}}}}"), 1),
        ("setup-node whose uses comes after its name", pc_job(f"      - name: Node\n        uses: actions/setup-node@{SHA}\n        with:\n          package-manager-cache: false"), 0),
        ("setup-node whose uses comes after two other keys and has nothing", pc_job(f"      - id: node\n        name: Node\n        uses: actions/setup-node@{SHA}\n        with:\n          node-version: 22"), 1),
        ("two setup-node steps, only the first with package-manager-cache false", pc_job(node("          package-manager-cache: false\n") + node("          node-version: 22\n")), 1),
        ("two setup-node steps, only the second with package-manager-cache false", pc_job(node("          node-version: 22\n") + node("          package-manager-cache: false\n")), 1),
        ("two setup-node steps, both with package-manager-cache false", pc_job(node("          package-manager-cache: false\n") + node("          package-manager-cache: false\n")), 0),
        ("package-manager-cache false in the step after setup-node is not setup-node's", pc_job(node("          node-version: 22\n") + f"      - uses: actions/setup-dotnet@{SHA}\n        with:\n          package-manager-cache: false"), 1),
        ("a step after the last setup-node that is not a step of the job's list does not count", pc_job(node("          node-version: 22\n") + "    timeout-minutes: 5\n    env:\n      package-manager-cache: false"), 1),
        ("setup-node in a job that is not held to the list needs nothing", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", f"      - uses: actions/setup-node@{SHA}\n      - run: python build/list-test-projects.py windows-pc"), 0),
        ("an action outside the list in a job that builds the local CI program", LOCAL_CI_JOBS.replace("      - run: dotnet publish tools/DbdPresence.LocalCi", f"      - uses: softprops/action-gh-release@{SHA}\n      - run: dotnet publish tools/DbdPresence.LocalCi"), 1),
        ("an action outside the list in ci-windows.yml", WINDOWS_FILE.replace("      - run: python build/list-test-projects.py windows-desktop", f"      - uses: softprops/action-gh-release@{SHA}\n      - run: python build/list-test-projects.py windows-desktop"), 1, "ci-windows.yml"),
        ("an action outside the list in a workflow that holds secrets", SECRETS_AND_SCREEN.replace("      - uses: actions/checkout@" + SHA + "\n  screen:", f"      - uses: cloudflare/wrangler-action@{SHA}\n  screen:", 1), 1, "deploy.yml"),
        ("an action outside the list in a job that is not held to the list", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", f"      - uses: softprops/action-gh-release@{SHA}\n      - run: python build/list-test-projects.py windows-pc"), 0),
        # `uses:` with its value on a later line cannot be read, so it is refused in every job.
        ("the action on the line after uses: in a PC-lane job", pc_job(f"      - uses:\n          actions/cache@{SHA}"), 1),
        ("the action on the line after uses: in a job that is not held to the list", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", f"      - uses:\n          actions/cache@{SHA}"), 1),
        ("a block scalar after uses:", pc_job(f"      - uses: >-\n          actions/cache@{SHA}"), 1),
        ("a quoted uses key with its value on the next line", pc_job(f"      - \"uses\":\n          actions/cache@{SHA}"), 1),
        # The end of the jobs block: a top-level key after it is not part of the last job, and it counts for every job.
        ("a screenshot folder in a top-level key after the jobs makes every job a screen job", TWO_JOBS + "env:\n  DBDP_SCREENSHOT_DIR: shots\n", 1),
        ("a screenshot folder in a one-line top-level key right after the jobs", TWO_JOBS + "env: {DBDP_SCREENSHOT_DIR: shots}\n", 1),
        ("a signer mention in a top-level key after the jobs makes every job a hosted job", TWO_JOBS + "env:\n  SIGNER: tools/DbdPresence.ReleaseSigner\n", 1),
        ("a tool marker in a top-level key after the jobs makes every job a no-cache job", OWNER_TOOL_JOBS.replace("  screen:\n    runs-on: ${{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}\n    steps:\n      - uses: actions/checkout@" + SHA + "\n", "  screen:\n    runs-on: ${{ vars.HOSTED_WINDOWS_RUNNER || 'windows-2025' }}\n    env:\n      DBDP_SCREENSHOT_DIR: shots\n    steps:\n      - uses: actions/setup-dotnet@" + SHA + "\n        with:\n          cache: true\n") + "env:\n  SIGNER: tools/DbdPresence.ReleaseSigner\n", 1),
        ("a cache-looking line in a top-level key after a PC-lane job is not part of that job", GOOD + "defaults:\n  cache: npm\n", 0),
        ("a cache-looking line in a one-line top-level key after a PC-lane job is not part of that job", GOOD + "defaults: {cache: npm}\n", 0),
        # Local actions in a restricted job are read and held to the same rules (the files are made in a temporary folder).
        ("setup-node whose with block comes before its uses", pc_job(f"      - with:\n          package-manager-cache: false\n        uses: actions/setup-node@{SHA}"), 0),
        ("setup-node whose with block lies between its name and its uses", pc_job(f"      - name: Node\n        with:\n          package-manager-cache: false\n        uses: actions/setup-node@{SHA}"), 0),
        ("setup-node with a blank line between its with block and its uses", pc_job(f"      - with:\n          package-manager-cache: false\n\n        uses: actions/setup-node@{SHA}"), 0),
        ("package-manager-cache false two steps later is not setup-node's", pc_job(node("          node-version: 22\n") + f"      - uses: actions/setup-dotnet@{SHA}\n        with:\n          package-manager-cache: false\n      - uses: actions/checkout@{SHA}"), 1),
        ("an upper-case key that only names a file a cache would read", pc_job(f"      - uses: actions/setup-dotnet@{SHA}\n        with:\n          Cache-Dependency-Path: x"), 0),
        # What the messages say: the 1-based line of the problem, the reason and the thing that is wrong.
        ("the message of a cache input names its line, key, value and the reason", pc_job(f"      - uses: actions/setup-dotnet@{SHA}\n        with:\n          cache: npm"), 1, "self-test.yml", ["self-test.yml:12: 'cache: npm' switches a cache on", "can run on the owner's PC"]),
        ("the message of an action outside the list names its line and the list", pc_job(f"      - uses: gradle/actions/setup-gradle@{SHA}"), 1, "self-test.yml", ["self-test.yml:10: 'gradle/actions/setup-gradle@", "is not an action this job may use", "actions/checkout, actions/setup-dotnet, actions/setup-node, actions/upload-artifact"]),
        ("the message of a cache variable names its line", pc_job("      - run: echo $ACTIONS_CACHE_URL"), 1, "self-test.yml", ["self-test.yml:10: this line reaches the Actions cache"]),
        ("the message of a setup-node step names the line of its uses", pc_job(node("          node-version: 22\n")), 1, "self-test.yml", ["self-test.yml:10: this actions/setup-node step must say 'package-manager-cache: false'"]),
        ("the message of a uses value on a later line names its line", pc_job(f"      - uses:\n          actions/cache@{SHA}"), 1, "self-test.yml", ["self-test.yml:10: write the action after 'uses:' on the same line"]),
        ("a workflow with secrets says why its jobs are held to the list", SECRETS_AND_SCREEN.replace("      - uses: actions/checkout@" + SHA + "\n  screen:", f"      - uses: cloudflare/wrangler-action@{SHA}\n  screen:", 1), 1, "deploy.yml", ["belongs to a workflow that holds secrets"]),
        ("ci-windows.yml says why its jobs are held to the list", WINDOWS_FILE.replace("      - run: python build/list-test-projects.py windows-desktop", f"      - uses: softprops/action-gh-release@{SHA}\n      - run: python build/list-test-projects.py windows-desktop"), 1, "ci-windows.yml", ["is a job of ci-windows.yml"]),
        ("a job that builds the local CI program says why it is held to the list", LOCAL_CI_JOBS.replace("      - run: dotnet publish tools/DbdPresence.LocalCi", f"      - uses: softprops/action-gh-release@{SHA}\n      - run: dotnet publish tools/DbdPresence.LocalCi"), 1, "self-test.yml", ["builds a program the owner trusts and runs with real rights"]),
        ("a local action's problems are named with the action", pc_job("      - uses: ./.github/actions/cached"), 1, "self-test.yml", ["self-test.yml > ./.github/actions/cached:", "'actions/cache@"]),
        ("the chain of local actions is cut at the third level and says which action it would not read", pc_job("      - uses: ./.github/actions/chain1"), 1, "self-test.yml", ["the local action ./.github/actions/chain4 is nested 3 levels deep or more"]),
        ("a clean local action in a PC-lane job", pc_job("      - uses: ./.github/actions/clean"), 0),
        ("a local action that uses the cache action", pc_job("      - uses: ./.github/actions/cached"), 1),
        ("a local action that calls another local action that caches", pc_job("      - uses: ./.github/actions/nested"), 1),
        ("a local action in a file named action.yaml that caches", pc_job("      - uses: ./.github/actions/yaml-extension"), 1),
        ("a local action with a setup-node step that does not turn the cache off", pc_job("      - uses: ./.github/actions/node-bare"), 1),
        ("a local action with a docker step", pc_job("      - uses: ./.github/actions/docker-step"), 1),
        ("a local action that does not exist", pc_job("      - uses: ./.github/actions/missing"), 1),
        ("a local action outside the repository", pc_job("      - uses: ./../outside"), 1),
        ("a chain of local actions that is too deep to be read", pc_job("      - uses: ./.github/actions/chain1"), 1),
        ("a local action in a job that is not held to the list is not read", TWO_JOBS.replace("      - run: python build/list-test-projects.py windows-pc", "      - uses: ./.github/actions/cached\n      - run: python build/list-test-projects.py windows-pc"), 0),
    ]
    failed = []
    with tempfile.TemporaryDirectory() as temporary:
        root = pathlib.Path(temporary)
        make_local_actions(root)
        for case in cases:
            name, text, expected = case[:3]
            filename = case[3] if len(case) > 3 and case[3] else "self-test.yml"
            problems = problems_in(filename, text, root)
            if len(problems) != expected:
                failed.append(f"the '{name}' case found {len(problems)} problem(s), expected {expected}")
            elif len(case) > 4 and not all(any(said in problem for problem in problems) for said in case[4]):
                failed.append(f"the '{name}' case did not say {case[4]!r} in {problems!r}")
    for line in failed:
        print("ERROR self-test: " + line, file=sys.stderr)
    if not failed:
        print(f"check-workflows self-test: {len(cases)} cases passed")
    return 1 if failed else 0


def main() -> int:
    if "--self-test" in sys.argv[1:]:
        return self_test()
    targets = [pathlib.Path(a).resolve() for a in sys.argv[1:]] or [WORKFLOWS]
    files = workflow_files(targets)
    if not files:
        print("ERROR no workflow files found", file=sys.stderr)
        return 1
    problems = [p for f in files for p in problems_in(f.relative_to(ROOT).as_posix() if f.is_relative_to(ROOT) else str(f), f.read_text(encoding="utf-8"))]
    for line in problems:
        print("ERROR " + line, file=sys.stderr)
    if not problems:
        print(f"check-workflows: {len(files)} workflow files pass (SHA pins, top-level permissions, no pull_request_target, runners from variables, screen jobs and the release signer on hosted machines)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
