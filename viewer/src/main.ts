import "./style.css";
import * as THREE from "three";
import * as OBC from "@thatopen/components";
import type { FragmentsModel } from "@thatopen/fragments";
import { initPlugins } from "./plugins";

const ALLOWED_PSETS = ["INSUR_Medicion"];
const MEASURABLE_MATERIAL_PREFIX = "IDS-M-";

const container = document.getElementById("container") as HTMLDivElement;
const statusEl = document.getElementById("status") as HTMLSpanElement;
const propsEl = document.getElementById("props") as HTMLElement;
const fileInput = document.getElementById("file") as HTMLInputElement;

const components = new OBC.Components();
const worlds = components.get(OBC.Worlds);
const world = worlds.create<
  OBC.SimpleScene,
  OBC.SimpleCamera,
  OBC.SimpleRenderer
>();

world.scene = new OBC.SimpleScene(components);
world.renderer = new OBC.SimpleRenderer(components, container);
world.renderer.showLogo = false;
world.camera = new OBC.SimpleCamera(components);
components.init();
world.scene.setup();
world.scene.three.background = new THREE.Color(0x1e1f22);
world.camera.controls.setLookAt(12, 8, 12, 0, 0, 0);

const grids = components.get(OBC.Grids);
grids.create(world);

const fragments = components.get(OBC.FragmentsManager);
fragments.init("/worker.mjs");

world.camera.controls.addEventListener("update", () =>
  fragments.core.update(),
);

fragments.list.onItemSet.add(({ value: model }) => {
  model.useCamera(world.camera.three);
  world.scene.three.add(model.object);
  fragments.core.update(true);
});

const ifcLoader = components.get(OBC.IfcLoader);
await ifcLoader.setup({
  autoSetWasm: false,
  wasm: { path: "/", absolute: true },
});

const highlightMaterial: import("@thatopen/fragments").MaterialDefinition = {
  color: new THREE.Color("#5865f2"),
  renderedFaces: 0,
  opacity: 1,
  transparent: false,
};

let selected: { model: FragmentsModel; localId: number } | null = null;
let currentFile: File | null = null;

initPlugins({ getFile: () => currentFile, components, fragments });

fileInput.addEventListener("change", async () => {
  const file = fileInput.files?.[0];
  if (!file) return;
  currentFile = file;
  statusEl.textContent = `Cargando ${file.name}...`;
  try {
    const data = new Uint8Array(await file.arrayBuffer());
    const model = await ifcLoader.load(
      data,
      true,
      file.name.replace(/\.ifc$/i, ""),
    );
    const box = new THREE.Box3().setFromObject(model.object);
    if (!box.isEmpty()) {
      const sphere = box.getBoundingSphere(new THREE.Sphere());
      await world.camera.controls.fitToSphere(sphere, true);
    }
    statusEl.textContent = `${file.name} cargado`;
  } catch (err) {
    console.error(err);
    const reason = err instanceof Error ? err.message : String(err);
    statusEl.textContent = `Error al cargar ${file.name}: ${reason}`;
  } finally {
    fileInput.value = "";
  }
});

type Row = Record<string, { value?: unknown } | undefined>;

function valueOf(row: unknown, key: string): string | null {
  const v = (row as Row | undefined)?.[key]?.value;
  return v === null || v === undefined || typeof v === "object"
    ? null
    : String(v);
}

function toArray(x: unknown): Record<string, unknown>[] {
  if (Array.isArray(x)) return x as Record<string, unknown>[];
  return x && typeof x === "object" ? [x as Record<string, unknown>] : [];
}

