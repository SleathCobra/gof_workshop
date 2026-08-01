const state = {
  canvas: null,
  gl: null,
  program: null,
  lineProgram: null,
  meshes: [],
  scene: null,
  texture: null,
  textureSize: [0, 0],
  selected: -1,
  visible: false,
  lost: false,
  options: {
    lit: true,
    wireframe: false,
    bounds: true,
    pivots: true,
    cull: false,
    orthographic: false,
    linear: true,
    isolate: false,
  },
  camera: { target: [0, 0, 0], yaw: -0.65, pitch: 0.35, distance: 8 },
  animation: { playing: false, time: 0, startedAt: 0, startTime: 0 },
  frame: 0,
  frameRequested: false,
  lastFrameMs: 0,
  drawCalls: 0,
  pick: null,
  pointer: null,
  contextLosses: 0,
};

const vertexSource = `#version 300 es
precision highp float;
layout(location=0) in vec3 aPosition;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUv;
uniform mat4 uModel;
uniform mat4 uViewProjection;
out vec3 vNormal;
out vec2 vUv;
void main() {
  gl_Position = uViewProjection * uModel * vec4(aPosition, 1.0);
  vNormal = mat3(uModel) * aNormal;
  vUv = aUv;
}`;

const fragmentSource = `#version 300 es
precision highp float;
in vec3 vNormal;
in vec2 vUv;
uniform vec4 uColor;
uniform vec4 uPickColor;
uniform sampler2D uTexture;
uniform int uMode;
uniform bool uHasTexture;
uniform bool uLit;
uniform bool uSelected;
out vec4 outColor;
void main() {
  if (uMode == 2) { outColor = uPickColor; return; }
  vec4 color = uHasTexture ? texture(uTexture, vUv) : uColor;
  if (uLit) {
    vec3 normal = normalize(vNormal);
    float diffuse = max(dot(normal, normalize(vec3(0.35, 0.75, 0.55))), 0.0);
    color.rgb *= 0.28 + diffuse * 0.72;
  }
  if (uSelected) color.rgb = mix(color.rgb, vec3(1.0, 0.52, 0.05), 0.42);
  outColor = color;
}`;

const lineVertexSource = `#version 300 es
precision highp float;
layout(location=0) in vec3 aPosition;
uniform mat4 uModel;
uniform mat4 uViewProjection;
uniform float uPointSize;
void main() {
  gl_Position = uViewProjection * uModel * vec4(aPosition, 1.0);
  gl_PointSize = uPointSize;
}`;

const lineFragmentSource = `#version 300 es
precision highp float;
uniform vec4 uColor;
out vec4 outColor;
void main() { outColor = uColor; }`;

function createCanvas() {
  let canvas = document.getElementById('workshop-webgl-viewport');
  if (!canvas) {
    canvas = document.createElement('canvas');
    canvas.id = 'workshop-webgl-viewport';
    canvas.setAttribute('aria-label', 'Realtime WebGL model viewport');
    Object.assign(canvas.style, {
      position: 'fixed',
      display: 'none',
      zIndex: '20',
      background: '#0e1116',
      border: '1px solid #333842',
      touchAction: 'none',
      outline: 'none',
    });
    canvas.tabIndex = 0;
    document.body.appendChild(canvas);
  }
  state.canvas = canvas;
  installInput(canvas);
  resizeCanvas();
  return canvas;
}

function resizeCanvas() {
  if (!state.canvas) return;
  const left = Math.min(296, Math.max(0, window.innerWidth - 320));
  const top = Math.min(132, Math.max(0, window.innerHeight - 180));
  const bottom = 78;
  const width = Math.max(64, window.innerWidth - left - 12);
  const height = Math.max(64, window.innerHeight - top - bottom);
  Object.assign(state.canvas.style, {
    left: `${left}px`, top: `${top}px`, width: `${width}px`, height: `${height}px`,
  });
  const scale = Math.min(window.devicePixelRatio || 1, 2);
  const pixelWidth = Math.max(1, Math.floor(width * scale));
  const pixelHeight = Math.max(1, Math.floor(height * scale));
  if (state.canvas.width !== pixelWidth || state.canvas.height !== pixelHeight) {
    state.canvas.width = pixelWidth;
    state.canvas.height = pixelHeight;
    disposePickTarget();
    requestFrame();
  }
}

