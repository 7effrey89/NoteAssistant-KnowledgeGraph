import Graph from "https://esm.sh/graphology@0.26.0";
import Sigma from "https://esm.sh/sigma@3.0.3";
import { EdgeCurvedArrowProgram } from "https://esm.sh/@sigma/edge-curve@3.1.0?deps=sigma@3.0.3";
import { circlepack, circular, random } from "https://esm.sh/graphology-layout@0.6.1";
import forceAtlas2 from "https://esm.sh/graphology-layout-forceatlas2@0.10.1";
import ForceLayout from "https://esm.sh/graphology-layout-force@0.2.4/worker";
import ForceAtlas2Layout from "https://esm.sh/graphology-layout-forceatlas2@0.10.1/worker";
import NoverlapLayout from "https://esm.sh/graphology-layout-noverlap@0.4.2/worker";

const backendBaseUrl = window.noteAssistantGraphExplorer?.backendBaseUrl || "http://localhost:5070";
const palette = ["#2563eb", "#16a34a", "#dc2626", "#9333ea", "#0891b2", "#ca8a04", "#db2777", "#4f46e5", "#0f766e", "#ea580c"];
const inspectorWidthStorageKey = "noteAssistantGraphExplorer.inspectorWidth";
const inspectorMinWidth = 260;
const inspectorMaxWidth = 640;
const state = {
  graphName: "knowledge_graph",
  graph: null,
  renderer: null,
  rawNodes: [],
  rawEdges: [],
  degreeById: new Map(),
  colorByLabel: new Map(),
  selectedNode: null,
  selectedEdge: null,
  hoveredNode: null,
  inspectorHighlightedNode: null,
  filters: { search: "", nodeTypes: [], edgeTypes: [], minDegree: 0 },
  settings: { showLabels: true, showEdgeLabels: true, nodeNamesInside: false, curvedEdges: true, highlightNeighborhood: true, sizeMode: "degree", labelDensity: 50 },
  currentLayout: "random",
  currentWorkerLayout: null,
  lastWorkerLayout: null,
  layoutWorker: null,
  lastPayload: null,
  history: [],
  historyIndex: -1,
  isRestoringHistory: false,
  isQueryRunning: false,
  latestQueryRequestId: 0,
  nodeDetailsCache: new Map(),
  latestNodeDetailsRequestId: 0
};

const el = {
  backendStatus: document.getElementById("kgBackendStatus"),
  graphStatus: document.getElementById("kgGraphStatus"),
  graphNameInput: document.getElementById("kgGraphNameInput"),
  queryInput: document.getElementById("kgQueryInput"),
  runQueryBtn: document.getElementById("kgRunQueryBtn"),
  loadSampleBtn: document.getElementById("kgLoadSampleBtn"),
  exportBtn: document.getElementById("kgExportBtn"),
  refreshStatusBtn: document.getElementById("kgRefreshStatusBtn"),
  queryMessage: document.getElementById("kgQueryMessage"),
  searchInput: document.getElementById("kgSearchInput"),
  nodeTypeFilter: document.getElementById("kgNodeTypeFilter"),
  nodeTypeFilterSummary: document.getElementById("kgNodeTypeFilterSummary"),
  nodeTypeFilterList: document.getElementById("kgNodeTypeFilterList"),
  edgeTypeFilter: document.getElementById("kgEdgeTypeFilter"),
  edgeTypeFilterSummary: document.getElementById("kgEdgeTypeFilterSummary"),
  edgeTypeFilterList: document.getElementById("kgEdgeTypeFilterList"),
  minDegreeInput: document.getElementById("kgMinDegreeInput"),
  sizeModeSelect: document.getElementById("kgSizeModeSelect"),
  labelsToggle: document.getElementById("kgLabelsToggle"),
  nodeNamesInsideToggle: document.getElementById("kgNodeNamesInsideToggle"),
  labelDensitySlider: document.getElementById("kgLabelDensitySlider"),
  edgeLabelsToggle: document.getElementById("kgEdgeLabelsToggle"),
  curvedEdgesToggle: document.getElementById("kgCurvedEdgesToggle"),
  neighborToggle: document.getElementById("kgNeighborToggle"),
  searchResults: document.getElementById("kgSearchResults"),
  layoutStatus: document.getElementById("kgLayoutStatus"),
  layoutStatusText: document.getElementById("kgLayoutStatusText"),
  workerLayoutToggleBtn: document.getElementById("kgWorkerLayoutToggleBtn"),
  historyBackBtn: document.getElementById("kgHistoryBackBtn"),
  historyForwardBtn: document.getElementById("kgHistoryForwardBtn"),
  canvasZoomInBtn: document.getElementById("kgCanvasZoomInBtn"),
  canvasZoomOutBtn: document.getElementById("kgCanvasZoomOutBtn"),
  canvasFitBtn: document.getElementById("kgCanvasFitBtn"),
  canvasCenterBtn: document.getElementById("kgCanvasCenterBtn"),
  stats: document.getElementById("kgStats"),
  legend: document.getElementById("kgLegend"),
  explorerLayout: document.querySelector(".kg-explorer-layout"),
  sigmaContainer: document.getElementById("kgSigmaContainer"),
  emptyState: document.getElementById("kgEmptyState"),
  inspectorPanel: document.getElementById("kgInspectorPanel"),
  inspectorResizeHandle: document.getElementById("kgInspectorResizeHandle"),
  inspectorContent: document.getElementById("kgInspectorContent")
};

el.runQueryBtn.addEventListener("click", runQuery);
el.loadSampleBtn.addEventListener("click", () => {
  el.queryInput.value = "MATCH p=(n)-[r]->(m)\nRETURN n, r, m\nLIMIT 150";
  autosizeQueryInput();
});
el.exportBtn.addEventListener("click", exportGraphJson);
el.refreshStatusBtn.addEventListener("click", refreshStatus);
el.queryInput.addEventListener("input", autosizeQueryInput);
el.searchInput.addEventListener("input", () => updateFilters({ search: el.searchInput.value.trim() }));
el.minDegreeInput.addEventListener("input", () => updateFilters({ minDegree: Number(el.minDegreeInput.value) || 0 }));
el.sizeModeSelect.addEventListener("change", () => {
  state.settings.sizeMode = el.sizeModeSelect.value;
  rebuildGraph();
});
el.labelsToggle.addEventListener("change", () => {
  state.settings.showLabels = el.labelsToggle.checked;
  updateRendererSettings();
});
el.nodeNamesInsideToggle.addEventListener("change", () => {
  state.settings.nodeNamesInside = el.nodeNamesInsideToggle.checked;
  updateRendererSettings();
});
if (el.labelDensitySlider) {
  el.labelDensitySlider.addEventListener("input", () => {
    state.settings.labelDensity = Number(el.labelDensitySlider.value) || 50;
    refreshRenderer();
  });
}
el.edgeLabelsToggle.addEventListener("change", () => {
  state.settings.showEdgeLabels = el.edgeLabelsToggle.checked;
  updateRendererSettings();
});
el.curvedEdgesToggle.addEventListener("change", () => {
  state.settings.curvedEdges = el.curvedEdgesToggle.checked;
  updateEdgeShapeAttributes();
  updateRendererSettings();
});
el.neighborToggle.addEventListener("change", () => {
  state.settings.highlightNeighborhood = el.neighborToggle.checked;
  refreshRenderer();
});
el.historyBackBtn.addEventListener("click", () => navigateGraphHistory(-1));
el.historyForwardBtn.addEventListener("click", () => navigateGraphHistory(1));
el.canvasZoomInBtn.addEventListener("click", () => zoomBy(0.75));
el.canvasZoomOutBtn.addEventListener("click", () => zoomBy(1.35));
el.canvasFitBtn.addEventListener("click", fitGraph);
el.canvasCenterBtn.addEventListener("click", centerSelectedOrFit);
el.workerLayoutToggleBtn?.addEventListener("click", toggleCurrentWorkerLayout);
document.querySelectorAll("[data-kg-layout]").forEach(button => {
  button.addEventListener("click", () => setLayout(button.dataset.kgLayout || "random"));
});
document.querySelectorAll("[data-kg-worker-layout]").forEach(button => {
  button.addEventListener("click", () => toggleWorkerLayout(button.dataset.kgWorkerLayout || "force"));
});
document.addEventListener("click", event => {
  [el.nodeTypeFilter, el.edgeTypeFilter].forEach(dropdown => {
    if (dropdown?.open && !dropdown.contains(event.target)) dropdown.open = false;
  });
});
initInspectorResize();
autosizeQueryInput();
updateLayoutStatus();

