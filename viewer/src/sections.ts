import * as THREE from "three";
import { TransformControls } from "three/examples/jsm/controls/TransformControls.js";
import * as OBC from "@thatopen/components";
import type { FragmentsModel } from "@thatopen/fragments";

type Axis = "x" | "y" | "z";

interface Cut {
  axis: Axis;
  color: number;
  vector: THREE.Vector3;
  plane: THREE.Plane;
  active: boolean;
  side: 1 | -1;
  value: number;
  helper: THREE.Mesh | null;
}

interface Options {
  world: OBC.SimpleWorld<OBC.SimpleScene, OBC.SimpleCamera, OBC.SimpleRenderer>;
  fragments: OBC.FragmentsManager;
  getSelected: () => { model: FragmentsModel; localId: number } | null;
}

const HELPER_OFFSET = 0.003;

export function initSections({ world, fragments, getSelected }: Options) {
  const bar = document.getElementById("section-bar") as HTMLElement;
  const axisButtons = {
    x: bar.querySelector<HTMLButtonElement>('[data-axis="x"]')!,
    y: bar.querySelector<HTMLButtonElement>('[data-axis="y"]')!,
    z: bar.querySelector<HTMLButtonElement>('[data-axis="z"]')!,
  };
  const flipBtn = bar.querySelector<HTMLButtonElement>("[data-flip]")!;
  const clearBtn = bar.querySelector<HTMLButtonElement>("[data-clear]")!;

  const cuts: Record<Axis, Cut> = {
    x: { axis: "x", color: 0xe5484d, vector: new THREE.Vector3(1, 0, 0), plane: new THREE.Plane(), active: false, side: 1, value: 0, helper: null },
    y: { axis: "y", color: 0x30a46c, vector: new THREE.Vector3(0, 1, 0), plane: new THREE.Plane(), active: false, side: 1, value: 0, helper: null },
    z: { axis: "z", color: 0x3e63dd, vector: new THREE.Vector3(0, 0, 1), plane: new THREE.Plane(), active: false, side: 1, value: 0, helper: null },
  };

  const scene = world.scene.three;
  const pivot = new THREE.Object3D();
  scene.add(pivot);

  const transform = new TransformControls(world.camera.three, world.renderer!.three.domElement);
  transform.setSpace("world");
  transform.setSize(0.8);
  const gizmo = transform.getHelper();
  gizmo.visible = false;
  scene.add(gizmo);

  let current: Cut | null = null;
  let lastDragEnd = 0;

  transform.addEventListener("dragging-changed", (event) => {
    world.camera.controls.enabled = !(event as unknown as { value: boolean }).value;
    if (!(event as unknown as { value: boolean }).value) lastDragEnd = performance.now();
  });

  transform.addEventListener("objectChange", () => {
    if (!current) return;
    current.value = pivot.position[current.axis];
    applyCut(current);
  });

  function modelBounds(): THREE.Box3 {
    const box = new THREE.Box3();
    for (const model of fragments.list.values()) box.union(model.box);
    return box;
  }

  function applyCut(cut: Cut) {
    cut.plane.normal.copy(cut.vector).multiplyScalar(-cut.side);
    cut.plane.constant = cut.side * cut.value;
    world.renderer!.setPlane(cut.active, cut.plane);
    world.renderer!.updateClippingPlanes();

    if (cut.helper) {
      cut.helper.visible = cut.active;
      cut.helper.position.copy(pivot.position);
      cut.helper.position[cut.axis] = cut.value - cut.side * HELPER_OFFSET;
    }
    fragments.core.update(true);
    updateBar();
  }

  function buildHelper(cut: Cut, box: THREE.Box3, center: THREE.Vector3): THREE.Mesh {
    cut.helper?.removeFromParent();
    const size = box.getSize(new THREE.Vector3()).multiplyScalar(1.1);
    const [w, h] =
      cut.axis === "y" ? [size.x, size.z] : cut.axis === "x" ? [size.z, size.y] : [size.x, size.y];
    const geometry = new THREE.PlaneGeometry(Math.max(w, 1), Math.max(h, 1));
    if (cut.axis === "y") geometry.rotateX(-Math.PI / 2);
    if (cut.axis === "x") geometry.rotateY(Math.PI / 2);

    const mesh = new THREE.Mesh(
      geometry,
      new THREE.MeshBasicMaterial({
        color: cut.color,
        transparent: true,
        opacity: 0.18,
        side: THREE.DoubleSide,
        depthWrite: false,
      }),
    );
    mesh.position.copy(center);
    mesh.raycast = () => {};
    scene.add(mesh);
    return mesh;
  }

  async function selectionCenter(): Promise<THREE.Vector3 | null> {
    const sel = getSelected();
    if (!sel) return null;
    const box = await sel.model.getMergedBox([sel.localId]);
    return box.isEmpty() ? null : box.getCenter(new THREE.Vector3());
  }

  async function activate(axis: Axis) {
    const cut = cuts[axis];
    const center = await selectionCenter();

    if (center) {
      const bounds = modelBounds();
      cut.value = center[axis];
      cut.active = true;
      pivot.position.copy(center);
      cut.helper = buildHelper(cut, bounds.isEmpty() ? new THREE.Box3().setFromCenterAndSize(center, new THREE.Vector3(20, 20, 20)) : bounds, center);
    } else if (!cut.active) {
      return;
    }

    attach(cut);
    applyCut(cut);
  }

  function attach(cut: Cut) {
    current = cut;
    pivot.position[cut.axis] = cut.value;
    transform.attach(pivot);
    transform.showX = cut.axis === "x";
    transform.showY = cut.axis === "y";
    transform.showZ = cut.axis === "z";
    transform.setMode("translate");
    gizmo.visible = true;
  }

  function clearAll() {
    for (const cut of Object.values(cuts)) {
      cut.active = false;
      cut.side = 1;
      applyCut(cut);
      cut.helper?.removeFromParent();
      cut.helper = null;
    }
    transform.detach();
    gizmo.visible = false;
    current = null;
    updateBar();
  }

  function updateBar() {
    const hasSelection = getSelected() !== null;
    const anyActive = Object.values(cuts).some((c) => c.active);
    bar.hidden = !hasSelection && !anyActive;

    for (const axis of ["x", "y", "z"] as const) {
      axisButtons[axis].disabled = !hasSelection && !cuts[axis].active;
      axisButtons[axis].classList.toggle("active", cuts[axis].active);
      axisButtons[axis].classList.toggle("current", current?.axis === axis && cuts[axis].active);
    }
    flipBtn.disabled = !current?.active;
    clearBtn.disabled = !anyActive;
  }

  for (const axis of ["x", "y", "z"] as const) {
    axisButtons[axis].addEventListener("click", () => activate(axis));
  }

  flipBtn.addEventListener("click", () => {
    if (!current) return;
    current.side = current.side === 1 ? -1 : 1;
    applyCut(current);
  });
  clearBtn.addEventListener("click", clearAll);

  updateBar();

  return {
    refreshSelection: updateBar,
    isBusy: () => transform.axis !== null || performance.now() - lastDragEnd < 300,
  };
}
