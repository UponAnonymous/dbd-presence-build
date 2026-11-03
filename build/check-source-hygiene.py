#!/usr/bin/env python3
"""QUALITY_GATES U6/U7 for product code (src/**/*.cs, src/**/*.xaml) and, since local CI round 4 (QA minor 6), for the owner's local CI program, its tests and the
architecture tests (DEFAULT_TARGETS: the program runs with administrator rights, so it is held to the same rules as the product). Folders named on the command line replace that list.

* no placeholders: TODO/FIXME/HACK/PLACEHOLDER (any case), NotImplementedException,
  NotSupportedException("not implemented"/"not yet ...");
* no swallowed exceptions: a catch body that is empty or only `return;`, `continue;`, `break;` or `_ = x;`
  (comments are ignored). A deliberate swallow needs `// hygiene: allow-swallow <reason>` inside the body;
* no empty event handlers `(object? sender, ...EventArgs e) { }`;
* no raw exception text in a log call (TM-12): `Log.*(...)`, `_log.*(...)`, `logger.*(...)` and `LogXxx(logger, ...)` statements may not pass
  `x.Message` or `x.ToString()` unless the statement also uses `LogRedaction` (use `LogRedaction.Current.Describe(ex)` or `.Redact(text)`).

Comments and string literals are blanked before matching, so text inside them cannot hide or fake code.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
# What is checked when no folder is named: the product, and the owner's local CI program with the tests that guard it.
DEFAULT_TARGETS = ("src", "tools/DbdPresence.LocalCi", "tests/DbdPresence.LocalCi.Tests", "tests/DbdPresence.ArchitectureTests")
PLACEHOLDER = re.compile(r"\b(TODO|FIXME|HACK|PLACEHOLDER)\b", re.IGNORECASE)
NOT_IMPLEMENTED = re.compile(r"\bNotImplementedException\b")
NOT_SUPPORTED_PLACEHOLDER = re.compile(r'NotSupportedException\s*\(\s*"[^"]*(not implemented|not yet)', re.IGNORECASE)
EMPTY_HANDLER = re.compile(r"\(\s*object\??\s+\w+\s*,\s*[\w.]*EventArgs\s+\w+\s*\)\s*\{\s*\}")
TRIVIAL_BODY = re.compile(r"^\s*(return\s*;|continue\s*;|break\s*;|_\s*=\s*\w+\s*;)?\s*$")
ALLOW_SWALLOW = "hygiene: allow-swallow"
# The review page (a generated HTML form) uses the HTML attribute name "placeholder" (lower case) for its text boxes; that is not a placeholder marker.
HTML_PLACEHOLDER_FILES = {"src/DbdPresence.Study/ReviewPage.cs"}
LOG_CALL = re.compile(r"(?:\b(?:Log|_log|_logger|logger)\s*\.\s*(?:Verbose|Debug|Information|Warning|Error|Fatal|Write|Log\w*)\s*\(|\bLog[A-Z]\w*\s*\(\s*_?logger\s*,)")
RAW_EXCEPTION_TEXT = re.compile(r"\b\w+(?:\.\w+)*\s*\.\s*(?:Message|ToString\s*\(\s*\))")


def blank_code(text: str) -> str:
    """Replaces comments and string/char literals with spaces (newlines kept) so offsets still match."""
    out = list(text)
    i, n = 0, len(text)

    def blank(start: int, end: int) -> None:
        for k in range(start, min(end, n)):
            if out[k] != "\n":
                out[k] = " "

    while i < n:
        c = text[i]
        two = text[i:i + 2]
        if two == "//":
            end = text.find("\n", i)
            end = n if end < 0 else end
            blank(i, end)
            i = end
        elif two == "/*":
            end = text.find("*/", i + 2)
            end = n if end < 0 else end + 2
            blank(i, end)
            i = end
        elif text.startswith('"""', i):
            end = text.find('"""', i + 3)
            end = n if end < 0 else end + 3
            blank(i, end)
            i = end
        elif c == '"' or two in ('@"', '$"') or text[i:i + 3] in ('$@"', '@$"'):
            start = i
            verbatim = "@" in text[i:i + 3].split('"')[0]
            i = text.index('"', i) + 1
            while i < n:
                if verbatim and text.startswith('""', i):
                    i += 2
                    continue
                if not verbatim and text[i] == "\\":
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            blank(start, i)
        elif c == "'":
            end = i + 1
            while end < n and text[end] != "'":
                end += 2 if text[end] == "\\" else 1
            blank(i, end + 1)
            i = end + 1
        else:
            i += 1
    return "".join(out)


def matching_brace(code: str, open_index: int) -> int:
    depth = 0
    for k in range(open_index, len(code)):
        if code[k] == "{":
            depth += 1
        elif code[k] == "}":
            depth -= 1
            if depth == 0:
                return k
    return -1


def line_of(text: str, index: int) -> int:
    return text.count("\n", 0, index) + 1


def check_file(path: pathlib.Path) -> list[str]:
    raw = path.read_text(encoding="utf-8")
    rel = path.relative_to(ROOT).as_posix()
    problems = []
    for lineno, line in enumerate(raw.splitlines(), 1):
        if any(not (rel in HTML_PLACEHOLDER_FILES and m.group(0) == "placeholder") for m in PLACEHOLDER.finditer(line)):
            problems.append(f"{rel}:{lineno}: placeholder marker: {line.strip()}")
        if NOT_SUPPORTED_PLACEHOLDER.search(line):
            problems.append(f"{rel}:{lineno}: NotSupportedException used as a placeholder")
    if path.suffix != ".cs":
        return problems

    code = blank_code(raw)
    for match in NOT_IMPLEMENTED.finditer(code):
        problems.append(f"{rel}:{line_of(code, match.start())}: NotImplementedException")
    for match in EMPTY_HANDLER.finditer(code):
        problems.append(f"{rel}:{line_of(code, match.start())}: empty event handler")
    for match in re.finditer(r"\bcatch\b", code):
        k = match.end()
        brace = code.find("{", k)
        header = code[k:brace]
        if brace < 0 or not re.fullmatch(r"\s*(\([^{}]*\))?\s*(when\s*\(.*\))?\s*", header, re.S):
            continue
        end = matching_brace(code, brace)
        body_code = code[brace + 1:end]
        body_raw = raw[brace + 1:end]
        if TRIVIAL_BODY.match(body_code) and ALLOW_SWALLOW not in body_raw:
            problems.append(f"{rel}:{line_of(code, match.start())}: swallowed exception (catch body does nothing)")
    for match in LOG_CALL.finditer(code):
        end = code.find(";", match.end())
        statement = code[match.start():end if end >= 0 else len(code)]
        if RAW_EXCEPTION_TEXT.search(statement) and "LogRedaction" not in statement:
            problems.append(f"{rel}:{line_of(code, match.start())}: raw exception text in a log call (wrap in LogRedaction, TM-12)")
    return problems


def main() -> int:
    targets = [ROOT / a for a in sys.argv[1:]] or [ROOT / t for t in DEFAULT_TARGETS]
    files = [f for t in targets for f in ([t] if t.is_file() else sorted(t.rglob("*"))) if f.suffix in (".cs", ".xaml")
             and "/obj/" not in f.as_posix() and "/bin/" not in f.as_posix()]
    problems = [p for f in files for p in check_file(f)]
    if problems:
        print("Source hygiene problems (QUALITY_GATES U6/U7):")
        print("\n".join(f"  {p}" for p in problems))
        return 1
    print(f"Source hygiene OK ({len(files)} files).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
