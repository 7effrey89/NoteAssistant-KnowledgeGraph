import Graph from "https://esm.sh/graphology@0.26.0";
import Sigma from "https://esm.sh/sigma@3";
import { circlepack, circular, random } from "https://esm.sh/graphology-layout@0.6.1";
import forceAtlas2 from "https://esm.sh/graphology-layout-forceatlas2@0.10.1";
import ForceLayout from "https://esm.sh/graphology-layout-force@0.2.4/worker";
import ForceAtlas2Layout from "https://esm.sh/graphology-layout-forceatlas2@0.10.1/worker";
import NoverlapLayout from "https://esm.sh/graphology-layout-noverlap@0.4.2/worker";

const backendBaseUrl = window.noteAssistantGraphExplorer?.backendBaseUrl || "http://localhost:5070";
const palette = ["#2563eb", "#16a34a", "#dc2626", "#9333ea", "#0891b2", "#ca8a04", "#db2777", "#4f46e5", "#0f766e", "#ea580c"];
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
  filters: { search: "", nodeType: "", edgeType: "", minDegree: 0 },
  settings: { showLabels: true, showEdgeLabels: true, highlightNeighborhood: true, sizeMode: "degree" },
  currentLayout: "random",
  currentWorkerLayout: null,
  lastWorkerLayout: null,
  layoutWorker: null,
  lastPayload: null,
  history: [],
  historyIndex: -1,
  isRestoringHistory: false
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
  edgeTypeFilter: document.getElementById("kgEdgeTypeFilter"),
  minDegreeInput: document.getElementById("kgMinDegreeInput"),
  sizeModeSelect: document.getElementById("kgSizeModeSelect"),
  labelsToggle: document.getElementById("kgLabelsToggle"),
  edgeLabelsToggle: document.getElementById("kgEdgeLabelsToggle"),
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
  sigmaContainer: document.getElementById("kgSigmaContainer"),
  emptyState: document.getElementById("kgEmptyState"),
  inspectorContent: document.getElementById("kgInspectorContent")
};

el.runQueryBtn.addEventListener("click", runQuery);
el.loadSampleBtn.addEventListener("click", () => {
  el.queryInput.value = "MATCH p=(n)-[r]->(m)\nRETURN n, r, m\nLIMIT 150";
});
el.exportBtn.addEventListener("click", exportGraphJson);
el.refreshStatusBtn.addEventListener("click", refreshStatus);
el.searchInput.addEventListener("input", () => updateFilters({ search: el.searchInput.value.trim() }));
el.nodeTypeFilter.addEventListener("change", () => updateFilters({ nodeType: el.nodeTypeFilter.value }));
el.edgeTypeFilter.addEventListener("change", () => updateFilters({ edgeType: el.edgeTypeFilter.value }));
el.minDegreeInput.addEventListener("input", () => updateFilters({ minDegree: Number(el.minDegreeInput.value) || 0 }));
el.sizeModeSelect.addEventListener("change", () => {
  state.settings.sizeMode = el.sizeModeSelect.value;
  rebuildGraph();
});
el.labelsToggle.addEventListener("change", () => {
  state.settings.showLabels = el.labelsToggle.checked;
  updateRendererSettings();
});
el.edgeLabelsToggle.addEventListener("change", () => {
  state.settings.showEdgeLabels = el.edgeLabelsToggle.checked;
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
updateLayoutStatus();

await refreshStatus();

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
  const cypher = el.queryInput.value.trim();
  state.graphName = el.graphNameInput.value.trim() || "knowledge_graph";
  if (!cypher) {
    setQueryMessage("Cypher query is required.", "error");
    return;
  }

  setQueryMessage("Running query...", "muted");
  el.runQueryBtn.disabled = true;
  try {
    const response = await fetch(`${backendBaseUrl}/api/query`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ cypher, graphName: state.graphName })
    });
    const payload = await response.json().catch(() => null);
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
    setQueryMessage(`Query failed: ${error.message}`, "error");
  } finally {
    el.runQueryBtn.disabled = false;
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
  state.history = [];
  state.historyIndex = -1;
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
  const size = state.settings.sizeMode === "degree" ? 7 + Math.min(18, degree * 2.4) : 10;
  return { x: 0, y: 0, size, label: node.title, color: state.colorByLabel.get(node.label) || palette[0], kgLabel: node.label, title: node.title, properties: node.properties, degree };
}

