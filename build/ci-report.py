#!/usr/bin/env python3
"""CI helpers (stdlib only).

  ci-report.py coverage <dir>     Summarise every cobertura file under <dir> (Markdown to stdout).
  ci-report.py vulnerable <json>  Fail on any vulnerable package (any severity at/above moderate is an
                                  error; low is a warning) and on a malformed or failed scan.
"""
import json
import pathlib
import sys
import xml.etree.ElementTree as ET


def coverage(directory: str) -> int:
    files = sorted(pathlib.Path(directory).rglob("coverage*.xml"))
    if not files:
        print("No coverage files found.")
        return 1
    print("| Report | Line coverage | Branch coverage |\n|---|---|---|")
    for path in files:
        root = ET.parse(path).getroot()
        line = float(root.get("line-rate", 0)) * 100
        branch = float(root.get("branch-rate", 0)) * 100
        print(f"| {path.parent.name} | {line:.1f}% | {branch:.1f}% |")
    return 0


def vulnerable(report: str) -> int:
    try:
        data = json.loads(pathlib.Path(report).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        print(f"::error::Vulnerability scan output unreadable: {error}")
        return 1
    if data.get("problems"):
        for problem in data["problems"]:
            print(f"::error::Vulnerability scan problem: {problem}")
        return 1
    projects = data.get("projects")
    if not projects:
        print("::error::Vulnerability scan returned no projects (feed unreachable?)")
        return 1
    status = 0
    for project in projects:
        for framework in project.get("frameworks", []):
            for package in framework.get("topLevelPackages", []) + framework.get("transitivePackages", []):
                for vulnerability in package.get("vulnerabilities", []):
                    severity = vulnerability.get("severity", "Unknown")
                    level = "warning" if severity.lower() == "low" else "error"
                    print(f"::{level}::{project['path']}: {package['id']} {package.get('resolvedVersion')} {severity} {vulnerability.get('advisoryurl')}")
                    if level == "error":
                        status = 1
    if status == 0:
        print(f"No moderate/high/critical vulnerabilities in {len(projects)} projects.")
    return status


if __name__ == "__main__":
    if len(sys.argv) != 3 or sys.argv[1] not in ("coverage", "vulnerable"):
        print(__doc__)
        sys.exit(2)
    sys.exit(coverage(sys.argv[2]) if sys.argv[1] == "coverage" else vulnerable(sys.argv[2]))
