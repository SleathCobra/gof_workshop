"""Headless Blender validation for Workshop glTF/AEM round trips.

This script is independently authored for the MIT-licensed Workshop. It only
operates on paths explicitly passed after Blender's ``--`` separator.
"""

import json
import pathlib
import sys

import bpy


def arguments():
    marker = sys.argv.index("--") if "--" in sys.argv else -1
    values = sys.argv[marker + 1 :] if marker >= 0 else []
    if len(values) != 3:
        raise RuntimeError("Expected input.gltf output.gltf report.json")
    return tuple(pathlib.Path(value).resolve() for value in values)


def main():
    source, destination, report_path = arguments()
    destination.parent.mkdir(parents=True, exist_ok=True)
    report_path.parent.mkdir(parents=True, exist_ok=True)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    result = bpy.ops.import_scene.gltf(filepath=str(source))
    if "FINISHED" not in result:
        raise RuntimeError(f"Blender glTF import failed: {result}")

    meshes = [value for value in bpy.data.objects if value.type == "MESH"]
    if not meshes:
        raise RuntimeError("Imported glTF contains no Blender mesh objects")

    animated = next(
        (value for value in meshes if value.animation_data and value.animation_data.action),
        meshes[0],
    )
    scene = bpy.context.scene
    frame = max(2, int(scene.frame_end))
    scene.frame_set(frame)
    original_x = float(animated.location.x)
    animated.location.x = original_x + 0.25
    inserted = animated.keyframe_insert(data_path="location", index=0, frame=frame)
    if not inserted:
        raise RuntimeError("Blender could not insert the validation location key")

    bpy.ops.export_scene.gltf(
        filepath=str(destination),
        export_format="GLTF_SEPARATE",
        export_animations=True,
        export_extras=True,
        export_materials="EXPORT",
    )

    report = {
        "blenderVersion": bpy.app.version_string,
        "source": source.name,
        "output": destination.name,
        "meshCount": len(meshes),
        "materialCount": len(bpy.data.materials),
        "imageCount": len(bpy.data.images),
        "actionCount": len(bpy.data.actions),
        "modifiedObject": animated.name,
        "modifiedFrame": frame,
        "originalLocationX": original_x,
        "modifiedLocationX": float(animated.location.x),
        "stableSubmeshIds": [value.get("stableSubmeshId", "") for value in meshes],
        "workshopDocumentIds": [value.get("workshopDocumentId", "") for value in meshes],
        "outputExists": destination.exists(),
    }
    report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print("GOF2_WORKSHOP_BLENDER_REPORT=" + json.dumps(report, sort_keys=True))


if __name__ == "__main__":
    main()