await refreshStatus();

function autosizeQueryInput() {
  el.queryInput.style.height = "auto";
  el.queryInput.style.height = `${el.queryInput.scrollHeight}px`;
}

function initInspectorResize() {
  if (!el.explorerLayout || !el.inspectorPanel || !el.inspectorResizeHandle) return;

  const storedWidth = Number(localStorage.getItem(inspectorWidthStorageKey));
  if (Number.isFinite(storedWidth) && storedWidth > 0) {
    setInspectorWidth(storedWidth, { persist: false, refresh: false });
  }

  let dragStartX = 0;
  let dragStartWidth = 0;

  el.inspectorResizeHandle.addEventListener("pointerdown", event => {
    if (event.button !== 0) return;
    event.preventDefault();
    dragStartX = event.clientX;
    dragStartWidth = getInspectorWidth();
    el.inspectorResizeHandle.classList.add("is-dragging");
    document.body.classList.add("kg-inspector-resizing");
    el.inspectorResizeHandle.setPointerCapture(event.pointerId);
  });

  el.inspectorResizeHandle.addEventListener("pointermove", event => {
    if (!el.inspectorResizeHandle.classList.contains("is-dragging")) return;
    const delta = dragStartX - event.clientX;
    setInspectorWidth(dragStartWidth + delta, { persist: false });
  });

  const finishDrag = event => {
    if (!el.inspectorResizeHandle.classList.contains("is-dragging")) return;
    el.inspectorResizeHandle.classList.remove("is-dragging");
    document.body.classList.remove("kg-inspector-resizing");
    if (el.inspectorResizeHandle.hasPointerCapture(event.pointerId)) {
      el.inspectorResizeHandle.releasePointerCapture(event.pointerId);
    }
    setInspectorWidth(getInspectorWidth(), { persist: true });
  };

  el.inspectorResizeHandle.addEventListener("pointerup", finishDrag);
  el.inspectorResizeHandle.addEventListener("pointercancel", finishDrag);

  el.inspectorResizeHandle.addEventListener("keydown", event => {
    if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const step = event.shiftKey ? 50 : 20;
    if (event.key === "ArrowLeft") setInspectorWidth(getInspectorWidth() + step);
    if (event.key === "ArrowRight") setInspectorWidth(getInspectorWidth() - step);
    if (event.key === "Home") setInspectorWidth(inspectorMinWidth);
    if (event.key === "End") setInspectorWidth(inspectorMaxWidth);
  });
}

function getInspectorWidth() {
  return el.inspectorPanel?.getBoundingClientRect().width || 320;
}

function setInspectorWidth(width, options = {}) {
  if (!el.explorerLayout) return;
  const viewportLimit = Math.max(inspectorMinWidth, Math.min(inspectorMaxWidth, Math.round(window.innerWidth * 0.45)));
  const nextWidth = Math.round(Math.min(Math.max(width, inspectorMinWidth), viewportLimit));
  el.explorerLayout.style.setProperty("--kg-inspector-width", `${nextWidth}px`);
  el.inspectorResizeHandle?.setAttribute("aria-valuemin", String(inspectorMinWidth));
  el.inspectorResizeHandle?.setAttribute("aria-valuemax", String(viewportLimit));
  el.inspectorResizeHandle?.setAttribute("aria-valuenow", String(nextWidth));
  if (options.persist !== false) localStorage.setItem(inspectorWidthStorageKey, String(nextWidth));
  if (options.refresh !== false) requestAnimationFrame(() => {
    state.renderer?.resize?.();
    refreshRenderer();
  });
}

async function refreshStatus() {
  setStatus(el.backendStatus, "checking", "Checking backend");
  try {
    const health = await fetch(`${backendBaseUrl}/api/health`);
    setStatus(el.backendStatus, health.ok ? "online" : "warning", health.ok ? "Backend online" : `Backend ${health.status}`);
  } catch (error) {
    setStatus(el.backendStatus, "offline", `Backend offline: ${error.message}`);
  }

  try {
    const info = await fetch(`${backendBaseUrl}/api/graph/info`);
    if (info.ok) {
      const payload = await info.json();
      if (payload?.graphName) {
        state.graphName = payload.graphName;
        el.graphNameInput.value = payload.graphName;
      }
    }
  } catch {
  }

  try {
    const check = await fetch(`${backendBaseUrl}/api/graph/check`);
    const payload = await check.json().catch(() => null);
    if (check.ok) {
      const exists = payload.graphExists ? "exists" : "missing";
      setStatus(el.graphStatus, payload.graphExists ? "online" : "warning", `${payload.graphName}: ${exists} (${payload.entities} entities)`);
    } else {
      setStatus(el.graphStatus, "warning", payload?.detail || "Graph check unavailable");
    }
  } catch (error) {
    setStatus(el.graphStatus, "offline", `Graph check failed: ${error.message}`);
  }
}

async function runQuery() {
  if (state.isQueryRunning) {
    setQueryMessage("Query already running...", "muted");
    return;
  }

  const cypher = el.queryInput.value.trim();
  state.graphName = el.graphNameInput.value.trim() || "knowledge_graph";
  if (!cypher) {
    setQueryMessage("Cypher query is required.", "error");
    return;
  }

  const requestId = ++state.latestQueryRequestId;
  state.isQueryRunning = true;
  setQueryMessage("Running query...", "muted");
  el.runQueryBtn.disabled = true;
  try {
    const response = await fetch(`${backendBaseUrl}/api/query`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ cypher, graphName: state.graphName })
    });
    const payload = await response.json().catch(() => null);
    if (requestId !== state.latestQueryRequestId) {
      return;
    }

    if (!response.ok) {
      setQueryMessage(payload?.error || payload?.detail || `Query failed with status ${response.status}.`, "error");
      loadGraph([], []);
      return;
    }

    state.lastPayload = payload;
    loadGraph(payload.nodes || [], payload.edges || []);
    setQueryMessage(`Rendered ${(payload.nodes || []).length} nodes and ${(payload.edges || []).length} relationships.`, "success");
    el.exportBtn.disabled = false;
  } catch (error) {
    if (requestId !== state.latestQueryRequestId) {
      return;
    }
    setQueryMessage(`Query failed: ${error.message}`, "error");
  } finally {
    if (requestId === state.latestQueryRequestId) {
      state.isQueryRunning = false;
      el.runQueryBtn.disabled = false;
    }
  }
}