function installInput(canvas) {
  if (canvas.dataset.workshopInput === 'installed') return;
  canvas.dataset.workshopInput = 'installed';
  window.addEventListener('resize', resizeCanvas);
  canvas.addEventListener('contextmenu', event => event.preventDefault());
  canvas.addEventListener('pointerdown', event => {
    canvas.focus();
    canvas.setPointerCapture(event.pointerId);
    state.pointer = { id: event.pointerId, button: event.button, x: event.clientX, y: event.clientY, startX: event.clientX, startY: event.clientY };
  });
  canvas.addEventListener('pointermove', event => {
    const pointer = state.pointer;
    if (!pointer || pointer.id !== event.pointerId) return;
    const dx = event.clientX - pointer.x;
    const dy = event.clientY - pointer.y;
    pointer.x = event.clientX;
    pointer.y = event.clientY;
    if (pointer.button === 0) {
      state.camera.yaw += dx * 0.008;
      state.camera.pitch = clamp(state.camera.pitch + dy * 0.008, -1.52, 1.52);
    } else {
      const right = [Math.cos(state.camera.yaw), 0, -Math.sin(state.camera.yaw)];
      const up = [0, 1, 0];
      const factor = state.camera.distance * 0.0015;
      state.camera.target = add(state.camera.target, add(scale(right, -dx * factor), scale(up, dy * factor)));
    }
    requestFrame();
  });
  const finishPointer = event => {
    const pointer = state.pointer;
    if (!pointer || pointer.id !== event.pointerId) return;
    const movement = Math.hypot(event.clientX - pointer.startX, event.clientY - pointer.startY);
    if (pointer.button === 0 && movement < 4) pick(event.clientX, event.clientY);
    state.pointer = null;
  };
  canvas.addEventListener('pointerup', finishPointer);
  canvas.addEventListener('pointercancel', finishPointer);
  canvas.addEventListener('wheel', event => {
    event.preventDefault();
    state.camera.distance = clamp(state.camera.distance * Math.exp(event.deltaY * 0.0012), 0.01, 100000);
    requestFrame();
  }, { passive: false });
  canvas.addEventListener('webglcontextlost', event => {
    event.preventDefault();
    state.lost = true;
    state.contextLosses++;
    document.body.dataset.workshopWebglStatus = 'context-lost';
  });
  canvas.addEventListener('webglcontextrestored', () => {
    state.lost = false;
    const retainedScene = state.scene;
    const retainedTexture = state.textureBytes;
    initializeContext();
    if (retainedScene) uploadScene(retainedScene);
    if (retainedTexture) uploadTexture(retainedTexture.width, retainedTexture.height, retainedTexture.bytes);
    document.body.dataset.workshopWebglStatus = 'context-restored';
    requestFrame();
  });
}

function initializeContext() {
  const canvas = state.canvas || createCanvas();
  const gl = canvas.getContext('webgl2', {
    alpha: false,
    antialias: true,
    depth: true,
    stencil: false,
    preserveDrawingBuffer: true,
    powerPreference: 'high-performance',
  });
  if (!gl) throw new Error('WebGL 2 is unavailable; the software fallback remains available.');
  state.gl = gl;
  state.program = createProgram(gl, vertexSource, fragmentSource);
  state.lineProgram = createProgram(gl, lineVertexSource, lineFragmentSource);
  state.sphere = createSphereLines(gl);
  state.point = createPoint(gl);
  gl.enable(gl.DEPTH_TEST);
  gl.enable(gl.BLEND);
  gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
  document.body.dataset.workshopWebglStatus = 'ready';
}

