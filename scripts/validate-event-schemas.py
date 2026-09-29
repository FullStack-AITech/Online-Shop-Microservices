"""Validate event examples (or captured messages) against docs/events/schemas.

    python scripts/validate-event-schemas.py              # every committed example
    python scripts/validate-event-schemas.py msg.json ... # specific files

A file is matched to its schema by name: `order-created.v1.example.json` is checked
against `order-created.v1.schema.json`; any other file name is matched on its
`eventType` and `eventVersion`. Exits 1 if anything is invalid, so CI can run it.

Needs `jsonschema>=4.18` (which brings `referencing`).
"""

import json
import pathlib
import re
import sys

from jsonschema import Draft202012Validator
from referencing import Registry, Resource

ROOT = pathlib.Path(__file__).resolve().parents[1] / "docs" / "events"


def kebab(name: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "-", name).lower()


def main(argv: list[str]) -> int:
    schemas = {
        path.name: json.loads(path.read_text("utf-8"))
        for path in (ROOT / "schemas").glob("*.schema.json")
    }
    registry = Registry().with_resources(
        (schema["$id"], Resource.from_contents(schema)) for schema in schemas.values()
    )
    targets = [pathlib.Path(arg) for arg in argv] or sorted((ROOT / "examples").glob("*.json"))

    problems = 0
    for path in targets:
        document = json.loads(path.read_text("utf-8"))
        if path.name.endswith(".example.json"):
            schema_name = path.name.replace(".example.json", ".schema.json")
        else:
            schema_name = (
                f"{kebab(document.get('eventType', ''))}.v{document.get('eventVersion')}.schema.json"
            )
        schema = schemas.get(schema_name)
        if schema is None:
            problems += 1
            print(f"FAIL {path.name}: no schema named {schema_name}")
            continue

        validator = Draft202012Validator(
            schema, registry=registry, format_checker=Draft202012Validator.FORMAT_CHECKER
        )
        errors = sorted(validator.iter_errors(document), key=lambda error: error.json_path)
        for error in errors:
            print(f"FAIL {path.name}: {error.json_path}: {error.message}")
        problems += len(errors)
        if not errors:
            print(f"ok   {path.name}")

    print("all valid" if not problems else f"{problems} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