function loadGraph(nodes, edges) {
  state.rawNodes = nodes.map(normalizeNode);
  const nodeIds = new Set(state.rawNodes.map(node => node.id));
  state.rawEdges = edges.map(normalizeEdge).filter(edge => nodeIds.has(edge.source) && nodeIds.has(edge.target));
  state.degreeById = calculateDegrees(state.rawNodes, state.rawEdges);
  state.colorByLabel = buildColorMap(state.rawNodes);
  state.selectedNode = null;
  state.selectedEdge = null;
  state.inspectorHighlightedNode = null;
  state.history = [];
  state.historyIndex = -1;
  state.nodeDetailsCache = new Map();
  rebuildFilterOptions();
  rebuildGraph();
  renderInspector(null);
  updateHistoryButtons();
}

function rebuildGraph() {
  stopWorkerLayout({ clearLast: true });
  if (state.renderer) {
    state.renderer.kill();
    state.renderer = null;
  }

  // Ensure previous Sigma canvases are removed before constructing a new renderer.
  if (el.sigmaContainer) {
    el.sigmaContainer.innerHTML = "";
  }

  const graph = new Graph({ type: "directed", multi: true, allowSelfLoops: true });
  state.rawNodes.forEach(node => graph.addNode(node.id, buildNodeAttributes(node)));
  state.rawEdges.forEach(edge => graph.addDirectedEdgeWithKey(edge.id, edge.source, edge.target, buildEdgeAttributes(edge)));

  state.graph = graph;
  applyLayout(state.currentLayout, false);
  if (!graph.order) {
    el.emptyState.hidden = false;
    el.exportBtn.disabled = true;
    renderStats();
    renderLegend();
    renderSearchResults([]);
    return;
  }

  el.emptyState.hidden = true;
  state.renderer = new Sigma(graph, el.sigmaContainer, buildSigmaSettings());
  registerGraphEvents();
  updateReducers();
  renderLegend();
  renderStats();
  updateSearchResults();
  setTimeout(() => {
    state.renderer?.getCamera().animatedReset({ duration: 250 });
    setTimeout(() => recordGraphHistory({ replace: true }), 280);
  }, 0);
}

function normalizeNode(node) {
  const id = String(node.id ?? node.title ?? crypto.randomUUID());
  const label = node.label || "Node";
  const title = node.title || node.properties?.name || node.properties?.title || id;
  return { id, label, title, properties: node.properties || {} };
}

function normalizeEdge(edge, index) {
  const source = String(edge.source);
  const target = String(edge.target);
  return { id: edge.id ? String(edge.id) : `${source}->${target}:${edge.label || "RELATED"}:${index}`, source, target, label: edge.label || "RELATED", properties: edge.properties || {} };
}

function buildNodeAttributes(node) {
  const degree = state.degreeById.get(node.id) || 0;
  const baseSizeInside = state.settings.nodeNamesInside ? 14 : 7;
  const maxSizeInside = state.settings.nodeNamesInside ? 28 : 18;
  const size = state.settings.sizeMode === "degree" ? baseSizeInside + Math.min(maxSizeInside - baseSizeInside, degree * 2.4) : baseSizeInside + 3;
  return { x: 0, y: 0, size, label: node.title, color: state.colorByLabel.get(node.label) || palette[0], kgLabel: node.label, title: node.title, properties: node.properties, degree };
}

function buildEdgeAttributes(edge) {
  return { size: 1.3, type: getEdgeType(), label: edge.label, color: "#94a3b8", relationship: edge.label, properties: edge.properties };
}

function buildSigmaSettings() {
  const settings = {
    allowInvalidContainer: true,
    renderLabels: state.settings.showLabels,
    renderEdgeLabels: state.settings.showEdgeLabels,
    defaultDrawNodeLabel: renderAdaptiveNodeLabel,
    labelRenderer: renderAdaptiveNodeLabel,
    labelRenderedSizeThreshold: 0,
    labelSize: 12,
    edgeLabelSize: 10,
    minCameraRatio: 0.03,
    maxCameraRatio: 8,
    defaultEdgeType: getEdgeType(),
    edgeProgramClasses: { curvedArrow: EdgeCurvedArrowProgram },
    labelColor: { color: "#1f2937" },
    edgeLabelColor: { color: "#475569" }
  };

  return settings;
}

function getEdgeType() {
  return state.settings.curvedEdges ? "curvedArrow" : "arrow";
}

function updateEdgeShapeAttributes() {
  if (!state.graph) return;
  const edgeType = getEdgeType();
  state.graph.forEachEdge(edge => state.graph.setEdgeAttribute(edge, "type", edgeType));
}

function stripEntityTypePrefix(label) {
  if (!label) return label;
  const prefixes = ["Concept:", "Person:", "Product:", "Technology:", "Organization:", "Document:", "Platform:"];
  let text = String(label).trim();
  for (const prefix of prefixes) {
    if (text.startsWith(prefix)) {
      text = text.slice(prefix.length).trim();
      break;
    }
  }
  return text;
}

function abbreviateLabel(label, density) {
  if (!label) return label;
  const text = String(label).trim();
  const maxChars = Math.round(10 + (density / 100) * 10);
  if (text.length <= maxChars) return text;
  return text.slice(0, maxChars - 1) + "…";
}

function fitCanvasTextMultiline(context, text, maxWidth, maxLines = 2) {
  const value = String(text ?? "").trim();
  const lines = [];
  const words = value.split(/\s+/);
  let currentLine = "";

  for (const word of words) {
    const testLine = currentLine ? `${currentLine} ${word}` : word;
    const metrics = context.measureText(testLine);
    if (metrics.width <= maxWidth) {
      currentLine = testLine;
    } else {
      if (currentLine) lines.push(currentLine);
      currentLine = word;
      if (lines.length >= maxLines - 1) {
        break;
      }
    }
  }

  if (currentLine) {
    if (lines.length < maxLines) {
      lines.push(currentLine);
    } else {
      let lastLine = lines[lines.length - 1];
      while (lastLine.length > 1 && context.measureText(`${lastLine}…`).width > maxWidth) {
        lastLine = lastLine.slice(0, -1);
      }
      lines[lines.length - 1] = lastLine;
      if (lines[lines.length - 1].length > 1) {
        lines[lines.length - 1] += "…";
      }
    }
  }

  return lines;
}