function createProgram(gl, vertex, fragment) {
  const vertexShader = compile(gl, gl.VERTEX_SHADER, vertex);
  const fragmentShader = compile(gl, gl.FRAGMENT_SHADER, fragment);
  const program = gl.createProgram();
  gl.attachShader(program, vertexShader);
  gl.attachShader(program, fragmentShader);
  gl.linkProgram(program);
  gl.deleteShader(vertexShader);
  gl.deleteShader(fragmentShader);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    const log = gl.getProgramInfoLog(program) || 'unknown link failure';
    gl.deleteProgram(program);
    throw new Error(`WebGL program link failed: ${log}`);
  }
  return program;
}

function compile(gl, type, source) {
  const shader = gl.createShader(type);
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    const log = gl.getShaderInfoLog(shader) || 'unknown compile failure';
    gl.deleteShader(shader);
    throw new Error(`WebGL shader compilation failed: ${log}`);
  }
  return shader;
}

function uploadScene(scene) {
  const gl = state.gl;
  disposeMeshes();
  state.scene = scene;
  state.meshes = scene.primitives.map(primitive => {
    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    const position = uploadAttribute(gl, 0, primitive.positions, 3);
    const vertexCount = primitive.positions.length / 3;
    const normals = primitive.normals.length === vertexCount * 3 ? primitive.normals : new Array(vertexCount * 3).fill(0);
    const uvs = primitive.uvs.length === vertexCount * 2 ? primitive.uvs : new Array(vertexCount * 2).fill(0);
    const normal = uploadAttribute(gl, 1, normals, 3);
    const uv = uploadAttribute(gl, 2, uvs, 2);
    const index = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, index);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, new Uint16Array(primitive.indices), gl.STATIC_DRAW);
    gl.bindVertexArray(null);
    return { primitive, vao, buffers: [position, normal, uv, index], indexCount: primitive.indices.length };
  });
  state.selected = -1;
  frameAll();
}

function uploadAttribute(gl, location, values, width) {
  const buffer = gl.createBuffer();
  gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(values), gl.STATIC_DRAW);
  gl.enableVertexAttribArray(location);
  gl.vertexAttribPointer(location, width, gl.FLOAT, false, 0, 0);
  return buffer;
}

function uploadTexture(width, height, bytes) {
  const gl = state.gl;
  if (state.texture) gl.deleteTexture(state.texture);
  const texture = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, texture);
  gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
  gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, bytes);
  state.texture = texture;
  state.textureSize = [width, height];
  applyTextureFilter();
  if (isPowerOfTwo(width) && isPowerOfTwo(height)) gl.generateMipmap(gl.TEXTURE_2D);
  state.textureBytes = { width, height, bytes };
  requestFrame();
}

function applyTextureFilter() {
  const gl = state.gl;
  if (!gl || !state.texture) return;
  gl.bindTexture(gl.TEXTURE_2D, state.texture);
  const linear = state.options.linear;
  const hasMips = isPowerOfTwo(state.textureSize[0]) && isPowerOfTwo(state.textureSize[1]);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, linear ? (hasMips ? gl.LINEAR_MIPMAP_LINEAR : gl.LINEAR) : gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, linear ? gl.LINEAR : gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
}

