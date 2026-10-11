#!/usr/bin/env python3
"""Validate compatibility evidence and produce release notes without network or third-party packages."""
import argparse
import calendar
import datetime as dt
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def check_schema(value, schema, path="$", errors=None):
    """Validate the subset used by our closed JSON schema. Unknown schema keywords fail, not silently pass."""
    if errors is None:
        errors = []
    allowed = {"$schema", "type", "const", "enum", "pattern", "format", "properties", "required", "additionalProperties", "items", "minItems", "uniqueItems", "minLength", "minimum"}
    if set(schema) - allowed:
        errors.append(f"{path}: unsupported schema keywords {set(schema) - allowed}")
    types = {"null": value is None, "object": isinstance(value, dict), "array": isinstance(value, list), "string": isinstance(value, str), "integer": type(value) is int}
    wanted = schema.get("type")
    if wanted and not any(types.get(t, False) for t in (wanted if isinstance(wanted, list) else [wanted])):
        errors.append(f"{path}: expected {wanted}")
        return errors
    if "const" in schema and value != schema["const"]: errors.append(f"{path}: expected {schema['const']}")
    if "enum" in schema and value not in schema["enum"]: errors.append(f"{path}: not in enum")
    if isinstance(value, str):
        if "pattern" in schema and not re.search(schema["pattern"], value): errors.append(f"{path}: invalid format")
        if len(value) < schema.get("minLength", 0): errors.append(f"{path}: empty string")
        if schema.get("format") == "date":
            try: dt.date.fromisoformat(value)
            except ValueError: errors.append(f"{path}: invalid date")
    if type(value) is int and value < schema.get("minimum", value): errors.append(f"{path}: below minimum")
    if isinstance(value, dict):
        for key in schema.get("required", []):
            if key not in value: errors.append(f"{path}: missing {key}")
        properties = schema.get("properties", {})
        for key, item in value.items():
            if key in properties: check_schema(item, properties[key], f"{path}.{key}", errors)
            elif schema.get("additionalProperties") is False: errors.append(f"{path}: unexpected {key}")
    if isinstance(value, list):
        if len(value) < schema.get("minItems", 0): errors.append(f"{path}: too few items")
        if schema.get("uniqueItems") and len({json.dumps(item, sort_keys=True) for item in value}) != len(value): errors.append(f"{path}: duplicate items")
        for index, item in enumerate(value): check_schema(item, schema.get("items", {}), f"{path}[{index}]", errors)
    return errors


def six_months(date):
    month = date.month + 6
    year = date.year + (month - 1) // 12
    month = (month - 1) % 12 + 1
    return date.replace(year=year, month=month, day=min(date.day, calendar.monthrange(year, month)[1]))


def validate(matrix, schema, today):
    errors = check_schema(matrix, schema)
    if errors: raise ValueError("\n".join(errors))
    def valid_version(value):
        return len(value) <= 64 and re.fullmatch(schema["properties"]["recommended"]["pattern"], value) and all(
            int(part) <= 2_147_483_647 for part in re.split(r"[-+]", value, maxsplit=1)[0].split("."))
    for name in ("minimum", "recommended", "latest"):
        if matrix[name] is not None and not valid_version(matrix[name]):
            raise ValueError(name + " must be SemVer within the server parser bounds")
    for library in matrix["libraries"]:
        if library["version"] != "legacy" and not valid_version(library["version"]):
            raise ValueError("Library release version must be SemVer or legacy")
        if library.get("minServer") is not None and not valid_version(library["minServer"]):
            raise ValueError("Library minServer must be SemVer")
    for rules in (matrix["deprecated"], matrix["blocked"]):
        for rule in rules:
            if not rule["range"].strip() or len(rule["range"]) > 256:
                raise ValueError("Invalid library version range")
            for token in (token for token in rule["range"].split(" ") if token):
                match = re.fullmatch(r"(<=|>=|<|>|=)?(.+)", token)
                if match is None or not valid_version(match.group(2)):
                    raise ValueError("Invalid library version range token: " + token)
    for rule in matrix["deprecated"]:
        if not valid_version(rule["replacement"]):
            raise ValueError("Replacement library version must be SemVer within the server parser bounds")
        sunset = dt.date.fromisoformat(rule["sunset"])
        if sunset < dt.date.fromisoformat(rule["announced"]) + dt.timedelta(days=90) or sunset < six_months(dt.date.fromisoformat(rule["successorReleased"])):
            raise ValueError("Deprecation requires 90 days notice and six months after the successor ships")
        successor = next((library for library in matrix["libraries"] if library["version"] == rule["replacement"] and library.get("released")), None)
        if successor and successor["released"] != rule["successorReleased"]:
            raise ValueError("Deprecation successor date must match the recorded library release")
    if matrix["minimum"]:
        rule = next((r for r in matrix["deprecated"] if r["range"] == "<" + matrix["minimum"]), None)
        if rule is None or dt.date.fromisoformat(rule["sunset"]) > today:
            raise ValueError("Raising the library minimum requires a completed deprecation entry for <minimum")