function renderAdaptiveNodeLabel(context, data) {
  if (!data.label) return;

  const radius = Math.max(6, data.size || 0);
  const density = state.settings.labelDensity || 50;
  const displayLabel = stripEntityTypePrefix(data.label);
  const TINY_THRESHOLD = 12;
  const shouldForceOutside = state.settings.nodeNamesInside && radius < TINY_THRESHOLD;

  context.save();

  if (state.settings.nodeNamesInside && !shouldForceOutside) {
    const maxWidth = Math.max(10, radius * 1.72);
    const baseFontSize = radius * 0.66;
    const densityMultiplier = 0.8 + (density / 100) * 0.4;
    const fontSize = Math.max(7, Math.min(12, baseFontSize * densityMultiplier));
    
    context.font = `800 ${fontSize}px system-ui, -apple-system, Segoe UI, sans-serif`;
    context.textAlign = "center";
    context.textBaseline = "middle";
    context.fillStyle = "#ffffff";
    context.strokeStyle = "rgba(15, 23, 42, 0.55)";
    context.lineWidth = Math.max(2, fontSize * 0.22);

    const lines = fitCanvasTextMultiline(context, displayLabel, maxWidth, 2);
    const lineHeight = fontSize * 1.2;
    const totalHeight = lines.length * lineHeight;
    const startY = data.y - totalHeight / 2 + fontSize / 2;

    lines.forEach((line, idx) => {
      const y = startY + idx * lineHeight;
      context.strokeText(line, data.x, y);
      context.fillText(line, data.x, y);
    });
  } else {
    const maxWidth = 180;
    const baseFontSize = radius * 0.9;
    const densityMultiplier = 0.8 + (density / 100) * 0.4;
    const fontSize = Math.max(10, Math.min(13, baseFontSize * densityMultiplier));
    
    context.font = `600 ${fontSize}px system-ui, -apple-system, Segoe UI, sans-serif`;
    context.textAlign = "center";
    context.textBaseline = "alphabetic";
    context.fillStyle = "#1f2937";
    context.strokeStyle = "rgba(255, 255, 255, 0.88)";
    context.lineWidth = Math.max(2, fontSize * 0.24);

    const abbreviated = abbreviateLabel(displayLabel, density);
    const text = abbreviated.length > maxWidth / 5 && context.measureText(abbreviated).width > maxWidth 
      ? abbreviated.slice(0, Math.floor(maxWidth / 5)) + "…" 
      : abbreviated;
    
    const y = data.y + radius + fontSize + 2;
    context.strokeText(text, data.x, y);
    context.fillText(text, data.x, y);
  }

  context.restore();
}

function fitCanvasText(context, text, maxWidth) {
  const lines = fitCanvasTextMultiline(context, text, maxWidth, 1);
  return lines[0] || "";
}

function registerGraphEvents() {
  const renderer = state.renderer;
  if (!renderer) return;

  renderer.on("clickNode", ({ node }) => {
    selectNode(node, { center: false });
  });
  renderer.on("clickEdge", ({ edge }) => {
    state.selectedEdge = edge;
    state.selectedNode = null;
    renderInspector({ type: "edge", id: edge });
    refreshRenderer();
    recordGraphHistory();
  });
  renderer.on("clickStage", () => {
    state.selectedNode = null;
    state.selectedEdge = null;
    renderInspector(null);
    refreshRenderer();
    recordGraphHistory();
  });
  renderer.on("enterNode", ({ node }) => {
    state.hoveredNode = node;
    el.sigmaContainer.classList.add("kg-grabbing-ready");
    refreshRenderer();
  });
  renderer.on("leaveNode", () => {
    state.hoveredNode = null;
    el.sigmaContainer.classList.remove("kg-grabbing-ready");
    refreshRenderer();
  });
  enableNodeDragging(renderer);
}

function enableNodeDragging(renderer) {
  let draggedNode = null;
  let isDragging = false;
  renderer.on("downNode", event => {
    isDragging = true;
    draggedNode = event.node;
    state.selectedNode = event.node;
    state.selectedEdge = null;
    el.sigmaContainer.classList.add("kg-dragging");
    renderer.getGraph().setNodeAttribute(draggedNode, "highlighted", true);
    renderInspector({ type: "node", id: draggedNode });
  });
  renderer.getMouseCaptor().on("mousemovebody", event => {
    if (!isDragging || !draggedNode) return;
    const position = renderer.viewportToGraph(event);
    renderer.getGraph().setNodeAttribute(draggedNode, "x", position.x);
    renderer.getGraph().setNodeAttribute(draggedNode, "y", position.y);
    event.preventSigmaDefault();
    event.original.preventDefault();
    event.original.stopPropagation();
  });
  renderer.getMouseCaptor().on("mouseup", () => {
    const movedNode = draggedNode;
    if (draggedNode) renderer.getGraph().removeNodeAttribute(draggedNode, "highlighted");
    isDragging = false;
    draggedNode = null;
    el.sigmaContainer.classList.remove("kg-dragging");
    if (movedNode) setTimeout(() => recordGraphHistory(), 0);
  });
}

function updateFilters(partial) {
  state.filters = { ...state.filters, ...partial };
  updateReducers();
  updateSearchResults();
  renderStats();
}

function updateRendererSettings() {
  if (!state.renderer) return;
  state.renderer.setSetting("renderLabels", state.settings.showLabels);
  state.renderer.setSetting("renderEdgeLabels", state.settings.showEdgeLabels);
  state.renderer.setSetting("defaultEdgeType", getEdgeType());
  updateReducers();
}

function updateReducers() {
  if (!state.renderer || !state.graph) return;
  state.renderer.setSetting("nodeReducer", nodeReducer);
  state.renderer.setSetting("edgeReducer", edgeReducer);
  refreshRenderer();
}

function nodeReducer(node, data) {
  const reduced = { ...data };
  if (isNodeHidden(node, data)) {
    reduced.hidden = true;
    return reduced;
  }
  const isSelected = state.selectedNode === node;
  const isHovered = state.hoveredNode === node;
  const anchor = state.hoveredNode || state.selectedNode;
  const isNeighbor = anchor && (anchor === node || state.graph.hasEdge(anchor, node) || state.graph.hasEdge(node, anchor));
  const matchesSearch = state.filters.search && nodeMatchesSearch(node, data, state.filters.search);
  if (state.settings.highlightNeighborhood && anchor && !isNeighbor) {
    reduced.color = "#d5dce5";
    reduced.label = "";
  }
  if (matchesSearch) {
    reduced.color = "#f59e0b";
    reduced.size = Math.max(data.size + 4, 15);
    reduced.forceLabel = true;
  }
  if (isHovered) {
    reduced.highlighted = true;
    reduced.size = data.size + 5;
    reduced.forceLabel = true;
    reduced.zIndex = 10;
  } else if (isSelected) {
    reduced.color = "#111827";
    reduced.size = data.size + 5;
    reduced.forceLabel = true;
    reduced.zIndex = 10;
  }
  if (!state.settings.showLabels && !matchesSearch && !isSelected && !isHovered) reduced.label = "";
  return reduced;
}

function edgeReducer(edge, data) {
  const reduced = { ...data };
  const source = state.graph.source(edge);
  const target = state.graph.target(edge);
  if (isNodeHidden(source, state.graph.getNodeAttributes(source)) || isNodeHidden(target, state.graph.getNodeAttributes(target)) || isEdgeHidden(data)) {
    reduced.hidden = true;
    return reduced;
  }
  const anchor = state.hoveredNode || state.selectedNode;
  if (state.selectedEdge === edge) {
    reduced.color = "#111827";
    reduced.size = 4;
  } else if (state.settings.highlightNeighborhood && anchor && source !== anchor && target !== anchor) {
    reduced.color = "#e2e8f0";
    reduced.label = "";
  } else if (anchor && (source === anchor || target === anchor)) {
    reduced.color = "#2563eb";
    reduced.size = 2.6;
  }
  if (!state.settings.showEdgeLabels) reduced.label = "";
  return reduced;
}

function isNodeHidden(node, data) {
  if (state.selectedNode === node || state.hoveredNode === node) return false;
  if (state.filters.nodeTypes.length && !state.filters.nodeTypes.includes(data.kgLabel)) return true;
  if (state.filters.minDegree && (data.degree || 0) < state.filters.minDegree) return true;
  if (state.filters.search && !nodeMatchesSearch(node, data, state.filters.search)) {
    const hasMatchingNeighbor = state.rawEdges.some(edge => {
      if (edge.source !== node && edge.target !== node) return false;
      const otherId = edge.source === node ? edge.target : edge.source;
      if (!state.graph.hasNode(otherId)) return false;
      return nodeMatchesSearch(otherId, state.graph.getNodeAttributes(otherId), state.filters.search);
    });
    if (!hasMatchingNeighbor) return true;
  }
  return false;
}

