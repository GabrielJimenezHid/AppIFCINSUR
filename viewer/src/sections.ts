import * as THREE from "three";
import * as OBC from "@thatopen/components";

type Axis = "y" | "x" | "z";

interface Cut {
  axis: Axis;
  vector: THREE.Vector3;
  plane: THREE.Plane;
  active: boolean;
}

interface Options {
  world: OBC.SimpleWorld<OBC.SimpleScene, OBC.SimpleCamera, OBC.SimpleRenderer>;
  fragments: OBC.FragmentsManager;
  container: HTMLElement;
}

const ENTRIES: { axis: Axis; side: 1 | -1; label: string }[] = [
  { axis: "y", side: 1, label: "Planta (Y): ocultar lo de arriba" },
  { axis: "y", side: -1, label: "Planta (Y): ocultar lo de abajo" },
  { axis: "x", side: 1, label: "Corte X: ocultar lado +X" },
  { axis: "x", side: -1, label: "Corte X: ocultar lado −X" },
  { axis: "z", side: 1, label: "Corte Z: ocultar lado +Z" },
  { axis: "z", side: -1, label: "Corte Z: ocultar lado −Z" },
];

export function initSections({ world, fragments, container }: Options) {
  const clearBtn = document.getElementById("sections-clear") as HTMLButtonElement;

  const cuts: Record<Axis, Cut> = {
    y: { axis: "y", vector: new THREE.Vector3(0, 1, 0), plane: new THREE.Plane(), active: false },
    x: { axis: "x", vector: new THREE.Vector3(1, 0, 0), plane: new THREE.Plane(), active: false },
    z: { axis: "z", vector: new THREE.Vector3(0, 0, 1), plane: new THREE.Plane(), active: false },
  };

  const menu = document.createElement("div");
  menu.id = "ctx-menu";
  document.body.appendChild(menu);

  const anyActive = () => Object.values(cuts).some((c) => c.active);

  function refresh() {
    world.renderer!.updateClippingPlanes();
    fragments.core.update(true);
    clearBtn.hidden = !anyActive();
  }

  function setCut(axis: Axis, side: 1 | -1, value: number) {
    const cut = cuts[axis];
    cut.plane.normal.copy(cut.vector).multiplyScalar(-side);
    cut.plane.constant = side * value;
    cut.active = true;
    world.renderer!.setPlane(true, cut.plane);
    refresh();
  }

  function clearCuts() {
    for (const cut of Object.values(cuts)) {
      cut.active = false;
      world.renderer!.setPlane(false, cut.plane);
    }
    refresh();
  }

  function closeMenu() {
    menu.classList.remove("open");
  }

  function openMenu(x: number, y: number, point: THREE.Vector3 | null) {
    menu.innerHTML = "";

    if (point) {
      const title = document.createElement("div");
      title.className = "title";
      title.textContent = "Cortar por el punto seleccionado";
      menu.appendChild(title);

      for (const entry of ENTRIES) {
        const item = document.createElement("button");
        item.textContent = entry.label;
        item.addEventListener("click", () => {
          setCut(entry.axis, entry.side, point[entry.axis]);
          closeMenu();
        });
        menu.appendChild(item);
      }
    }

    if (anyActive()) {
      const clear = document.createElement("button");
      clear.textContent = "Quitar todos los cortes";
      clear.addEventListener("click", () => {
        clearCuts();
        closeMenu();
      });
      menu.appendChild(clear);
    }

    if (!menu.childElementCount) return;

    menu.classList.add("open");
    const { innerWidth, innerHeight } = window;
    menu.style.left = `${Math.min(x, innerWidth - menu.offsetWidth - 8)}px`;
    menu.style.top = `${Math.min(y, innerHeight - menu.offsetHeight - 8)}px`;
  }

  clearBtn.addEventListener("click", clearCuts);

  let downAt: { x: number; y: number } | null = null;
  container.addEventListener("mousedown", (e) => {
    if (e.button === 2) downAt = { x: e.clientX, y: e.clientY };
  });

  container.addEventListener("contextmenu", async (event) => {
    event.preventDefault();
    const moved =
      downAt !== null &&
      Math.hypot(event.clientX - downAt.x, event.clientY - downAt.y) > 5;
    downAt = null;
    if (moved) return;

    const mouse = new THREE.Vector2(event.clientX, event.clientY);
    const dom = world.renderer!.three.domElement;
    let point: THREE.Vector3 | null = null;
    for (const model of fragments.list.values()) {
      const hit = await model.raycast({ camera: world.camera.three, mouse, dom });
      if (hit) {
        point = hit.point;
        break;
      }
    }
    openMenu(event.clientX, event.clientY, point);
  });

  window.addEventListener("mousedown", (e) => {
    if (!menu.contains(e.target as Node)) closeMenu();
  });
  window.addEventListener("keydown", (e) => {
    if (e.key === "Escape") closeMenu();
  });
  window.addEventListener("wheel", closeMenu, { passive: true });
}