function renderItemData(data: Record<string, unknown>): string {
  const basic = ["_category", "Name", "ObjectType", "Tag", "_guid"]
    .map((key) => [key, valueOf(data, key)] as const)
    .filter(([, v]) => v !== null)
    .map(
      ([k, v]) =>
        `<tr><td>${escapeHtml(k)}</td><td>${escapeHtml(v!)}</td></tr>`,
    );
  let html = `<table>${basic.join("")}</table>`;

  for (const pset of toArray(data.IsDefinedBy)) {
    const psetName = valueOf(pset, "Name");
    if (!psetName || !ALLOWED_PSETS.includes(psetName)) continue;
    const rows = toArray(pset.HasProperties)
      .map((prop) => [valueOf(prop, "Name"), valueOf(prop, "NominalValue")])
      .filter(([name]) => name !== null)
      .map(
        ([name, v]) =>
          `<tr><td>${escapeHtml(name!)}</td><td>${escapeHtml(v ?? "")}</td></tr>`,
      );
    html += `<h3>${escapeHtml(psetName)}</h3><table>${rows.join("")}</table>`;
  }
  const materials = findMeasurableMaterials(data);
  html += `<h3>Materiales medibles (${MEASURABLE_MATERIAL_PREFIX})</h3>`;
  html += materials.length
    ? `<table>${materials.map((m) => `<tr><td>${escapeHtml(m)}</td></tr>`).join("")}</table>`
    : `<p>Ninguno</p>`;
  return html;
}

function findMeasurableMaterials(root: unknown): string[] {
  const found = new Set<string>();
  const seen = new WeakSet<object>();
  const visit = (node: unknown, depth: number) => {
    if (!node || typeof node !== "object" || depth > 10 || seen.has(node)) return;
    seen.add(node);
    const category = valueOf(node, "_category");
    const name = valueOf(node, "Name");
    if (
      category?.toUpperCase() === "IFCMATERIAL" &&
      name?.startsWith(MEASURABLE_MATERIAL_PREFIX)
    ) {
      found.add(name);
    }
    for (const child of Object.values(node)) {
      if (Array.isArray(child)) child.forEach((c) => visit(c, depth + 1));
      else visit(child, depth + 1);
    }
  };
  visit(root, 0);
  return [...found];
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

container.addEventListener("click", async (event) => {
  const canvas = world.renderer!.three.domElement;
  const mouse = new THREE.Vector2(event.clientX, event.clientY);
  const dom = canvas;

  if (selected) {
    await selected.model.resetHighlight([selected.localId]);
    selected = null;
  }

  let hit: Awaited<ReturnType<FragmentsModel["raycast"]>> = null;
  for (const model of fragments.list.values()) {
    hit = await model.raycast({ camera: world.camera.three, mouse, dom });
    if (hit) break;
  }

  if (!hit) {
    propsEl.textContent = "Haz clic en un elemento para ver sus propiedades";
    fragments.core.update(true);
    return;
  }

  selected = { model: hit.fragments, localId: hit.localId };
  propsEl.textContent = `Elemento #${hit.localId}: cargando propiedades...`;

  try {
    await hit.fragments.highlight([hit.localId], highlightMaterial);
    fragments.core.update(true);
  } catch (err) {
    console.error("highlight", err);
  }

  try {
    const [data] = await hit.fragments.getItemsData([hit.localId], {
      attributesDefault: true,
      relationsDefault: { attributes: false, relations: false },
      relations: {
        IsDefinedBy: { attributes: true, relations: true },
        HasProperties: { attributes: true, relations: false },
        DefinesOccurrence: { attributes: false, relations: false },
        HasAssociations: { attributes: true, relations: true },
        ForLayerSet: { attributes: true, relations: true },
        MaterialLayers: { attributes: true, relations: true },
        Materials: { attributes: true, relations: true },
        MaterialConstituents: { attributes: true, relations: true },
        MaterialProfiles: { attributes: true, relations: true },
        Material: { attributes: true, relations: false },
      },
    });
    const table = renderItemData((data ?? {}) as Record<string, unknown>);
    propsEl.innerHTML = `<h3>Elemento #${hit.localId}</h3>${table}`;
  } catch (err) {
    console.error("getItemsData", err);
    const reason = err instanceof Error ? err.message : String(err);
    propsEl.textContent = `Error al leer propiedades: ${reason}`;
  }
});
