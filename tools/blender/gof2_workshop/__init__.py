"""Galaxy on Fire 2 Workshop Blender metadata and validation helper (MIT)."""

import bpy
from bpy.props import EnumProperty, StringProperty


bl_info = {
    "name": "Galaxy on Fire 2 Workshop",
    "author": "Galaxy on Fire 2 Workshop contributors",
    "version": (0, 1, 0),
    "blender": (4, 3, 0),
    "location": "Properties > Object > GOF2 Workshop",
    "description": "Displays Workshop glTF metadata and validates AEM target constraints",
    "category": "Import-Export",
}


class GOF2WORKSHOP_OT_validate(bpy.types.Operator):
    bl_idname = "gof2_workshop.validate_selection"
    bl_label = "Validate Selected for AEM"
    bl_description = "Check selected meshes against current PC AEM v4/v5 constraints"

    def execute(self, context):
        failures = []
        for obj in context.selected_objects:
            if obj.type != "MESH":
                failures.append(f"{obj.name}: not a mesh")
                continue
            vertices = len(obj.data.vertices)
            loops = len(obj.data.loops)
            if vertices == 0 or vertices > 65535:
                failures.append(f"{obj.name}: {vertices} vertices exceeds the 1..65535 range")
            if loops == 0:
                failures.append(f"{obj.name}: no triangle loops")
            if obj.data.uv_layers.active is None:
                failures.append(f"{obj.name}: no active UV set")
            if obj.find_armature() is not None:
                failures.append(f"{obj.name}: armature skinning is not representable in AEM v4/v5")
            if obj.data.shape_keys is not None:
                failures.append(f"{obj.name}: shape keys are not representable in AEM v4/v5")
            if any(len(polygon.vertices) != 3 for polygon in obj.data.polygons):
                failures.append(f"{obj.name}: mesh contains non-triangle polygons")
        if failures:
            self.report({"WARNING"}, "; ".join(failures[:4]))
        else:
            self.report({"INFO"}, "Selection satisfies the basic Workshop AEM constraints")
        return {"FINISHED"}


class GOF2WORKSHOP_OT_export(bpy.types.Operator):
    bl_idname = "gof2_workshop.export_for_workshop"
    bl_label = "Export for Workshop"
    bl_description = "Export selected objects as a metadata-preserving glTF for Workshop reimport"

    filepath: StringProperty(subtype="FILE_PATH", default="workshop_reimport.gltf")
    mode: EnumProperty(
        name="Content",
        items=(
            ("ANIMATION", "Animation reimport", "Export selected animated objects for animation-only reimport"),
            ("GEOMETRY", "Geometry and animation", "Export selected geometry, materials, and animation"),
            ("MATERIALS", "Materials", "Export selected geometry and materials without animation"),
        ),
        default="GEOMETRY",
    )

    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}

    def execute(self, context):
        if not context.selected_objects:
            self.report({"ERROR"}, "Select at least one Workshop mesh")
            return {"CANCELLED"}
        bpy.ops.export_scene.gltf(
            filepath=self.filepath,
            export_format="GLTF_SEPARATE",
            use_selection=True,
            export_extras=True,
            export_animations=self.mode != "MATERIALS",
            export_materials="NONE" if self.mode == "ANIMATION" else "EXPORT",
        )
        self.report({"INFO"}, f"Exported {self.mode.lower()} reimport package")
        return {"FINISHED"}


class GOF2WORKSHOP_PT_object(bpy.types.Panel):
    bl_label = "GOF2 Workshop"
    bl_idname = "GOF2WORKSHOP_PT_object"
    bl_space_type = "PROPERTIES"
    bl_region_type = "WINDOW"
    bl_context = "object"

    def draw(self, context):
        layout = self.layout
        obj = context.object
        if obj is None:
            layout.label(text="Select a Workshop object")
            return
        layout.label(text=f"Stable ID: {obj.get('stableSubmeshId', 'not present')}")
        layout.label(text=f"Document ID: {obj.get('workshopDocumentId', 'not present')}")
        layout.label(text=f"Source submesh: {obj.get('sourceSubmeshIndex', 'not present')}")
        layout.label(text=f"Source pivot: {obj.get('sourcePivot', 'not present')}")
        layout.operator(GOF2WORKSHOP_OT_validate.bl_idname)
        layout.operator(GOF2WORKSHOP_OT_export.bl_idname)


CLASSES = (GOF2WORKSHOP_OT_validate, GOF2WORKSHOP_OT_export, GOF2WORKSHOP_PT_object)


def register():
    for value in CLASSES:
        bpy.utils.register_class(value)


def unregister():
    for value in reversed(CLASSES):
        bpy.utils.unregister_class(value)