function isEdgeHidden(data) {
  return Boolean(state.filters.edgeTypes.length && !state.filters.edgeTypes.includes(data.relationship));
}

function nodeMatchesSearch(node, data, query) {
  const normalized = query.toLowerCase();
  const haystack = [node, data.label, data.title, data.kgLabel, ...Object.entries(data.properties || {}).flatMap(([key, value]) => [key, value])].join(" ").toLowerCase();
  return haystack.includes(normalized);
}

function setLayout(layout, animate = true) {
  stopWorkerLayout({ clearLast: true });
  state.lastWorkerLayout = null;
  state.currentLayout = layout;
  document.querySelectorAll("[data-kg-layout]").forEach(button => button.classList.toggle("active", button.dataset.kgLayout === layout));
  document.querySelectorAll("[data-kg-worker-layout]").forEach(button => button.classList.remove("active"));
  applyLayout(layout, animate);
  refreshRenderer();
  setTimeout(() => recordGraphHistory(), animate ? 280 : 0);
}

function applyLayout(layout, animate) {
  if (!state.graph || !state.graph.order) return;
  if (layout === "circular") circular.assign(state.graph, { scale: Math.max(1, state.graph.order / 6) });
  if (layout === "random") random.assign(state.graph, { scale: Math.max(1, state.graph.order / 5) });
  if (layout === "circlepack") circlepack.assign(state.graph, { hierarchyAttributes: ["kgLabel"], center: 0, scale: Math.max(1, state.graph.order / 4) });
  if (layout === "grid") assignGridLayout(state.graph);
  if (layout === "force") {
    random.assign(state.graph, { scale: Math.max(1, state.graph.order / 4) });
    forceAtlas2.assign(state.graph, { iterations: state.graph.order < 80 ? 180 : 90, settings: { gravity: 0.8, scalingRatio: 12, slowDown: 8, barnesHutOptimize: state.graph.order > 80 } });
  }
  if (animate && state.renderer) state.renderer.getCamera().animatedReset({ duration: 250 });
}

function toggleWorkerLayout(layout) {
  if (state.currentWorkerLayout === layout && state.layoutWorker?.isRunning?.()) {
    stopWorkerLayout();
    return;
  }

  startWorkerLayout(layout);
}

function toggleCurrentWorkerLayout() {
  if (state.currentWorkerLayout && state.layoutWorker?.isRunning?.()) {
    stopWorkerLayout();
    return;
  }

  const layout = state.lastWorkerLayout || (isWorkerLayout(state.currentLayout) ? state.currentLayout : null);
  if (layout) startWorkerLayout(layout);
}

function startWorkerLayout(layout) {
  if (!state.graph || !state.graph.order) return;
  stopWorkerLayout();
  if (!hasFinitePositions(state.graph)) random.assign(state.graph, { scale: Math.max(1, state.graph.order / 5) });

  const WorkerCtor = getWorkerLayoutConstructor(layout);
  if (!WorkerCtor) return;

  state.currentWorkerLayout = layout;
  state.lastWorkerLayout = layout;
  state.currentLayout = layout;
  document.querySelectorAll("[data-kg-layout]").forEach(button => button.classList.remove("active"));
  document.querySelectorAll("[data-kg-worker-layout]").forEach(button => button.classList.toggle("active", button.dataset.kgWorkerLayout === layout));

  try {
    state.layoutWorker = new WorkerCtor(state.graph, getWorkerLayoutOptions(layout));
    state.layoutWorker.start();
    updateLayoutStatus();
    refreshRenderer();
  } catch (error) {
    state.layoutWorker = null;
    state.currentWorkerLayout = null;
    setQueryMessage(`Layout worker failed: ${error.message}`, "error");
    updateLayoutStatus();
  }
}

function stopWorkerLayout(options = {}) {
  if (state.layoutWorker) {
    state.layoutWorker.stop?.();
    state.layoutWorker.kill?.();
    state.layoutWorker = null;
  }
  if (state.currentWorkerLayout) {
    setTimeout(() => recordGraphHistory(), 0);
  }
  state.currentWorkerLayout = null;
  if (options.clearLast) state.lastWorkerLayout = null;
  document.querySelectorAll("[data-kg-worker-layout]").forEach(button => button.classList.remove("active"));
  updateLayoutStatus();
}

function isWorkerLayout(layout) {
  return layout === "force" || layout === "forceatlas2" || layout === "noverlap";
}

function getWorkerLayoutConstructor(layout) {
  if (layout === "force") return ForceLayout;
  if (layout === "forceatlas2") return ForceAtlas2Layout;
  if (layout === "noverlap") return NoverlapLayout;
  return null;
}

function getWorkerLayoutOptions(layout) {
  if (layout === "force") {
    return { settings: { attraction: 0.0006, repulsion: 0.12, gravity: 0.0002 } };
  }

  if (layout === "forceatlas2") {
    return { settings: { gravity: 0.8, scalingRatio: 12, slowDown: 8, barnesHutOptimize: state.graph.order > 80 } };
  }

  if (layout === "noverlap") {
    return { settings: { margin: 6, ratio: 1.2, expansion: 1.1 } };
  }

  return {};
}

function hasFinitePositions(graph) {
  let valid = true;
  graph.forEachNode((node, attributes) => {
    if (!Number.isFinite(attributes.x) || !Number.isFinite(attributes.y)) valid = false;
  });
  return valid;
}

function updateLayoutStatus() {
  if (!el.layoutStatusText) return;
  if (state.currentWorkerLayout && state.layoutWorker?.isRunning?.()) {
    el.layoutStatusText.innerHTML = `<strong>Current:</strong> <span class="kg-layout-running">${escapeHtml(getWorkerLayoutLabel(state.currentWorkerLayout))} is running</span>`;
    updateWorkerLayoutToggle(true, state.currentWorkerLayout);
    return;
  }

  if (state.lastWorkerLayout) {
    el.layoutStatusText.innerHTML = `<strong>Current:</strong> <span class="kg-layout-stopped">${escapeHtml(getWorkerLayoutLabel(state.lastWorkerLayout))} is stopped</span>`;
    updateWorkerLayoutToggle(false, state.lastWorkerLayout);
    return;
  }

  el.layoutStatusText.innerHTML = "<strong>Current:</strong> none";
  updateWorkerLayoutToggle(false, null);
}

function updateWorkerLayoutToggle(isRunning, layout) {
  if (!el.workerLayoutToggleBtn) return;
  el.workerLayoutToggleBtn.disabled = !layout;
  el.workerLayoutToggleBtn.textContent = isRunning ? "Stop" : "Start";
  el.workerLayoutToggleBtn.classList.toggle("running", isRunning);
  el.workerLayoutToggleBtn.setAttribute("aria-pressed", isRunning ? "true" : "false");
  el.workerLayoutToggleBtn.title = layout
    ? `${isRunning ? "Stop" : "Start"} ${getWorkerLayoutLabel(layout)} worker layout`
    : "Choose a worker layout first";
}

function getWorkerLayoutLabel(layout) {
  if (layout === "forceatlas2") return "forceAtlas2";
  return layout;
}

function assignGridLayout(graph) {
  const columns = Math.max(1, Math.ceil(Math.sqrt(graph.order)));
  const spacing = 1.4;
  let index = 0;
  graph.forEachNode(node => {
    const row = Math.floor(index / columns);
    const column = index % columns;
    graph.mergeNodeAttributes(node, { x: (column - columns / 2) * spacing, y: (row - columns / 2) * spacing });
    index++;
  });
}

