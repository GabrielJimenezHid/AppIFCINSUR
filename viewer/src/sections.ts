import * as THREE from "three";
import * as OBC from "@thatopen/components";

type Axis = "y" | "x" | "z";

interface Cut {
  axis: Axis;
  label: string;
  vector: THREE.Vector3;
  plane: THREE.Plane;
  active: boolean;
  side: 1 | -1;
  value: number;
}

interface Options {
  world: OBC.SimpleWorld<OBC.SimpleScene, OBC.SimpleCamera, OBC.SimpleRenderer>;
  fragments: OBC.FragmentsManager;
}

export function initSections({ world, fragments }: Options) {
  const panel = document.getElementById("sections-panel") as HTMLElement;
  const toggle = document.getElementById("sections-toggle") as HTMLButtonElement;

  const defs: { axis: Axis; label: string; vector: THREE.Vector3 }[] = [
    { axis: "y", label: "Planta (horizontal)", vector: new THREE.Vector3(0, 1, 0) },
    { axis: "x", label: "Corte vertical X", vector: new THREE.Vector3(1, 0, 0) },
    { axis: "z", label: "Corte vertical Z", vector: new THREE.Vector3(0, 0, 1) },
  ];
  const cuts: Cut[] = defs.map((c) => ({
    ...c,
    plane: new THREE.Plane(),
    active: false,
    side: 1,
    value: 0,
  }));

  const bounds = () => {
    const box = new THREE.Box3();
    for (const model of fragments.list.values()) box.union(model.box);
    return box.isEmpty() ? new THREE.Box3(new THREE.Vector3(-50, -50, -50), new THREE.Vector3(50, 50, 50)) : box;
  };

  function applyCut(cut: Cut) {
    cut.plane.normal.copy(cut.vector).multiplyScalar(-cut.side);
    cut.plane.constant = cut.side * cut.value;
    world.renderer!.setPlane(cut.active, cut.plane);
    world.renderer!.updateClippingPlanes();
    fragments.core.update(true);
  }

  function render() {
    const box = bounds();
    panel.innerHTML = `<h3>Cortes</h3>`;

    for (const cut of cuts) {
      const min = box.min[cut.axis];
      const max = box.max[cut.axis];
      if (cut.value < min || cut.value > max) cut.value = max;

      const row = document.createElement("div");
      row.className = "cut";
      row.innerHTML = `
        <label class="cut-head">
          <input type="checkbox" ${cut.active ? "checked" : ""}/>
          <span>${cut.label}</span>
          <button type="button" title="Invertir el lado que se oculta">⇅</button>
        </label>
        <div class="cut-body">
          <input type="range" min="${min}" max="${max}" step="${(max - min) / 400 || 0.01}" value="${cut.value}"/>
          <output>${cut.value.toFixed(2)}</output>
        </div>`;

      const check = row.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
      const slider = row.querySelector<HTMLInputElement>('input[type="range"]')!;
      const output = row.querySelector("output")!;
      const flip = row.querySelector("button")!;

      check.addEventListener("change", () => {
        cut.active = check.checked;
        applyCut(cut);
      });
      slider.addEventListener("input", () => {
        cut.value = Number(slider.value);
        output.textContent = cut.value.toFixed(2);
        if (!cut.active) {
          cut.active = true;
          check.checked = true;
        }
        applyCut(cut);
      });
      flip.addEventListener("click", (e) => {
        e.preventDefault();
        cut.side = cut.side === 1 ? -1 : 1;
        applyCut(cut);
      });

      panel.appendChild(row);
    }

    const clear = document.createElement("button");
    clear.type = "button";
    clear.textContent = "Quitar cortes";
    clear.addEventListener("click", () => {
      for (const cut of cuts) {
        cut.active = false;
        cut.side = 1;
        applyCut(cut);
      }
      render();
    });
    panel.appendChild(clear);
  }

  toggle.addEventListener("click", () => {
    panel.classList.toggle("open");
    if (panel.classList.contains("open")) render();
  });
}
