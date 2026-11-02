"""Coverage per module and the ratchet that keeps it from sliding (CI, Windows job).

Reads every coverage.cobertura.xml under the given folders (coverlet, both test suites), merges them line by line -
a line counts as covered when any suite ran it - and groups the lines by module: the source folder under src/.
Presentation is shared source compiled into both UIs, so its lines come from both apps' assemblies and are counted
once per file and line. Generated files (obj/, *.g.cs) are left out.

Fails when a module's line coverage is more than the allowed drop below its floor in baseline.json; says when a
module has risen far enough that its floor should be raised (the ratchet only moves up, by a commit).

    python tools/coverage/coverage_ratchet.py TestResults/tests TestResults/presentation [--summary FILE]
"""
import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
BASELINE = HERE / "baseline.json"
MODULES = ("Core", "Presentation", "App", "UI", "Desktop")
MODULE_PATTERN = re.compile(r"(?:^|/)(?:src/)?Hearthpulse\.(Core|Presentation|App|UI|Desktop)/")
CONDITION = re.compile(r"\((\d+)/(\d+)\)")


def locate(path):
    """(module, path from the module folder on) of a source path; None for generated and foreign files."""
    path = path.replace("\\", "/")
    if "/obj/" in path or path.endswith(".g.cs"):
        return None
    match = MODULE_PATTERN.search(path)
    return (match.group(1), path[match.start(1) - len("Hearthpulse."):]) if match else None


def read_report(file, lines, branches):
    """Adds one report's lines: lines[(module, file, number)] = hit; branches[...] = (covered, total)."""
    root = ET.parse(file).getroot()
    sources = [(s.text or "").replace("\\", "/").rstrip("/") for s in root.iter("source")]
    for cls in root.iter("class"):
        name = cls.get("filename", "")
        found = locate(name) or next((f for f in (locate(s + "/" + name) for s in sources) if f), None)
        if found is None:
            continue
        module, key_file = found
        for line in cls.iter("line"):
            key = (module, key_file, int(line.get("number")))
            lines[key] = lines.get(key, False) or int(line.get("hits", "0")) > 0
            if line.get("branch") == "True":
                condition = CONDITION.search(line.get("condition-coverage", ""))
                if condition:
                    covered, total = int(condition.group(1)), int(condition.group(2))
                    old = branches.get(key, (0, total))
                    branches[key] = (max(old[0], covered), max(old[1], total))


def summarize(folders):
    lines, branches = {}, {}
    reports = sorted({p for folder in folders for p in Path(folder).rglob("coverage.cobertura.xml")})
    for report in reports:
        read_report(report, lines, branches)
    result = {}
    for module in MODULES:
        hit = [v for (m, _, _), v in lines.items() if m == module]
        cond = [v for (m, _, _), v in branches.items() if m == module]
        if not hit:
            continue
        covered_branches, total_branches = sum(c for c, _ in cond), sum(t for _, t in cond)
        result[module] = {
            "lines": len(hit),
            "line_rate": 100.0 * sum(hit) / len(hit),
            "branch_rate": 100.0 * covered_branches / total_branches if total_branches else None,
        }
    return result, len(reports)


def check(result, baseline):
    """Rows for the table and the list of failures."""
    allowed = baseline.get("allowed_drop", 0.5)
    floors, targets = baseline.get("floors", {}), baseline.get("targets", {})
    rows, failures = [], []
    for module, data in result.items():
        floor, target, rate = floors.get(module), targets.get(module), data["line_rate"]
        if floor is None:
            verdict = "no floor"
        elif rate < floor - allowed:
            verdict = f"**below the floor by {floor - rate:.1f} pp**"
            failures.append(f"{module}: {rate:.1f} % < floor {floor:.1f} % - {allowed} pp")
        elif rate > floor + allowed:
            verdict = f"raise the floor to {rate:.1f}"
        else:
            verdict = "ok"
        rows.append((module, data, floor, target, verdict))
    for module in floors:
        if module not in result:
            failures.append(f"{module}: no coverage data")
    return rows, failures


def table(rows, reports):
    out = ["### Code coverage by module (Windows, both suites merged)", "",
           "| Module | Lines | Line coverage | Branches | Floor | Target | |", "|---|---:|---:|---:|---:|---:|---|"]
    for module, data, floor, target, verdict in rows:
        branch = "" if data["branch_rate"] is None else f"{data['branch_rate']:.1f} %"
        out.append(f"| {module} | {data['lines']} | {data['line_rate']:.1f} % | {branch} | "
                   f"{'' if floor is None else f'{floor:.1f} %'} | {'' if target is None else f'{target:.0f} %'} | {verdict} |")
    out += ["", f"{reports} report file(s). Floors and targets: tools/coverage/baseline.json."]
    return out


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("folders", nargs="+")
    parser.add_argument("--baseline", default=str(BASELINE))
    parser.add_argument("--summary", help="append the table to this file (GITHUB_STEP_SUMMARY)")
    args = parser.parse_args(argv)
    result, reports = summarize(args.folders)
    baseline = json.loads(Path(args.baseline).read_text(encoding="utf-8"))
    rows, failures = check(result, baseline)
    text = "\n".join(table(rows, reports))
    print(text)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as summary:
            summary.write(text + "\n")
    if reports == 0:
        failures.append("no coverage.cobertura.xml found")
    for failure in failures:
        print(f"::error title=Coverage ratchet::{failure}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