function rebuildFilterOptions() {
  rebuildCheckboxDropdown({
    list: el.nodeTypeFilterList,
    summary: el.nodeTypeFilterSummary,
    values: uniqueSorted(state.rawNodes.map(node => node.label)),
    selectedValues: state.filters.nodeTypes,
    onChange: selected => updateFilters({ nodeTypes: selected })
  });
  rebuildCheckboxDropdown({
    list: el.edgeTypeFilterList,
    summary: el.edgeTypeFilterSummary,
    values: uniqueSorted(state.rawEdges.map(edge => edge.label)),
    selectedValues: state.filters.edgeTypes,
    onChange: selected => updateFilters({ edgeTypes: selected })
  });
}

function rebuildCheckboxDropdown({ list, summary, values, selectedValues, onChange }) {
  if (!list || !summary) return;
  const selected = selectedValues.filter(value => values.includes(value));
  list.innerHTML = "";
  list.appendChild(createCheckboxOption("All", "", selected.length === 0, checked => {
    if (!checked) return;
    list.querySelectorAll('input[data-filter-value]').forEach(input => { input.checked = false; });
    updateCheckboxDropdownSummary(summary, []);
    onChange([]);
  }));
  values.forEach(value => {
    list.appendChild(createCheckboxOption(value, value, selected.includes(value), () => {
      const next = Array.from(list.querySelectorAll('input[data-filter-value]:checked')).map(input => input.dataset.filterValue);
      const allCheckbox = list.querySelector('input:not([data-filter-value])');
      if (allCheckbox) allCheckbox.checked = next.length === 0;
      updateCheckboxDropdownSummary(summary, next);
      onChange(next);
    }));
  });
  updateCheckboxDropdownSummary(summary, selected);
  if (selected.length !== selectedValues.length) onChange(selected);
}

function createCheckboxOption(labelText, value, checked, onChange) {
  const label = document.createElement("label");
  label.className = "kg-checkbox-option";
  label.title = labelText;
  const checkbox = document.createElement("input");
  checkbox.type = "checkbox";
  checkbox.checked = checked;
  if (value) checkbox.dataset.filterValue = value;
  checkbox.addEventListener("change", () => onChange(checkbox.checked));
  const text = document.createElement("span");
  text.textContent = labelText;
  label.appendChild(checkbox);
  label.appendChild(text);
  return label;
}

function updateCheckboxDropdownSummary(summary, selected) {
  if (!selected.length) {
    summary.textContent = "All";
    summary.title = "All";
    return;
  }
  summary.textContent = selected.length === 1 ? selected[0] : `${selected.length} selected`;
  summary.title = selected.join(", ");
}

function updateSearchResults() {
  if (!state.graph || !state.filters.search) {
    renderSearchResults([]);
    return;
  }
  const results = [];
  state.graph.forEachNode((node, data) => {
    if (!isNodeHidden(node, data) && nodeMatchesSearch(node, data, state.filters.search)) results.push({ id: node, title: data.title, label: data.kgLabel });
  });
  renderSearchResults(results.slice(0, 30));
}

function renderSearchResults(results) {
  el.searchResults.innerHTML = "";
  if (!state.rawNodes.length) {
    el.searchResults.textContent = "Run a query to search graph nodes.";
    return;
  }
  if (!state.filters.search) {
    el.searchResults.textContent = "Type to filter and highlight nodes.";
    return;
  }
  if (!results.length) {
    el.searchResults.textContent = "No matching visible nodes.";
    return;
  }
  const fragment = document.createDocumentFragment();
  results.forEach(result => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "kg-search-result";
    button.innerHTML = `<span>${escapeHtml(result.title)}</span><small>${escapeHtml(result.label)}</small>`;
    button.addEventListener("click", () => selectNode(result.id));
    fragment.appendChild(button);
  });
  el.searchResults.appendChild(fragment);
}

function selectNode(node, options = {}) {
  if (!state.graph?.hasNode(node)) return;
  clearInspectorHoverState();
  state.selectedNode = node;
  state.selectedEdge = null;
  renderInspector({ type: "node", id: node });
  refreshRenderer();
  if (options.center !== false) requestAnimationFrame(() => centerNode(node));
  setTimeout(() => recordGraphHistory(), options.center === false ? 0 : 360);
}

function centerNode(node) {
  if (!state.renderer || !state.graph?.hasNode(node)) return;
  const displayData = state.renderer.getNodeDisplayData(node);
  const x = Number(displayData?.x ?? state.graph.getNodeAttribute(node, "x"));
  const y = Number(displayData?.y ?? state.graph.getNodeAttribute(node, "y"));
  if (!Number.isFinite(x) || !Number.isFinite(y)) {
    state.renderer.getCamera().animatedReset({ duration: 250 });
    return;
  }

  const currentRatio = state.renderer.getCamera().getState().ratio;
  const ratio = Math.min(Math.max(currentRatio, 0.12), 1.1);
  state.renderer.getCamera().animate({ x, y, ratio }, { duration: 300 });
}

function fitGraph() {
  if (!state.renderer) return;
  state.renderer.getCamera().animatedReset({ duration: 250 });
  setTimeout(() => recordGraphHistory(), 280);
}

function centerSelectedOrFit() {
  if (state.selectedNode && state.graph?.hasNode(state.selectedNode)) {
    centerNode(state.selectedNode);
    setTimeout(() => recordGraphHistory(), 330);
    return;
  }

  fitGraph();
}

function zoomBy(factor) {
  const camera = state.renderer?.getCamera();
  if (!camera) return;
  camera.animate({ ratio: camera.getState().ratio * factor }, { duration: 180 });
  setTimeout(() => recordGraphHistory(), 210);
}

function recordGraphHistory(options = {}) {
  if (state.isRestoringHistory || !state.renderer) return;
  const snapshot = createGraphHistorySnapshot();
  if (!snapshot) return;

  const current = state.history[state.historyIndex];
  if (current && sameHistorySnapshot(current, snapshot)) {
    updateHistoryButtons();
    return;
  }

  if (options.replace && state.historyIndex >= 0) {
    state.history[state.historyIndex] = snapshot;
  } else {
    state.history = state.history.slice(0, state.historyIndex + 1);
    state.history.push(snapshot);
    if (state.history.length > 80) state.history.shift();
    state.historyIndex = state.history.length - 1;
  }

  updateHistoryButtons();
}

function createGraphHistorySnapshot() {
  const cameraState = state.renderer?.getCamera().getState();
  if (!cameraState) return null;
  return {
    selectedNode: state.selectedNode,
    selectedEdge: state.selectedEdge,
    camera: {
      x: roundCameraValue(cameraState.x),
      y: roundCameraValue(cameraState.y),
      ratio: roundCameraValue(cameraState.ratio),
      angle: roundCameraValue(cameraState.angle || 0)
    }
  };
}

function navigateGraphHistory(direction) {
  const nextIndex = state.historyIndex + direction;
  if (nextIndex < 0 || nextIndex >= state.history.length) return;
  state.historyIndex = nextIndex;
  restoreGraphHistory(state.history[state.historyIndex]);
}