def render(matrix, server, commit, allow_ci_only):
    rows = [r for r in matrix["tested"] if r["server"] == server and r["serverCommit"].lower() == commit.lower()]
    recommended = matrix["recommended"] or "legacy"
    passed = [r for r in rows if r["library"] == recommended and r["result"] == "pass"]
    if not passed and not (allow_ci_only and any(r["result"] == "ci-only" for r in rows)):
        raise ValueError("No editor-verified pass for the recommended library on this exact commit; record evidence, or explicitly --allow-ci-only with a ci-only evidence row")
    lines = ["## Tested with", "", "| Library | s&box revision | Engine | Database | Result | Evidence |", "| --- | --- | --- | --- | --- | --- |"]
    for row in rows:
        escape = lambda text: str(text).replace("|", "\\|").replace("\n", " ")
        cells = [row["library"], row["revision"] or "unknown", row["engine"], ", ".join(row["database"]), row["result"], ", ".join(row["evidence"])]
        lines.append("| " + " | ".join(escape(c) for c in cells) + " |")
    if not passed: lines += ["", "**CI-only release: this commit has not passed the real s&box editor gate.**"]
    return "\n".join(lines)


def rollback_notes(paths):
    lines = ["", "## Rollback compatibility"]
    if not paths:
        return "\n".join(lines + ["No preserved-data rollback target certified for this release. Older pre-compatibility binaries require backup restore."])
    for path in paths:
        report = json.loads(path.read_text())
        if report.get("protocol") != 1 or report.get("Compatible") is not True or not report.get("version"):
            raise ValueError("Rollback reports must be successful target-binary compatibility --json results")
        floor = report.get("MinimumBinarySchema")
        if type(floor) is not int or floor > report["SupportedVersion"] or report["DatabaseVersion"] < report["SupportedVersion"]:
            raise ValueError("Rollback target has no valid certified schema floor")
        lines.append(f"- Preserved-data rollback target: **{report['version']}**; database schema {report['DatabaseVersion']}, target supports {report['SupportedVersion']}, certified floor {floor}.")
    lines.append("Use `rollback --keep-data`; it rechecks the actual target and every instance before changing the binary. These reports certify schema/config compatibility, not a game-editor test.")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--matrix", type=Path, default=ROOT / "compat/library-matrix.json")
    parser.add_argument("--server")
    parser.add_argument("--commit")
    parser.add_argument("--allow-ci-only", action="store_true")
    parser.add_argument("--baseline", type=Path, help="Previous release's matrix; verifies its announced sunsets are not shortened")
    parser.add_argument("--policy-source", type=Path, help="Tested source matrix; a detached artifact may change only tested evidence")
    parser.add_argument("--rollback-report", type=Path, action="append", default=[], help="Target binary compatibility --json report against the release schema/config")
    args = parser.parse_args()
    try:
        matrix = json.loads(args.matrix.read_text())
        schema = json.loads((ROOT / "compat/library-matrix.schema.json").read_text())
        validate(matrix, schema, dt.date.today())
        if args.policy_source:
            source = json.loads(args.policy_source.read_text())
            if {key: value for key, value in source.items() if key != "tested"} != {key: value for key, value in matrix.items() if key != "tested"}:
                raise ValueError("Detached release evidence cannot change the tested source policy")
        if args.baseline:
            baseline = json.loads(args.baseline.read_text())
            for old in baseline["deprecated"]:
                current = next((r for r in matrix["deprecated"] if r["range"] == old["range"]), None)
                if not current or dt.date.fromisoformat(current["sunset"]) < dt.date.fromisoformat(old["sunset"]):
                    raise ValueError("An announced deprecation must remain in the matrix and cannot be shortened")
        if bool(args.server) != bool(args.commit): raise ValueError("--server and --commit must be supplied together")
        print(render(matrix, args.server, args.commit, args.allow_ci_only) + rollback_notes(args.rollback_report) if args.server else "Compatibility matrix valid; no release evidence claimed.")
    except (ValueError, KeyError, OSError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
