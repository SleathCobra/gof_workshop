# macOS renderer compatibility

The prior macOS failure was consistent with desktop core-profile requirements: the renderer did not originally own/bind a VAO, assumed the Windows ANGLE shader profile, and diagnostic overlay draws could leave the vertex attribute layout pointing at the line buffer.

Implemented corrections:

- create and bind a VAO for desktop core contexts;
- restore the mesh vertex layout before every mesh draw without re-uploading geometry;
- keep element-buffer unbinding within a valid VAO lifetime;
- select GLSL ES 3.00, desktop GLSL 1.50 for OpenGL 3.2, or legacy desktop GLSL 1.20 explicitly;
- record context profile, shading-language version and selected dialect in renderer diagnostics;
- preserve controlled software fallback when initialization, shaders or capabilities fail.

Windows ANGLE/OpenGL ES remains validated. No physical Intel or Apple Silicon Mac is attached to this engineering environment, so the portable path compiles and packages but is **not claimed as real-Mac validated**. The renderer diagnostics panel is intended to capture vendor, renderer, GL/GLSL versions, profile, maximum texture size, shader logs and fallback reason on the next Mac run.

macOS OpenGL remains deprecated by Apple. If this corrected 3.2-core path still fails on a supported target, the next decision is a focused Metal backend behind the existing scene renderer abstraction, not changes to parsers or the neutral scene model.