function restoreGraphHistory(snapshot) {
  if (!snapshot || !state.renderer) return;
  state.isRestoringHistory = true;
  state.selectedNode = snapshot.selectedNode && state.graph?.hasNode(snapshot.selectedNode) ? snapshot.selectedNode : null;
  state.selectedEdge = snapshot.selectedEdge && state.graph?.hasEdge(snapshot.selectedEdge) ? snapshot.selectedEdge : null;

  if (state.selectedNode) renderInspector({ type: "node", id: state.selectedNode });
  else if (state.selectedEdge) renderInspector({ type: "edge", id: state.selectedEdge });
  else renderInspector(null);

  refreshRenderer();
  state.renderer.getCamera().animate(snapshot.camera, { duration: 220 });
  setTimeout(() => {
    state.isRestoringHistory = false;
    updateHistoryButtons();
  }, 240);
}

function updateHistoryButtons() {
  if (!el.historyBackBtn || !el.historyForwardBtn) return;
  el.historyBackBtn.disabled = state.historyIndex <= 0;
  el.historyForwardBtn.disabled = state.historyIndex < 0 || state.historyIndex >= state.history.length - 1;
}

function sameHistorySnapshot(left, right) {
  return left.selectedNode === right.selectedNode
    && left.selectedEdge === right.selectedEdge
    && left.camera.x === right.camera.x
    && left.camera.y === right.camera.y
    && left.camera.ratio === right.camera.ratio
    && left.camera.angle === right.camera.angle;
}

function roundCameraValue(value) {
  return Math.round(Number(value || 0) * 10000) / 10000;
}

function renderInspector(selection, options = {}) {
  if (!selection || !state.graph) {
    clearInspectorHoverState();
    el.inspectorContent.className = "kg-inspector-empty";
    el.inspectorContent.textContent = "Select a node or relationship in the graph.";
    return;
  }

  if (selection.type === "node") {
    const data = state.graph.getNodeAttributes(selection.id);
    const neighbors = state.graph.neighbors(selection.id);
    const details = options.details || state.nodeDetailsCache.get(selection.id) || null;
    const properties = details?.attributes || data.properties;
    el.inspectorContent.className = "kg-inspector-content";
    el.inspectorContent.innerHTML = `<div class="kg-inspector-heading"><span class="kg-color-dot" style="background:${data.color}"></span><div><strong>${escapeHtml(data.title)}</strong><small>${escapeHtml(data.kgLabel)} - degree ${data.degree}</small></div></div>${renderPropertyTable(properties)}<div class="kg-neighbor-list"><div class="kg-mini-title">Neighbors</div>${neighbors.slice(0, 40).map(id => `<button type="button" data-node-id="${escapeHtml(id)}">${escapeHtml(state.graph.getNodeAttribute(id, "title") || id)}</button>`).join("") || '<span class="text-muted small">No neighbors</span>'}</div>${renderChunksSection(details, options.detailsError)}`;
    el.inspectorContent.querySelectorAll("[data-node-id]").forEach(button => button.addEventListener("click", () => selectNode(button.dataset.nodeId)));
    wireChunkCardHighlighting();
    if (!details && !options.detailsError && !options.skipDetailsLoad) {
      loadNodeDetails(selection.id, data);
    }
    return;
  }

  const data = state.graph.getEdgeAttributes(selection.id);
  clearInspectorHoverState();
  const source = state.graph.source(selection.id);
  const target = state.graph.target(selection.id);
  el.inspectorContent.className = "kg-inspector-content";
  el.inspectorContent.innerHTML = `<div class="kg-inspector-heading"><span class="kg-relationship-mark"></span><div><strong>${escapeHtml(data.relationship)}</strong><small>${escapeHtml(source)} -> ${escapeHtml(target)}</small></div></div>${renderPropertyTable(data.properties)}<div class="kg-edge-endpoints"><button type="button" data-node-id="${escapeHtml(source)}">Source</button><button type="button" data-node-id="${escapeHtml(target)}">Target</button></div>`;
  el.inspectorContent.querySelectorAll("[data-node-id]").forEach(button => button.addEventListener("click", () => selectNode(button.dataset.nodeId)));
}

async function loadNodeDetails(nodeId, data) {
  const requestId = ++state.latestNodeDetailsRequestId;
  try {
    const response = await fetch(`${backendBaseUrl}/api/graph/node/details`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        id: String(nodeId),
        label: data.kgLabel || "",
        title: data.title || "",
        properties: data.properties || {}
      })
    });
    const payload = await response.json().catch(() => null);
    if (requestId !== state.latestNodeDetailsRequestId || state.selectedNode !== nodeId) return;

    if (!response.ok) {
      renderInspector({ type: "node", id: nodeId }, {
        skipDetailsLoad: true,
        detailsError: payload?.error || payload?.detail || `Could not load linked chunks (status ${response.status}).`
      });
      return;
    }

    state.nodeDetailsCache.set(nodeId, payload || {});
    renderInspector({ type: "node", id: nodeId }, { details: payload || {}, skipDetailsLoad: true });
  } catch (error) {
    if (requestId !== state.latestNodeDetailsRequestId || state.selectedNode !== nodeId) return;
    renderInspector({ type: "node", id: nodeId }, { skipDetailsLoad: true, detailsError: `Could not load linked chunks: ${error.message}` });
  }
}

function renderChunksSection(details, error) {
  const chunks = details?.chunks || [];
  const content = error
    ? `<div class="kg-chunks-message kg-chunks-error">${escapeHtml(error)}</div>`
    : !details
      ? '<div class="kg-chunks-message">Loading linked chunks...</div>'
      : chunks.length === 0
        ? '<div class="kg-chunks-message">No linked chunks found.</div>'
        : `<div class="kg-chunk-card-list">${chunks.map(renderChunkCard).join("")}</div>`;

  return `<div class="kg-inspector-divider" role="separator"></div><section class="kg-linked-chunks" aria-label="Chunks linked to selected node"><div class="kg-mini-title">CHUNKS</div>${content}</section>`;
}

function renderChunkCard(chunk) {
  const chunkNodeId = findChunkNodeId(chunk);
  const documentNodeId = findDocumentNodeId(chunk.documentId);
  const documentTitle = chunk.documentTitle || chunk.documentFileName || `Document ${chunk.documentId}`;
  const documentReference = documentNodeId
    ? `<button type="button" data-node-id="${escapeHtml(documentNodeId)}">${escapeHtml(documentTitle)}</button>`
    : `<span>${escapeHtml(documentTitle)}</span>`;
  const scoreParts = [];
  if (chunk.score != null) scoreParts.push(`score ${formatMetric(chunk.score)}`);
  if (chunk.distance != null) scoreParts.push(`distance ${formatMetric(chunk.distance)}`);
  const meta = [chunk.linkReason, `chunk ${chunk.chunkIndex}`, chunk.documentDate, ...scoreParts].filter(Boolean).join(" · ");

  const chunkNodeAttribute = chunkNodeId ? ` data-chunk-node-id="${escapeHtml(chunkNodeId)}"` : "";
  return `<article class="kg-chunk-card"${chunkNodeAttribute} data-chunk-id="${escapeHtml(chunk.id)}" data-document-id="${escapeHtml(chunk.documentId)}" data-chunk-index="${escapeHtml(chunk.chunkIndex)}" data-document-title="${escapeHtml(documentTitle)}" tabindex="0"><div class="kg-chunk-text">${escapeHtml(chunk.content)}</div><footer><span>${escapeHtml(meta)}</span><span class="kg-chunk-document-ref">Document: ${documentReference}</span></footer></article>`;
}