function render(timestamp) {
  state.frameRequested = false;
  if (!state.visible || !state.gl || state.lost || !state.scene) return;
  const start = performance.now();
  resizeCanvas();
  const gl = state.gl;
  gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  gl.viewport(0, 0, state.canvas.width, state.canvas.height);
  gl.clearColor(0.035, 0.045, 0.062, 1);
  gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
  if (state.options.cull) { gl.enable(gl.CULL_FACE); gl.cullFace(gl.BACK); } else gl.disable(gl.CULL_FACE);
  const viewProjection = cameraMatrix();
  let animationTime = state.animation.time;
  if (state.animation.playing) {
    const duration = state.scene.animations?.[0]?.duration || 0;
    animationTime = duration > 0 ? (state.animation.startTime + (timestamp - state.animation.startedAt) / 1000) % duration : 0;
    state.animation.time = animationTime;
  }
  drawScene(viewProjection, animationTime, false);
  state.lastFrameMs = performance.now() - start;
  state.frame++;
  document.body.dataset.workshopWebglStatus = 'rendered';
  document.body.dataset.workshopWebglFrames = String(state.frame);
  document.body.dataset.workshopWebglSelected = String(state.selected);
  document.body.dataset.workshopWebglFrameMs = state.lastFrameMs.toFixed(3);
  if (state.animation.playing) requestFrame();
}

function drawScene(viewProjection, time, picking) {
  const gl = state.gl;
  gl.useProgram(state.program);
  setMatrix(state.program, 'uViewProjection', viewProjection);
  gl.uniform1i(gl.getUniformLocation(state.program, 'uTexture'), 0);
  state.drawCalls = 0;
  for (let index = 0; index < state.meshes.length; index++) {
    if (state.options.isolate && state.selected >= 0 && index !== state.selected) continue;
    const mesh = state.meshes[index];
    const model = primitiveTransform(index, time);
    setMatrix(state.program, 'uModel', model);
    const color = mesh.primitive.color;
    gl.uniform4f(gl.getUniformLocation(state.program, 'uColor'), color[0], color[1], color[2], color[3]);
    const pickId = index + 1;
    gl.uniform4f(gl.getUniformLocation(state.program, 'uPickColor'), (pickId & 255) / 255, ((pickId >> 8) & 255) / 255, ((pickId >> 16) & 255) / 255, 1);
    gl.uniform1i(gl.getUniformLocation(state.program, 'uMode'), picking ? 2 : 0);
    gl.uniform1i(gl.getUniformLocation(state.program, 'uHasTexture'), !picking && !!state.texture && mesh.primitive.uvs.length > 0 ? 1 : 0);
    gl.uniform1i(gl.getUniformLocation(state.program, 'uLit'), !picking && state.options.lit ? 1 : 0);
    gl.uniform1i(gl.getUniformLocation(state.program, 'uSelected'), !picking && index === state.selected ? 1 : 0);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, state.texture);
    gl.bindVertexArray(mesh.vao);
    gl.drawElements(gl.TRIANGLES, mesh.indexCount, gl.UNSIGNED_SHORT, 0);
    state.drawCalls++;
    if (!picking && state.options.wireframe) {
      gl.uniform1i(gl.getUniformLocation(state.program, 'uHasTexture'), 0);
      gl.uniform1i(gl.getUniformLocation(state.program, 'uLit'), 0);
      gl.uniform4f(gl.getUniformLocation(state.program, 'uColor'), 0.1, 0.85, 1.0, 0.72);
      gl.depthFunc(gl.LEQUAL);
      gl.drawElements(gl.LINES, mesh.indexCount, gl.UNSIGNED_SHORT, 0);
      gl.depthFunc(gl.LESS);
      state.drawCalls++;
    }
    gl.bindVertexArray(null);
    if (!picking) drawDiagnostics(mesh, model, viewProjection);
  }
}

