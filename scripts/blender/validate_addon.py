"""Load and register the optional Workshop Blender add-on in a clean session."""

import importlib.util
import json
import pathlib
import sys

import bpy


def main():
    marker = sys.argv.index("--") if "--" in sys.argv else -1
    values = sys.argv[marker + 1 :] if marker >= 0 else []
    if len(values) != 2:
        raise RuntimeError("Expected addon/__init__.py report.json")
    source = pathlib.Path(values[0]).resolve()
    report_path = pathlib.Path(values[1]).resolve()
    spec = importlib.util.spec_from_file_location("gof2_workshop", source)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.register()
    registered = (
        hasattr(bpy.ops.gof2_workshop, "validate_selection")
        and hasattr(bpy.ops.gof2_workshop, "export_for_workshop")
    )
    report = {
        "blenderVersion": bpy.app.version_string,
        "registered": registered,
        "classCount": len(module.CLASSES),
        "operators": [value.bl_idname for value in module.CLASSES if hasattr(value, "bl_idname")],
    }
    module.unregister()
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print("GOF2_WORKSHOP_ADDON_REPORT=" + json.dumps(report, sort_keys=True))


if __name__ == "__main__":
    main()
