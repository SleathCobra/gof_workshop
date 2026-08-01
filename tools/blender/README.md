# Blender helper

`gof2_workshop` is an optional MIT-licensed Blender add-on. Install its folder
through Blender's add-on preferences. It displays glTF metadata written by the
Workshop and validates selected geometry against the current PC AEM v4/v5
limits. The Workshop import path does not require this add-on.

For repeatable headless validation:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.1\blender.exe' --background --python scripts/blender/validate_roundtrip.py -- input.gltf output.gltf report.json
```