function drawDiagnostics(mesh, model, viewProjection) {
  const gl = state.gl;
  if (state.options.bounds) {
    const sphere = mesh.primitive.sphere;
    const sphereModel = multiply(model, multiply(translation([sphere[0], sphere[1], sphere[2]]), scaleMatrix([sphere[3], sphere[3], sphere[3]])));
    gl.useProgram(state.lineProgram);
    setMatrix(state.lineProgram, 'uViewProjection', viewProjection);
    setMatrix(state.lineProgram, 'uModel', sphereModel);
    gl.uniform4f(gl.getUniformLocation(state.lineProgram, 'uColor'), 1.0, 0.45, 0.12, 0.72);
    gl.uniform1f(gl.getUniformLocation(state.lineProgram, 'uPointSize'), 4);
    gl.bindVertexArray(state.sphere.vao);
    gl.drawArrays(gl.LINES, 0, state.sphere.count);
    state.drawCalls++;
  }
  if (state.options.pivots) {
    const pivot = mesh.primitive.pivot;
    gl.useProgram(state.lineProgram);
    setMatrix(state.lineProgram, 'uViewProjection', viewProjection);
    setMatrix(state.lineProgram, 'uModel', multiply(model, translation(pivot)));
    gl.uniform4f(gl.getUniformLocation(state.lineProgram, 'uColor'), 1.0, 0.88, 0.1, 1.0);
    gl.uniform1f(gl.getUniformLocation(state.lineProgram, 'uPointSize'), 8);
    gl.bindVertexArray(state.point.vao);
    gl.drawArrays(gl.POINTS, 0, 1);
    state.drawCalls++;
  }
  gl.bindVertexArray(null);
}

function primitiveTransform(index, time) {
  const primitive = state.scene.primitives[index];
  const track = state.scene.animations?.[0]?.tracks?.find(candidate => candidate.primitive === index);
  if (!track || track.keys.length === 0) return identity();
  const value = sampleTrack(track.keys, time);
  const pivot = primitive.pivot;
  return multiply(translation(add(pivot, value.translation)), multiply(quaternion(value.rotation), multiply(scaleMatrix(value.scale), translation(scale(pivot, -1)))));
}

function sampleTrack(keys, time) {
  if (keys.length === 1 || time <= keys[0].time) return keys[0];
  if (time >= keys[keys.length - 1].time) return keys[keys.length - 1];
  let high = 1;
  while (high < keys.length && keys[high].time < time) high++;
  const a = keys[high - 1];
  const b = keys[high];
  const t = (time - a.time) / Math.max(1e-7, b.time - a.time);
  return { translation: mix3(a.translation, b.translation, t), rotation: normalize4(mix4(a.rotation, b.rotation, t)), scale: mix3(a.scale, b.scale, t) };
}

function cameraMatrix() {
  const camera = state.camera;
  const cp = Math.cos(camera.pitch);
  const direction = [cp * Math.sin(camera.yaw), Math.sin(camera.pitch), cp * Math.cos(camera.yaw)];
  const eye = add(camera.target, scale(direction, camera.distance));
  const view = lookAt(eye, camera.target, [0, 1, 0]);
  const aspect = state.canvas.width / Math.max(1, state.canvas.height);
  const projection = state.options.orthographic
    ? ortho(-camera.distance * aspect * 0.55, camera.distance * aspect * 0.55, -camera.distance * 0.55, camera.distance * 0.55, 0.001, Math.max(1000, camera.distance * 20))
    : perspective(Math.PI / 4, aspect, Math.max(0.001, camera.distance / 10000), Math.max(1000, camera.distance * 20));
  return multiply(projection, view);
}

