namespace MechMaker.Server;

/// <summary>
/// The browser viewer served at "/" in HTTP mode: three.js rendering the
/// session machine's compiled scene (semantic primitives from /scene).
/// three.js comes from a CDN; everything else is inline — no build step.
/// </summary>
public static class WebUi
{
    public const string Html = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>MechMaker viewer</title>
<style>
  body { margin: 0; overflow: hidden; background: #1b1d21; font-family: system-ui, sans-serif; }
  #bar { position: fixed; top: 0; left: 0; right: 0; padding: 6px 12px; background: rgba(20,22,26,.85);
         color: #cfd3da; font-size: 13px; display: flex; gap: 12px; align-items: center; z-index: 5; }
  #bar button { background: #3a4150; color: #cfd3da; border: 0; border-radius: 4px; padding: 4px 10px; cursor: pointer; }
  #tip { position: fixed; padding: 3px 8px; background: rgba(10,12,15,.85); color: #e8eaf0;
         border-radius: 4px; font-size: 12px; pointer-events: none; display: none; z-index: 6; }
  #err { position: fixed; top: 40px; left: 12px; color: #ff9a76; font-size: 13px; white-space: pre; }
</style>
</head>
<body>
<div id="bar">
  <span id="title">MechMaker viewer</span>
  <button id="reload">reload</button>
  <label style="display:flex;gap:4px;align-items:center"><input type="checkbox" id="auto"> live (1s)</label>
</div>
<div id="tip"></div>
<div id="err"></div>
<script type="importmap">
{ "imports": {
    "three": "https://unpkg.com/three@0.160.0/build/three.module.js",
    "three/addons/": "https://unpkg.com/three@0.160.0/examples/jsm/"
} }
</script>
<script type="module">
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x1b1d21);

const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setSize(innerWidth, innerHeight);
renderer.setPixelRatio(devicePixelRatio);
document.body.appendChild(renderer.domElement);

const camera = new THREE.PerspectiveCamera(50, innerWidth / innerHeight, 0.005, 200);
camera.up.set(0, 0, 1); // MuJoCo is Z-up

const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;

scene.add(new THREE.HemisphereLight(0xdfe6f0, 0x33383f, 1.1));
const sun = new THREE.DirectionalLight(0xffffff, 1.6);
sun.position.set(2, -1.5, 3);
scene.add(sun);

const shapes = new THREE.Group();
scene.add(shapes);
const byUuid = new Map(); // mesh uuid -> shape name (hover)

async function load() {
    const res = await fetch('/scene');
    const data = await res.json();
    document.getElementById('title').textContent =
        data.name + ' — ' + data.shapes.length + ' shapes';
    // clear
    for (const mesh of [...shapes.children]) {
        shapes.remove(mesh);
        mesh.geometry.dispose();
        mesh.material.dispose();
    }
    byUuid.clear();

    const box = new THREE.Box3();
    const geoCache = new Map();
    for (const s of data.shapes) {
        const key = s.k + ':' + s.h.join(',');
        let geo = geoCache.get(key);
        if (!geo) {
            if (s.k === 0) {
                geo = new THREE.BoxGeometry(s.h[0] * 2, s.h[1] * 2, s.h[2] * 2);
            } else {
                // engine cylinders run along local Z; three.js cylinders along Y
                geo = new THREE.CylinderGeometry(s.h[0], s.h[0], s.h[1] * 2, 24);
                geo.rotateX(Math.PI / 2);
            }
            geoCache.set(key, geo);
        }
        const mat = new THREE.MeshStandardMaterial({
            color: new THREE.Color(s.c[0], s.c[1], s.c[2]),
            transparent: s.c[3] < 1,
            opacity: s.c[3],
            roughness: 0.62, metalness: 0.12
        });
        const mesh = new THREE.Mesh(geo, mat);
        mesh.matrixAutoUpdate = false;
        // row-major 3x3 rotation + translation -> column-major Matrix4
        mesh.matrix.set(
            s.r[0], s.r[1], s.r[2], s.p[0],
            s.r[3], s.r[4], s.r[5], s.p[1],
            s.r[6], s.r[7], s.r[8], s.p[2],
            0, 0, 0, 1);
        mesh.matrix.decompose(mesh.position, mesh.quaternion, mesh.scale);
        shapes.add(mesh);
        byUuid.set(mesh.uuid, s.n);
        box.expandByObject(mesh);
    }

    const centre = box.getCenter(new THREE.Vector3());
    const size = box.getSize(new THREE.Vector3()).length() || 1;
    controls.target.copy(centre);
    camera.position.set(centre.x + size, centre.y - size, centre.z + size * 0.8);
    controls.update();
}

const ray = new THREE.Raycaster();
const pointer = new THREE.Vector2();
const tip = document.getElementById('tip');
addEventListener('pointermove', (e) => {
    pointer.x = (e.clientX / innerWidth) * 2 - 1;
    pointer.y = -(e.clientY / innerHeight) * 2 + 1;
    ray.setFromCamera(pointer, camera);
    const hit = ray.intersectObjects(shapes.children, false)[0];
    if (hit) {
        tip.style.display = 'block';
        tip.style.left = (e.clientX + 12) + 'px';
        tip.style.top = (e.clientY + 12) + 'px';
        tip.textContent = byUuid.get(hit.object.uuid) ?? '';
    } else {
        tip.style.display = 'none';
    }
});

let auto = null;
document.getElementById('reload').addEventListener('click', load);
document.getElementById('auto').addEventListener('change', (e) => {
    if (e.target.checked) auto = setInterval(load, 1000);
    else { clearInterval(auto); auto = null; }
});
document.getElementById('err').textContent = '';

addEventListener('resize', () => {
    camera.aspect = innerWidth / innerHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(innerWidth, innerHeight);
});

function frame() {
    requestAnimationFrame(frame);
    controls.update();
    renderer.render(scene, camera);
}
load().catch((e) => document.getElementById('err').textContent = String(e));
frame();
</script>
</body>
</html>
""";
}