function buildEdgeAttributes(edge) {
  return { size: 1.3, label: edge.label, color: "#94a3b8", relationship: edge.label, properties: edge.properties };
}

function buildSigmaSettings() {
  return { allowInvalidContainer: true, renderLabels: state.settings.showLabels, renderEdgeLabels: state.settings.showEdgeLabels, labelRenderedSizeThreshold: 0, labelSize: 12, edgeLabelSize: 10, minCameraRatio: 0.03, maxCameraRatio: 8, defaultEdgeType: "arrow", labelColor: { color: "#1f2937" }, edgeLabelColor: { color: "#475569" } };
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
  const anchor = state.selectedNode || state.hoveredNode;
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
  if (isSelected || isHovered) {
    reduced.color = "#111827";
    reduced.size = data.size + 5;
    reduced.forceLabel = true;
    reduced.zIndex = 10;
  }
  if (!state.settings.showLabels && !matchesSearch && !isSelected) reduced.label = "";
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
  const anchor = state.selectedNode || state.hoveredNode;
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
  if (state.filters.nodeType && data.kgLabel !== state.filters.nodeType) return true;
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
  return Boolean(state.filters.edgeType && data.relationship !== state.filters.edgeType);
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
  rebuildSelect(el.nodeTypeFilter, uniqueSorted(state.rawNodes.map(node => node.label)));
  rebuildSelect(el.edgeTypeFilter, uniqueSorted(state.rawEdges.map(edge => edge.label)));
}

function rebuildSelect(select, values) {
  const previous = select.value;
  select.innerHTML = '<option value="">All</option>';
  values.forEach(value => {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = value;
    select.appendChild(option);
  });
  select.value = values.includes(previous) ? previous : "";
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

function renderInspector(selection) {
  if (!selection || !state.graph) {
    el.inspectorContent.className = "kg-inspector-empty";
    el.inspectorContent.textContent = "Select a node or relationship in the graph.";
    return;
  }

  if (selection.type === "node") {
    const data = state.graph.getNodeAttributes(selection.id);
    const neighbors = state.graph.neighbors(selection.id);
    el.inspectorContent.className = "kg-inspector-content";
    el.inspectorContent.innerHTML = `<div class="kg-inspector-heading"><span class="kg-color-dot" style="background:${data.color}"></span><div><strong>${escapeHtml(data.title)}</strong><small>${escapeHtml(data.kgLabel)} - degree ${data.degree}</small></div></div>${renderPropertyTable(data.properties)}<div class="kg-neighbor-list"><div class="kg-mini-title">Neighbors</div>${neighbors.slice(0, 40).map(id => `<button type="button" data-node-id="${escapeHtml(id)}">${escapeHtml(state.graph.getNodeAttribute(id, "title") || id)}</button>`).join("") || '<span class="text-muted small">No neighbors</span>'}</div>`;
    el.inspectorContent.querySelectorAll("[data-node-id]").forEach(button => button.addEventListener("click", () => selectNode(button.dataset.nodeId)));
    return;
  }

  const data = state.graph.getEdgeAttributes(selection.id);
  const source = state.graph.source(selection.id);
  const target = state.graph.target(selection.id);
  el.inspectorContent.className = "kg-inspector-content";
  el.inspectorContent.innerHTML = `<div class="kg-inspector-heading"><span class="kg-relationship-mark"></span><div><strong>${escapeHtml(data.relationship)}</strong><small>${escapeHtml(source)} -> ${escapeHtml(target)}</small></div></div>${renderPropertyTable(data.properties)}<div class="kg-edge-endpoints"><button type="button" data-node-id="${escapeHtml(source)}">Source</button><button type="button" data-node-id="${escapeHtml(target)}">Target</button></div>`;
  el.inspectorContent.querySelectorAll("[data-node-id]").forEach(button => button.addEventListener("click", () => selectNode(button.dataset.nodeId)));
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