function pick(clientX, clientY) {
  if (!state.visible || !state.scene || !state.gl) return;
  ensurePickTarget();
  const gl = state.gl;
  gl.bindFramebuffer(gl.FRAMEBUFFER, state.pick.framebuffer);
  gl.viewport(0, 0, state.canvas.width, state.canvas.height);
  gl.clearColor(0, 0, 0, 1);
  gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
  drawScene(cameraMatrix(), state.animation.time, true);
  const rect = state.canvas.getBoundingClientRect();
  const x = Math.floor((clientX - rect.left) * state.canvas.width / rect.width);
  const y = Math.floor((rect.bottom - clientY) * state.canvas.height / rect.height);
  const pixel = new Uint8Array(4);
  gl.readPixels(clamp(x, 0, state.canvas.width - 1), clamp(y, 0, state.canvas.height - 1), 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
  const id = pixel[0] | (pixel[1] << 8) | (pixel[2] << 16);
  state.selected = id > 0 && id <= state.meshes.length ? id - 1 : -1;
  gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  document.body.dataset.workshopWebglSelected = String(state.selected);
  requestFrame();
}

function ensurePickTarget() {
  const gl = state.gl;
  if (state.pick && state.pick.width === state.canvas.width && state.pick.height === state.canvas.height) return;
  disposePickTarget();
  const framebuffer = gl.createFramebuffer();
  const color = gl.createTexture();
  const depth = gl.createRenderbuffer();
  gl.bindTexture(gl.TEXTURE_2D, color);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, state.canvas.width, state.canvas.height, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
  gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
  gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT16, state.canvas.width, state.canvas.height);
  gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
  gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, color, 0);
  gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth);
  if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) throw new Error('WebGL picking framebuffer is incomplete.');
  gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  state.pick = { framebuffer, color, depth, width: state.canvas.width, height: state.canvas.height };
}

function disposePickTarget() {
  if (!state.pick || !state.gl) return;
  state.gl.deleteFramebuffer(state.pick.framebuffer);
  state.gl.deleteTexture(state.pick.color);
  state.gl.deleteRenderbuffer(state.pick.depth);
  state.pick = null;
}

function disposeMeshes() {
  if (!state.gl) { state.meshes = []; return; }
  for (const mesh of state.meshes) {
    state.gl.deleteVertexArray(mesh.vao);
    for (const buffer of mesh.buffers) state.gl.deleteBuffer(buffer);
  }
  state.meshes = [];
}

function createSphereLines(gl) {
  const vertices = [];
  const segments = 64;
  for (let axis = 0; axis < 3; axis++) {
    for (let i = 0; i < segments; i++) {
      for (const step of [i, i + 1]) {
        const angle = step * Math.PI * 2 / segments;
        const a = Math.cos(angle), b = Math.sin(angle);
        vertices.push(...(axis === 0 ? [0, a, b] : axis === 1 ? [a, 0, b] : [a, b, 0]));
      }
    }
  }
  const vao = gl.createVertexArray();
  const buffer = gl.createBuffer();
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(vertices), gl.STATIC_DRAW);
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);
  return { vao, buffer, count: vertices.length / 3 };
}

function createPoint(gl) {
  const vao = gl.createVertexArray();
  const buffer = gl.createBuffer();
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([0, 0, 0]), gl.STATIC_DRAW);
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);
  return { vao, buffer };
}

function requestFrame() {
  if (state.frameRequested || !state.visible) return;
  state.frameRequested = true;
  requestAnimationFrame(render);
}

export function initialize() {
  try {
    createCanvas();
    if (!state.gl || state.lost) initializeContext();
    const gl = state.gl;
    const vendor = gl.getParameter(gl.VENDOR);
    const renderer = gl.getParameter(gl.RENDERER);
    const version = gl.getParameter(gl.VERSION);
    return `WebGL 2 ready: ${vendor}; ${renderer}; ${version}`;
  } catch (error) {
    document.body.dataset.workshopWebglStatus = 'failed';
    return `WebGL initialization failed: ${error?.message ?? error}`;
  }
}

export function loadScene(sceneJson) {
  if (!state.gl) {
    const init = initialize();
    if (!state.gl) return init;
  }
  try {
    const scene = JSON.parse(sceneJson);
    uploadScene(scene);
    state.visible = true;
    state.canvas.style.display = 'block';
    requestFrame();
    return `WebGL scene uploaded once: ${scene.primitives.length} submesh(es)`;
  } catch (error) {
    return `WebGL scene upload failed: ${error?.message ?? error}`;
  }
}

