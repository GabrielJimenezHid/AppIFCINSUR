import * as THREE from "three";
import * as OBC from "@thatopen/components";

interface Param {
  name: string;
  label: string;
  type: "text" | "file" | "bool";
  default: string | null;
}

interface Manifest {
  id: string;
  name: string;
  description: string;
  parameters: Param[];
  actions: { id: string; label: string }[];
}

interface Column {
  key: string;
  label: string;
  type: string;
  valueFrom?: string | null;
}

interface Row {
  cells: Record<string, unknown>;
  elementIds: string[];
  id?: string | null;
  values?: Record<string, number> | null;
}

interface TableResult {
  title: string;
  summary: string | null;
  groupBy: string | null;
  columns: Column[];
  rows: Row[];
}

interface RowState {
  selected: boolean;
  color: string;
  choice: string;
}

function formatValue(value: number | undefined, choice: string): string {
  if (value === undefined) return "";
  return choice === "ud"
    ? Math.round(value).toLocaleString("es-ES")
    : value.toLocaleString("es-ES", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

interface Options {
  getFile: () => File | null;
  components: OBC.Components;
  fragments: OBC.FragmentsManager;
}

const esc = (s: string) =>
  s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

const $ = <T extends HTMLElement>(id: string) =>
  document.getElementById(id) as T;

function hslToHex(h: number, s: number, l: number): string {
  s /= 100;
  l /= 100;
  const k = (n: number) => (n + h / 30) % 12;
  const a = s * Math.min(l, 1 - l);
  const f = (n: number) =>
    l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
  const hex = (x: number) =>
    Math.round(x * 255)
      .toString(16)
      .padStart(2, "0");
  return `#${hex(f(0))}${hex(f(8))}${hex(f(4))}`;
}

const paletteColor = (i: number) => hslToHex((i * 137.508) % 360, 65, 52);

export function initPlugins({ getFile, components, fragments }: Options) {
  const launcher = $("plugins-panel");
  const openBtn = $<HTMLButtonElement>("plugins-toggle");
  const select = $<HTMLSelectElement>("plugin-select");
  const form = $("plugin-form");
  const status = $("plugin-status");

  const win = $("results-window");
  const winTitle = $("results-title");
  const winSummary = $("results-summary");
  const winBody = $("results-body");

  const hider = components.get(OBC.Hider);
  let manifests: Manifest[] = [];
  let table: TableResult | null = null;
  let states: RowState[] = [];
  const guidCache = new Map<number, OBC.ModelIdMap>();

  openBtn.addEventListener("click", async () => {
    launcher.classList.toggle("open");
    if (launcher.classList.contains("open") && manifests.length === 0) {
      await loadManifests();
    }
  });
  select.addEventListener("change", renderForm);

  $("results-close").addEventListener("click", () => win.classList.remove("open"));
  $("btn-view").addEventListener("click", () => createView());
  $("btn-materials").addEventListener("click", () => colorByMaterial());
  $("btn-reset").addEventListener("click", () => resetView());
  makeDraggable(win, $("results-header"));

  async function loadManifests() {
    try {
      const res = await fetch("/api/plugins");
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      manifests = await res.json();
      select.innerHTML = manifests
        .map((m) => `<option value="${esc(m.id)}">${esc(m.name)}</option>`)
        .join("");
      renderForm();
      status.textContent = manifests.length
        ? ""
        : "No hay plugins instalados en el servidor";
    } catch {
      status.textContent =
        "No se puede conectar con el servidor de plugins (localhost:5080)";
    }
  }

  function renderForm() {
    const manifest = manifests.find((m) => m.id === select.value);
    if (!manifest) {
      form.innerHTML = "";
      return;
    }
    const fields = manifest.parameters
      .map((p) => {
        const id = `param-${p.name}`;
        if (p.type === "bool") {
          const checked = p.default === "true" ? "checked" : "";
          return `<label><input type="checkbox" id="${id}" ${checked}/> ${esc(p.label)}</label>`;
        }
        if (p.type === "file") {
          return `<label>${esc(p.label)}<input type="file" id="${id}"/></label>`;
        }
        return `<label>${esc(p.label)}<input type="text" id="${id}" value="${esc(p.default ?? "")}"/></label>`;
      })
      .join("");
    const buttons = manifest.actions
      .map((a) => `<button data-action="${esc(a.id)}">${esc(a.label)}</button>`)
      .join("");
    form.innerHTML = `${fields}<div class="actions">${buttons}</div>`;
    form
      .querySelectorAll<HTMLButtonElement>("button[data-action]")
      .forEach((b) =>
        b.addEventListener("click", () => run(manifest, b.dataset.action!)),
      );
  }

  async function run(manifest: Manifest, action: string) {
    const file = getFile();
    if (!file) {
      status.textContent = "Abre primero un archivo IFC";
      return;
    }

    const body = new FormData();
    body.append("ifc", file, file.name);
    body.append("action", action);
    for (const p of manifest.parameters) {
      const input = document.getElementById(`param-${p.name}`) as HTMLInputElement;
      if (p.type === "file") {
        if (input.files?.[0]) body.append(p.name, input.files[0]);
      } else if (p.type === "bool") {
        body.append(p.name, String(input.checked));
      } else {
        body.append(p.name, input.value);
      }
    }

    if (table?.rows.some((r) => r.id)) {
      const overrides: Record<string, string> = {};
      table.rows.forEach((row, i) => {
        if (row.id && states[i]?.choice) overrides[row.id] = states[i].choice;
      });
      body.append("overrides", JSON.stringify(overrides));
    }

    status.textContent = "Procesando en el servidor... (puede tardar)";
    setButtonsDisabled(true);
    try {
      const res = await fetch(`/api/plugins/${manifest.id}/run`, {
        method: "POST",
        body,
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: `HTTP ${res.status}` }));
        throw new Error(err.error ?? `HTTP ${res.status}`);
      }
      const type = res.headers.get("content-type") ?? "";
      if (type.includes("application/json")) {
        showResults(await res.json());
        status.textContent = "Resultados abiertos en la ventana de mediciones";
      } else {
        const blob = await res.blob();
        const raw =
          /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(
            res.headers.get("content-disposition") ?? "",
          )?.[1] ?? "resultado";
        const name = decodeURIComponent(raw);
        const a = document.createElement("a");
        a.href = URL.createObjectURL(blob);
        a.download = name;
        a.click();
        URL.revokeObjectURL(a.href);
        status.textContent = `Descargado ${name}`;
      }
    } catch (err) {
      status.textContent = `Error: ${err instanceof Error ? err.message : String(err)}`;
    } finally {
      setButtonsDisabled(false);
    }
  }

  function setButtonsDisabled(disabled: boolean) {
    form.querySelectorAll("button").forEach((b) => (b.disabled = disabled));
  }

  function showResults(result: TableResult) {
    table = result;
    guidCache.clear();
    const selectKey = result.columns.find((c) => c.type === "select")?.key;
    states = result.rows.map((row, i) => ({
      selected: false,
      color: paletteColor(i),
      choice: selectKey ? String(row.cells[selectKey] ?? "") : "",
    }));
    winTitle.textContent = result.title;
    winSummary.textContent = result.summary ?? "";
    renderTable();
    win.classList.add("open");
  }

  function renderTable() {
    if (!table) return;
    const groupKey = table.groupBy;
    const columns = table.columns.filter((c) => c.key !== groupKey);
    const head = `<th></th><th></th>${columns.map((c) => `<th>${esc(c.label)}</th>`).join("")}`;

    const groups = new Map<string, number[]>();
    table.rows.forEach((row, i) => {
      const key = groupKey ? String(row.cells[groupKey] ?? "") : "";
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(i);
    });

    let html = "";
    for (const [key, indices] of groups) {
      if (groupKey) {
        html += `<tr class="group" data-group="${esc(key)}"><td colspan="${columns.length + 2}">
          <input type="checkbox" class="group-sel" data-group="${esc(key)}"/>
          <span class="caret">▾</span> ${esc(key)}
          <span class="count">${indices.length} partidas</span></td></tr>`;
      }
      for (const i of indices) {
        const row = table.rows[i];
        const cells = columns
          .map((c) => {
            if (c.type === "select" && row.values) {
              const options = Object.keys(row.values)
                .map(
                  (o) =>
                    `<option value="${esc(o)}" ${o === states[i].choice ? "selected" : ""}>${esc(o)}</option>`,
                )
                .join("");
              const disabled = Object.keys(row.values).length < 2 ? "disabled" : "";
              return `<td><select class="row-crit" ${disabled}>${options}</select></td>`;
            }
            if (c.valueFrom && row.values) {
              const text = formatValue(row.values[states[i].choice], states[i].choice);
              return `<td class="num" data-value-cell>${esc(text)}</td>`;
            }
            const v = row.cells[c.key];
            const text =
              c.type === "number" && typeof v === "number"
                ? v.toLocaleString("es-ES", { maximumFractionDigits: 3 })
                : String(v ?? "");
            return `<td class="${c.type === "number" ? "num" : ""}">${esc(text)}</td>`;
          })
          .join("");
        html += `<tr class="item" data-row="${i}" data-g="${esc(key)}">
          <td><input type="checkbox" class="row-sel"/></td>
          <td><input type="color" class="row-color" value="${states[i].color}"/></td>
          ${cells}</tr>`;
      }
    }
    winBody.innerHTML = `<table><thead><tr>${head}</tr></thead><tbody>${html}</tbody></table>`;
  }

  winBody.addEventListener("click", async (event) => {
    const target = event.target as HTMLElement;
    const tr = target.closest("tr");
    if (!tr || !table) return;

    if (tr.classList.contains("group")) {
      if (target.classList.contains("group-sel")) return;
      const key = tr.dataset.group!;
      const collapsed = tr.classList.toggle("collapsed");
      tr.querySelector(".caret")!.textContent = collapsed ? "▸" : "▾";
      winBody
        .querySelectorAll<HTMLElement>("tr.item")
        .forEach((r) => {
          if (r.dataset.g === key) r.classList.toggle("hidden", collapsed);
        });
      return;
    }

    if (target.tagName === "INPUT" || target.tagName === "SELECT" || target.tagName === "OPTION") return;
    winBody.querySelector("tr.selected")?.classList.remove("selected");
    tr.classList.add("selected");
    await isolateRows([Number(tr.dataset.row)]);
  });

  winBody.addEventListener("change", (event) => {
    const target = event.target as HTMLInputElement;
    const tr = target.closest("tr");
    if (!tr) return;

    if (target.classList.contains("row-crit")) {
      const index = Number(tr.dataset.row);
      states[index].choice = target.value;
      const cell = tr.querySelector("[data-value-cell]");
      if (cell && table) {
        cell.textContent = formatValue(table.rows[index].values?.[target.value], target.value);
      }
    } else if (target.classList.contains("row-sel")) {
      states[Number(tr.dataset.row)].selected = target.checked;
    } else if (target.classList.contains("row-color")) {
      states[Number(tr.dataset.row)].color = target.value;
    } else if (target.classList.contains("group-sel")) {
      const key = tr.dataset.group!;
      winBody.querySelectorAll<HTMLTableRowElement>("tr.item").forEach((r) => {
        if (r.dataset.g !== key) return;
        states[Number(r.dataset.row)].selected = target.checked;
        r.querySelector<HTMLInputElement>(".row-sel")!.checked = target.checked;
      });
    }
  });

  async function mapForRows(indices: number[]): Promise<OBC.ModelIdMap> {
    const merged: OBC.ModelIdMap = {};
    for (const i of indices) {
      let map = guidCache.get(i);
      if (!map) {
        map = {};
        const guids = table!.rows[i].elementIds;
        for (const model of fragments.list.values()) {
          const ids = (await model.getLocalIdsByGuids(guids)).filter(
            (id): id is number => id !== null,
          );
          if (ids.length) map[model.modelId] = new Set(ids);
        }
        guidCache.set(i, map);
      }
      for (const [modelId, ids] of Object.entries(map)) {
        merged[modelId] ??= new Set();
        ids.forEach((id) => merged[modelId].add(id));
      }
    }
    return merged;
  }

  async function isolateRows(indices: number[]) {
    await hider.isolate(await mapForRows(indices));
  }

  async function resetView() {
    await fragments.resetHighlight();
    await hider.set(true);
    fragments.core.update(true);
  }

  async function paintRows(entries: { index: number; color: string }[]) {
    if (!table || entries.length === 0) {
      winSummary.textContent = "Selecciona alguna partida (casilla de la izquierda)";
      return;
    }
    await resetView();

    const dim: OBC.ModelIdMap = {};
    for (const model of fragments.list.values()) {
      dim[model.modelId] = new Set(await model.getItemsIdsWithGeometry());
    }
    await fragments.highlight(
      {
        color: new THREE.Color("#8a8d94"),
        renderedFaces: 0,
        opacity: 0.12,
        transparent: true,
      },
      dim,
    );

    for (const { index, color } of entries) {
      const map = await mapForRows([index]);
      if (Object.keys(map).length === 0) continue;
      await fragments.highlight(
        {
          color: new THREE.Color(color),
          renderedFaces: 0,
          opacity: 1,
          transparent: false,
        },
        map,
      );
    }
    fragments.core.update(true);
  }

  async function createView() {
    const entries = states
      .map((s, index) => ({ index, color: s.color, selected: s.selected }))
      .filter((e) => e.selected);
    await paintRows(entries);
  }

  async function colorByMaterial() {
    if (!table) return;
    const colorByCode = new Map<string, string>();
    const entries: { index: number; color: string }[] = [];
    table.rows.forEach((row, index) => {
      if (!String(row.cells.categoria ?? "").endsWith("-Material")) return;
      const code = String(row.cells.codigo ?? "");
      if (!colorByCode.has(code)) colorByCode.set(code, paletteColor(colorByCode.size));
      entries.push({ index, color: colorByCode.get(code)! });
    });

    winBody.querySelectorAll<HTMLTableRowElement>("tr.item").forEach((tr) => {
      const index = Number(tr.dataset.row);
      const entry = entries.find((e) => e.index === index);
      states[index].selected = !!entry;
      if (entry) states[index].color = entry.color;
      tr.querySelector<HTMLInputElement>(".row-sel")!.checked = !!entry;
      tr.querySelector<HTMLInputElement>(".row-color")!.value = states[index].color;
    });
    await paintRows(entries);
  }
}

function makeDraggable(win: HTMLElement, handle: HTMLElement) {
  handle.addEventListener("mousedown", (down) => {
    if ((down.target as HTMLElement).closest("button")) return;
    const rect = win.getBoundingClientRect();
    const dx = down.clientX - rect.left;
    const dy = down.clientY - rect.top;
    const move = (e: MouseEvent) => {
      win.style.left = `${Math.max(0, e.clientX - dx)}px`;
      win.style.top = `${Math.max(0, e.clientY - dy)}px`;
      win.style.right = "auto";
    };
    const up = () => {
      window.removeEventListener("mousemove", move);
      window.removeEventListener("mouseup", up);
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
  });
}