function wireChunkCardHighlighting() {
  el.inspectorContent.querySelectorAll(".kg-chunk-card").forEach(card => {
    const highlight = () => {
      const nodeId = card.dataset.chunkNodeId || findChunkNodeIdFromDataset(card.dataset);
      if (!nodeId) return;
      card.dataset.chunkNodeId = nodeId;
      card.classList.add("is-highlighting");
      setInspectorHoveredNode(nodeId);
    };
    const clear = () => {
      card.classList.remove("is-highlighting");
      clearInspectorHoveredNode(card.dataset.chunkNodeId);
    };
    card.addEventListener("mouseenter", highlight);
    card.addEventListener("mouseleave", clear);
    card.addEventListener("focus", highlight);
    card.addEventListener("blur", clear);
  });
}

function setInspectorHoveredNode(nodeId) {
  if (!nodeId || !state.graph?.hasNode(nodeId)) return;
  if (state.inspectorHighlightedNode && state.inspectorHighlightedNode !== nodeId && state.graph.hasNode(state.inspectorHighlightedNode)) {
    state.graph.removeNodeAttribute(state.inspectorHighlightedNode, "highlighted");
  }
  state.hoveredNode = nodeId;
  state.inspectorHighlightedNode = nodeId;
  state.graph.setNodeAttribute(nodeId, "highlighted", true);
  refreshRenderer();
}

function clearInspectorHoveredNode(nodeId) {
  if (state.hoveredNode !== nodeId) return;
  clearInspectorHoverState();
  refreshRenderer();
}

function clearInspectorHoverState() {
  if (state.inspectorHighlightedNode && state.graph?.hasNode(state.inspectorHighlightedNode)) {
    state.graph.removeNodeAttribute(state.inspectorHighlightedNode, "highlighted");
  }
  state.hoveredNode = null;
  state.inspectorHighlightedNode = null;
}

function findChunkNodeIdFromDataset(dataset) {
  return findChunkNodeId({
    id: dataset.chunkId,
    documentId: dataset.documentId,
    chunkIndex: dataset.chunkIndex,
    documentTitle: dataset.documentTitle
  });
}

function findChunkNodeId(chunk) {
  if (!chunk || !state.graph) return null;
  const chunkId = String(chunk.id ?? "");
  const prefixed = chunkId ? `chunk:${chunkId}` : null;
  if (prefixed && state.graph.hasNode(prefixed)) return prefixed;

  let match = null;
  const chunkIndex = String(chunk.chunkIndex ?? "");
  const documentId = String(chunk.documentId ?? "");
  const documentTitle = String(chunk.documentTitle ?? "").toLowerCase();
  state.graph.forEachNode((node, data) => {
    if (match || data.kgLabel !== "Chunk") return;
    const properties = data.properties || {};
    const sameId = chunkId && String(properties.id ?? "") === chunkId;
    const sameDocumentChunk = documentId && chunkIndex
      && String(properties.document_id ?? "") === documentId
      && String(properties.chunk_index ?? "") === chunkIndex;
    const sameVisibleChunk = chunkIndex && String(properties.chunk_index ?? "") === chunkIndex
      && (!state.selectedNode || state.graph.hasEdge(node, state.selectedNode) || state.graph.hasEdge(state.selectedNode, node));
    const sameTitledChunk = chunkIndex && String(data.title || "").toLowerCase() === `chunk ${chunkIndex}`
      && (!documentTitle || String(properties.document_title || properties.title || "").toLowerCase().includes(documentTitle));
    if (sameId || sameDocumentChunk || sameVisibleChunk || sameTitledChunk) match = node;
  });
  return match;
}

function findDocumentNodeId(documentId) {
  const target = String(documentId ?? "");
  if (!target || !state.graph) return null;
  const prefixed = `doc:${target}`;
  if (state.graph.hasNode(prefixed)) return prefixed;

  let match = null;
  state.graph.forEachNode((node, data) => {
    if (match || data.kgLabel !== "Document") return;
    if (String(data.properties?.id ?? "") === target) match = node;
  });
  return match;
}

function formatMetric(value) {
  const number = Number(value);
  return Number.isFinite(number) ? number.toFixed(4).replace(/0+$/, "").replace(/\.$/, "") : String(value);
}

function renderPropertyTable(properties) {
  const entries = Object.entries(properties || {});
  if (!entries.length) return '<div class="kg-empty-properties">No properties returned for this item.</div>';
  return `<table class="table table-sm kg-property-table"><tbody>${entries.map(([key, value]) => `<tr><th>${escapeHtml(key)}</th><td>${escapeHtml(formatValue(value))}</td></tr>`).join("")}</tbody></table>`;
}

function renderLegend() {
  el.legend.innerHTML = "";
  for (const [label, color] of state.colorByLabel.entries()) {
    const item = document.createElement("span");
    item.className = "kg-legend-item";
    item.innerHTML = `<span style="background:${color}"></span>${escapeHtml(label)}`;
    el.legend.appendChild(item);
  }
}

function renderStats() {
  const visibleNodes = getVisibleNodeCount();
  const visibleEdges = getVisibleEdgeCount();
  const density = state.rawNodes.length > 1 ? (state.rawEdges.length / (state.rawNodes.length * (state.rawNodes.length - 1))).toFixed(3) : "0.000";
  el.stats.innerHTML = `<span>${state.rawNodes.length} nodes</span><span>${state.rawEdges.length} edges</span><span>${visibleNodes} visible nodes</span><span>${visibleEdges} visible edges</span><span>density ${density}</span>`;
}

function getVisibleNodeCount() {
  if (!state.graph) return 0;
  let count = 0;
  state.graph.forEachNode((node, data) => { if (!isNodeHidden(node, data)) count++; });
  return count;
}

function getVisibleEdgeCount() {
  if (!state.graph) return 0;
  let count = 0;
  state.graph.forEachEdge((edge, data, source, target, sourceAttributes, targetAttributes) => {
    if (!isEdgeHidden(data) && !isNodeHidden(source, sourceAttributes) && !isNodeHidden(target, targetAttributes)) count++;
  });
  return count;
}

function calculateDegrees(nodes, edges) {
  const degreeById = new Map(nodes.map(node => [node.id, 0]));
  edges.forEach(edge => {
    degreeById.set(edge.source, (degreeById.get(edge.source) || 0) + 1);
    degreeById.set(edge.target, (degreeById.get(edge.target) || 0) + 1);
  });
  return degreeById;
}

function buildColorMap(nodes) {
  const labels = uniqueSorted(nodes.map(node => node.label));
  return new Map(labels.map((label, index) => [label, palette[index % palette.length]]));
}

function exportGraphJson() {
  const data = JSON.stringify({ graphName: state.graphName, nodes: state.rawNodes, edges: state.rawEdges, rows: state.lastPayload?.rows || [] }, null, 2);
  const blob = new Blob([data], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `${state.graphName || "knowledge_graph"}-sigma-export.json`;
  link.click();
  URL.revokeObjectURL(url);
}

function refreshRenderer() {
  state.renderer?.refresh();
}

function setStatus(element, stateName, text) {
  element.className = `kg-status-pill kg-status-${stateName}`;
  element.textContent = text;
}

function setQueryMessage(text, kind) {
  el.queryMessage.className = `kg-query-message kg-query-${kind}`;
  el.queryMessage.textContent = text;
}

function uniqueSorted(values) {
  return [...new Set(values.filter(Boolean).map(String))].sort((a, b) => a.localeCompare(b));
}

function formatValue(value) {
  if (value == null) return "";
  if (typeof value === "object") return JSON.stringify(value, null, 2);
  return String(value);
}

function escapeHtml(value) {
  return String(value ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/\"/g, "&quot;").replace(/'/g, "&#39;");
}