export function setTexture(width, height, rgbaBase64) {
  try {
    const binary = atob(rgbaBase64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    if (bytes.length !== width * height * 4) throw new Error(`expected ${width * height * 4} RGBA bytes, got ${bytes.length}`);
    uploadTexture(width, height, bytes);
    return `texture ${width}x${height} uploaded once`;
  } catch (error) {
    return `texture upload failed: ${error?.message ?? error}`;
  }
}

export function clearTexture() {
  if (state.texture && state.gl) state.gl.deleteTexture(state.texture);
  state.texture = null;
  state.textureBytes = null;
  state.textureSize = [0, 0];
  requestFrame();
}

export function setOptions(lit, wireframe, bounds, pivots, cullBackFaces, orthographic, linearFiltering, isolateSelection) {
  Object.assign(state.options, { lit, wireframe, bounds, pivots, cull: cullBackFaces, orthographic, linear: linearFiltering, isolate: isolateSelection });
  applyTextureFilter();
  requestFrame();
}

export function setAnimation(playing, timeSeconds) {
  state.animation.playing = playing;
  state.animation.time = Math.max(0, timeSeconds);
  state.animation.startTime = state.animation.time;
  state.animation.startedAt = performance.now();
  requestFrame();
}

export function frameAll() {
  if (!state.scene) return;
  const min = state.scene.boundsMinimum, max = state.scene.boundsMaximum;
  state.camera.target = [(min[0] + max[0]) * 0.5, (min[1] + max[1]) * 0.5, (min[2] + max[2]) * 0.5];
  state.camera.distance = Math.max(0.01, Math.hypot(max[0] - min[0], max[1] - min[1], max[2] - min[2]) * 1.35);
  requestFrame();
}

export function frameSelected() {
  if (!state.scene || state.selected < 0) return;
  const sphere = state.scene.primitives[state.selected].sphere;
  state.camera.target = [sphere[0], sphere[1], sphere[2]];
  state.camera.distance = Math.max(0.01, sphere[3] * 2.8);
  requestFrame();
}

export function setVisible(visible) {
  state.visible = visible;
  if (state.canvas) state.canvas.style.display = visible ? 'block' : 'none';
  if (visible) requestFrame();
}

export function getDiagnostics() {
  if (!state.gl) return 'WebGL 2 is not initialized.';
  const gl = state.gl;
  return JSON.stringify({
    vendor: gl.getParameter(gl.VENDOR),
    renderer: gl.getParameter(gl.RENDERER),
    version: gl.getParameter(gl.VERSION),
    shadingLanguage: gl.getParameter(gl.SHADING_LANGUAGE_VERSION),
    maxTextureSize: gl.getParameter(gl.MAX_TEXTURE_SIZE),
    drawingBuffer: [gl.drawingBufferWidth, gl.drawingBufferHeight],
    meshes: state.meshes.length,
    drawCalls: state.drawCalls,
    selected: state.selected,
    frames: state.frame,
    lastFrameMs: state.lastFrameMs,
    contextLosses: state.contextLosses,
    visible: state.visible,
  });
}

export function getQueryParameter(name) {
  return new URLSearchParams(globalThis.location.search).get(name);
}

export function setSmokeStatus(value) {
  document.body.dataset.workshopSmoke = value;
}

export function disposeRenderer() {
  if (state.gl) {
    disposeMeshes();
    disposePickTarget();
    if (state.texture) state.gl.deleteTexture(state.texture);
    if (state.sphere) { state.gl.deleteVertexArray(state.sphere.vao); state.gl.deleteBuffer(state.sphere.buffer); }
    if (state.point) { state.gl.deleteVertexArray(state.point.vao); state.gl.deleteBuffer(state.point.buffer); }
    if (state.program) state.gl.deleteProgram(state.program);
    if (state.lineProgram) state.gl.deleteProgram(state.lineProgram);
    state.gl.getExtension('WEBGL_lose_context')?.loseContext();
  }
  state.canvas?.remove();
  state.canvas = null;
  state.gl = null;
  state.program = null;
  state.lineProgram = null;
  state.scene = null;
  state.visible = false;
}

function setMatrix(program, name, value) { state.gl.uniformMatrix4fv(state.gl.getUniformLocation(program, name), false, value); }
function clamp(value, min, max) { return Math.max(min, Math.min(max, value)); }
function isPowerOfTwo(value) { return value > 0 && (value & (value - 1)) === 0; }
function add(a, b) { return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]; }
function subtract(a, b) { return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]; }
function scale(a, value) { return [a[0] * value, a[1] * value, a[2] * value]; }
function dot(a, b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
function cross(a, b) { return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]; }
function normalize(a) { const length = Math.hypot(a[0], a[1], a[2]) || 1; return scale(a, 1 / length); }
function mix3(a, b, t) { return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t]; }
function mix4(a, b, t) { const sign = dot4(a, b) < 0 ? -1 : 1; return [a[0] + (b[0] * sign - a[0]) * t, a[1] + (b[1] * sign - a[1]) * t, a[2] + (b[2] * sign - a[2]) * t, a[3] + (b[3] * sign - a[3]) * t]; }
function dot4(a, b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]; }
function normalize4(a) { const length = Math.hypot(a[0], a[1], a[2], a[3]) || 1; return [a[0] / length, a[1] / length, a[2] / length, a[3] / length]; }
function identity() { return new Float32Array([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]); }
function translation(v) { const m = identity(); m[12] = v[0]; m[13] = v[1]; m[14] = v[2]; return m; }
function scaleMatrix(v) { const m = identity(); m[0] = v[0]; m[5] = v[1]; m[10] = v[2]; return m; }
function quaternion(q) {
  const [x, y, z, w] = normalize4(q); const x2 = x + x, y2 = y + y, z2 = z + z;
  const xx = x * x2, xy = x * y2, xz = x * z2, yy = y * y2, yz = y * z2, zz = z * z2, wx = w * x2, wy = w * y2, wz = w * z2;
  return new Float32Array([1 - (yy + zz), xy + wz, xz - wy, 0, xy - wz, 1 - (xx + zz), yz + wx, 0, xz + wy, yz - wx, 1 - (xx + yy), 0, 0, 0, 0, 1]);
}
function multiply(a, b) {
  const out = new Float32Array(16);
  for (let column = 0; column < 4; column++) for (let row = 0; row < 4; row++) out[column * 4 + row] = a[row] * b[column * 4] + a[4 + row] * b[column * 4 + 1] + a[8 + row] * b[column * 4 + 2] + a[12 + row] * b[column * 4 + 3];
  return out;
}
function perspective(fovy, aspect, near, far) {
  const f = 1 / Math.tan(fovy / 2), nf = 1 / (near - far);
  return new Float32Array([f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) * nf, -1, 0, 0, 2 * far * near * nf, 0]);
}
function ortho(left, right, bottom, top, near, far) {
  return new Float32Array([2 / (right - left), 0, 0, 0, 0, 2 / (top - bottom), 0, 0, 0, 0, -2 / (far - near), 0, -(right + left) / (right - left), -(top + bottom) / (top - bottom), -(far + near) / (far - near), 1]);
}
function lookAt(eye, center, up) {
  const z = normalize(subtract(eye, center)), x = normalize(cross(up, z)), y = cross(z, x);
  return new Float32Array([x[0], y[0], z[0], 0, x[1], y[1], z[1], 0, x[2], y[2], z[2], 0, -dot(x, eye), -dot(y, eye), -dot(z, eye), 1]);
}

globalThis.workshopWebGlSmoke = {
  orbit() {
    state.camera.yaw += 0.35;
    state.camera.pitch = clamp(state.camera.pitch - 0.12, -1.52, 1.52);
    requestFrame();
    return state.frame;
  },
  contextLoss() {
    const extension = state.gl?.getExtension('WEBGL_lose_context');
    if (!extension) return false;
    extension.loseContext();
    setTimeout(() => extension.restoreContext(), 250);
    return true;
  },
  diagnostics() { return getDiagnostics(); },
};
