import Graph from "https://esm.sh/graphology@0.26.0";
import Sigma from "https://esm.sh/sigma@3.0.3";
import { EdgeCurvedArrowProgram } from "https://esm.sh/@sigma/edge-curve@3.1.0?deps=sigma@3.0.3";
import { bindWebGLLayer, createContoursProgram } from "https://esm.sh/@sigma/layer-webgl@3.0.0?deps=sigma@3.0.3";
import { circlepack, circular, random } from "https://esm.sh/graphology-layout@0.6.1";
import forceAtlas2 from "https://esm.sh/graphology-layout-forceatlas2@0.10.1";
import ForceLayout from "https://esm.sh/graphology-layout-force@0.2.4/worker";
import ForceAtlas2Layout from "https://esm.sh/graphology-layout-forceatlas2@0.10.1/worker";
import NoverlapLayout from "https://esm.sh/graphology-layout-noverlap@0.4.2/worker";

const backendBaseUrl = window.noteAssistantGraphExplorer?.backendBaseUrl || "http://localhost:5070";
const palette = ["#2563eb", "#16a34a", "#dc2626", "#9333ea", "#0891b2", "#ca8a04", "#db2777", "#4f46e5", "#0f766e", "#ea580c"];
const communityPalette = ["#0f766e", "#7c3aed", "#dc2626", "#2563eb", "#b45309", "#be185d", "#047857", "#4f46e5", "#0891b2", "#a16207", "#6d28d9", "#15803d"];
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
  communityByEntityId: new Map(),
  communityByEntityKey: new Map(),
  colorByCommunityId: new Map(),
  communityMembershipsLoaded: false,
  communityMembershipsLoading: false,
  communityContourCleanups: [],
  selectedNode: null,
  selectedNodes: new Set(),
  deselectedRelatedNodes: new Set(),
  selectedEdge: null,
  hoveredNode: null,
  hoveredLegendLabel: null,
  selectedLegendLabel: null,
  inspectorHighlightedNode: null,
  filters: { search: "", nodeTypes: [], edgeTypes: [], communities: [], minDegree: 0 },
  filterDefaultsInitialized: { nodeTypes: false, edgeTypes: false, communities: false },
  filterOptions: { nodeTypes: [], edgeTypes: [], communities: [] },
  settings: { showLabels: true, showEdgeLabels: true, nodeNamesInside: false, curvedEdges: true, highlightNeighborhood: true, sizeMode: "degree", labelDensity: 50, colorMode: "label", forceControls: { nodeScale: 65, spacing: 135, centerPull: 30, linkPull: 55 } },
  currentLayout: "random",
  currentWorkerLayout: null,
  lastWorkerLayout: null,
  layoutWorker: null,
  layoutSettleTimer: null,
  lastPayload: null,
  history: [],
  historyIndex: -1,
  isRestoringHistory: false,
  isQueryRunning: false,
  isShiftKeyDown: false,
  isCtrlKeyDown: false,
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
  communityFilter: document.getElementById("kgCommunityFilter"),
  communityFilterSummary: document.getElementById("kgCommunityFilterSummary"),
  communityFilterList: document.getElementById("kgCommunityFilterList"),
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
  nodeScaleSlider: document.getElementById("kgNodeScaleSlider"),
  nodeScaleValue: document.getElementById("kgNodeScaleValue"),
  spacingSlider: document.getElementById("kgSpacingSlider"),
  spacingValue: document.getElementById("kgSpacingValue"),
  centerPullSlider: document.getElementById("kgCenterPullSlider"),
  centerPullValue: document.getElementById("kgCenterPullValue"),
  linkPullSlider: document.getElementById("kgLinkPullSlider"),
  linkPullValue: document.getElementById("kgLinkPullValue"),
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
el.communityFilter?.addEventListener("toggle", async () => {
  if (!el.communityFilter.open || state.communityMembershipsLoaded || state.communityMembershipsLoading) return;
  try {
    await loadCommunityMemberships();
  } catch (error) {
    setQueryMessage(`Community list unavailable: ${error.message}`, "error");
  }
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
const restartWorkerLayoutDebounced = debounce(() => restartCurrentWorkerLayout(), 220);
[el.nodeScaleSlider, el.spacingSlider, el.centerPullSlider, el.linkPullSlider].forEach(input => {
  input?.addEventListener("input", () => updateForceControlSettings({ restartWorker: true }));
});
document.querySelectorAll("[data-kg-layout]").forEach(button => {
  button.addEventListener("click", () => setLayout(button.dataset.kgLayout || "random"));
});
document.querySelectorAll("[data-kg-worker-layout]").forEach(button => {
  button.addEventListener("click", () => toggleWorkerLayout(button.dataset.kgWorkerLayout || "force"));
});
document.addEventListener("click", event => {
  [el.nodeTypeFilter, el.edgeTypeFilter, el.communityFilter].forEach(dropdown => {
    if (dropdown?.open && !dropdown.contains(event.target)) dropdown.open = false;
  });
});
document.addEventListener("keydown", event => {
  if (event.key === "Shift") state.isShiftKeyDown = true;
  if (event.key === "Control") state.isCtrlKeyDown = true;
  updateSelectionCursorState();
});
document.addEventListener("keyup", event => {
  if (event.key === "Shift") state.isShiftKeyDown = false;
  if (event.key === "Control") state.isCtrlKeyDown = false;
  updateSelectionCursorState();
});
window.addEventListener("blur", () => {
  state.isShiftKeyDown = false;
  state.isCtrlKeyDown = false;
  updateSelectionCursorState();
});
window.addEventListener("resize", debounce(() => updateNodeSizesForViewport(), 150));
initInspectorResize();
autosizeQueryInput();
updateForceControlOutputs();
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
    if (!health.ok) {
      setStatus(el.backendStatus, "warning", `Backend warning (${health.status})`);
    } else {
      const ageHealth = await fetch(`${backendBaseUrl}/api/health/age`);
      if (ageHealth.ok) {
        setStatus(el.backendStatus, "online", "Backend + PostgreSQL/AGE online");
      } else {
        const payload = await ageHealth.json().catch(() => null);
        const detail = payload?.detail || payload?.error || `status ${ageHealth.status}`;
        setStatus(el.backendStatus, "warning", `Backend online; PostgreSQL/AGE unavailable: ${detail}`);
      }
    }
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
    try {
      await loadCommunityMemberships();
    } catch {
    }
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
  state.filterDefaultsInitialized = { nodeTypes: false, edgeTypes: false, communities: false };
  state.filterOptions = { nodeTypes: [], edgeTypes: [], communities: [] };
  state.selectedNode = null;
  state.selectedNodes = new Set();
  state.deselectedRelatedNodes = new Set();
  state.selectedEdge = null;
  state.hoveredLegendLabel = null;
  state.selectedLegendLabel = null;
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
  const workerLayoutToRestart = state.currentWorkerLayout && state.layoutWorker?.isRunning?.()
    ? state.currentWorkerLayout
    : null;
  const fallbackToRandom = isWorkerLayout(state.currentLayout) && !workerLayoutToRestart;
  const layoutForRebuild = workerLayoutToRestart || fallbackToRandom ? "random" : state.currentLayout;

  stopWorkerLayout({ clearLast: !workerLayoutToRestart });
  clearCommunityContours();
  if (fallbackToRandom) {
    state.currentLayout = "random";
    document.querySelectorAll("[data-kg-layout]").forEach(button => button.classList.toggle("active", button.dataset.kgLayout === "random"));
    document.querySelectorAll("[data-kg-worker-layout]").forEach(button => button.classList.remove("active"));
  }

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
  applyLayout(layoutForRebuild, false);
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
  updateCommunityContours();
  setTimeout(() => {
    if (workerLayoutToRestart && state.graph?.order && state.renderer) {
      startWorkerLayout(workerLayoutToRestart);
    }
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
  const size = calculateNodeSize(degree);
  const community = getNodeCommunity(node);
  const colorGroup = state.settings.colorMode === "community" && community ? `community:${community.communityId}` : `label:${node.label}`;
  const colorGroupLabel = state.settings.colorMode === "community" && community ? `Community ${community.communityId}: ${community.communityTitle}` : node.label;
  const color = state.settings.colorMode === "community" && community
    ? state.colorByCommunityId.get(String(community.communityId)) || communityPalette[0]
    : state.colorByLabel.get(node.label) || palette[0];
  const properties = community
    ? {
      ...node.properties,
      community_id: String(community.communityId),
      community_title: community.communityTitle,
      community_entity_count: String(community.communityEntityCount ?? ""),
      community_relationship_count: String(community.communityRelationshipCount ?? "")
    }
    : node.properties;
  return {
    x: 0,
    y: 0,
    size,
    label: node.title,
    color,
    kgLabel: node.label,
    kgCommunityId: community ? String(community.communityId) : null,
    kgCommunityTitle: community?.communityTitle || null,
    kgColorGroup: colorGroup,
    kgColorGroupLabel: colorGroupLabel,
    title: node.title,
    properties,
    degree
  };
}

function colorWithAlpha(color, alpha) {
  if (!color) return `rgba(37, 99, 235, ${alpha})`;
  if (color.startsWith("#")) {
    const hex = color.slice(1);
    const normalized = hex.length === 3
      ? hex.split("").map(ch => ch + ch).join("")
      : hex;
    if (normalized.length === 6) {
      const red = Number.parseInt(normalized.slice(0, 2), 16);
      const green = Number.parseInt(normalized.slice(2, 4), 16);
      const blue = Number.parseInt(normalized.slice(4, 6), 16);
      if (Number.isFinite(red) && Number.isFinite(green) && Number.isFinite(blue)) {
        return `rgba(${red}, ${green}, ${blue}, ${alpha})`;
      }
    }
  }

  return color;
}

function calculateNodeSize(degree) {
  const densityScale = getGraphDensityScale();
  const nodeScale = getForceControlMultiplier("nodeScale");
  const baseSize = (state.settings.nodeNamesInside ? 12 : 5.8) * densityScale * nodeScale;
  const maxSize = (state.settings.nodeNamesInside ? 23 : 13.5) * densityScale * nodeScale;
  const minimumSize = state.settings.nodeNamesInside ? 5.5 : 2.4;
  const scaledBase = Math.max(minimumSize, baseSize);
  const scaledMax = Math.max(scaledBase + 1.8, maxSize);
  const degreeStep = 1.55 * densityScale * nodeScale;
  return state.settings.sizeMode === "degree"
    ? scaledBase + Math.min(scaledMax - scaledBase, degree * degreeStep)
    : Math.min(scaledMax, scaledBase + 3 * densityScale);
}

function getGraphDensityScale() {
  const nodeCount = Math.max(state.rawNodes.length || state.graph?.order || 1, 1);
  const rect = el.sigmaContainer?.getBoundingClientRect?.();
  const area = Math.max((rect?.width || 900) * (rect?.height || 620), 1);
  const targetAreaPerNode = state.settings.nodeNamesInside ? 5200 : 3600;
  const scale = Math.sqrt(area / (nodeCount * targetAreaPerNode));
  return Math.max(0.3, Math.min(1, scale));
}

function updateNodeSizesForViewport() {
  if (!state.graph?.order) return;
  state.graph.forEachNode((node, attributes) => {
    state.graph.setNodeAttribute(node, "size", calculateNodeSize(attributes.degree || 0));
  });
  state.graph.forEachEdge(edge => {
    state.graph.setEdgeAttribute(edge, "size", calculateEdgeSize());
  });
  refreshRenderer();
}

function updateForceControlSettings(options = {}) {
  state.settings.forceControls = {
    nodeScale: readSliderValue(el.nodeScaleSlider, state.settings.forceControls.nodeScale),
    spacing: readSliderValue(el.spacingSlider, state.settings.forceControls.spacing),
    centerPull: readSliderValue(el.centerPullSlider, state.settings.forceControls.centerPull),
    linkPull: readSliderValue(el.linkPullSlider, state.settings.forceControls.linkPull)
  };
  updateForceControlOutputs();
  updateNodeSizesForViewport();
  if (options.restartWorker) {
    restartWorkerLayoutDebounced();
  }
}

function readSliderValue(input, fallback) {
  const value = Number(input?.value);
  return Number.isFinite(value) ? value : fallback;
}

function updateForceControlOutputs() {
  const controls = state.settings.forceControls;
  if (el.nodeScaleValue) el.nodeScaleValue.value = `${controls.nodeScale}%`;
  if (el.spacingValue) el.spacingValue.value = `${controls.spacing}%`;
  if (el.centerPullValue) el.centerPullValue.value = `${controls.centerPull}%`;
  if (el.linkPullValue) el.linkPullValue.value = `${controls.linkPull}%`;
}

function getForceControlMultiplier(key) {
  const value = Number(state.settings.forceControls?.[key]);
  return (Number.isFinite(value) ? value : 100) / 100;
}

function getForceControlPower(key, min, max, fallback = 100) {
  const value = Number(state.settings.forceControls?.[key]);
  const normalized = Math.max(0, Math.min(1, (Number.isFinite(value) ? value : fallback) / 100));
  return min * Math.pow(max / min, normalized);
}

function restartCurrentWorkerLayout() {
  if (!state.graph?.order) return;
  const layout = getSelectedWorkerLayout();
  if (!layout) {
    setQueryMessage("Choose Force, ForceAtlas2, or No Overlap to apply spacing, center pull, and link pull.", "muted");
    return;
  }

  if (state.layoutWorker?.isRunning?.()) {
    stopWorkerLayout({ preserveCurrent: true });
  }

  startWorkerLayout(layout);
}

function getSelectedWorkerLayout() {
  if (state.currentWorkerLayout) return state.currentWorkerLayout;
  if (state.lastWorkerLayout) return state.lastWorkerLayout;
  return isWorkerLayout(state.currentLayout) ? state.currentLayout : null;
}

function buildEdgeAttributes(edge) {
  return { size: calculateEdgeSize(), type: getEdgeType(), label: edge.label, color: "#94a3b8", relationship: edge.label, properties: edge.properties };
}

function calculateEdgeSize() {
  const densityScale = getGraphDensityScale();
  return Math.max(0.55, Math.min(1.3, 1.3 * densityScale));
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
  if (shouldSuppressPassiveLabel(data, radius)) return;
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

function shouldSuppressPassiveLabel(data, radius) {
  if (!state.settings.showLabels) return true;
  if (data.forceLabel || data.highlighted) return false;
  const nodeCount = state.graph?.order || state.rawNodes.length || 0;
  if (nodeCount < 180) return false;
  const densityScale = getGraphDensityScale();
  const degree = Number(data.degree || 0);
  if (densityScale < 0.58 && degree < 4) return true;
  if (densityScale < 0.72 && radius < 7.5 && degree < 6) return true;
  return false;
}

function fitCanvasText(context, text, maxWidth) {
  const lines = fitCanvasTextMultiline(context, text, maxWidth, 1);
  return lines[0] || "";
}

function registerGraphEvents() {
  const renderer = state.renderer;
  if (!renderer) return;

  renderer.on("clickNode", event => {
    selectNode(event.node, { center: false, mode: getNodeSelectionMode(event) });
  });
  renderer.on("clickEdge", ({ edge }) => {
    state.selectedEdge = edge;
    state.selectedNode = null;
    state.selectedNodes.clear();
    state.deselectedRelatedNodes.clear();
    state.selectedLegendLabel = null;
    renderInspector({ type: "edge", id: edge });
    updateLegendActiveState();
    refreshRenderer();
    recordGraphHistory();
  });
  renderer.on("clickStage", () => {
    state.selectedNode = null;
    state.selectedNodes.clear();
    state.deselectedRelatedNodes.clear();
    state.selectedEdge = null;
    state.selectedLegendLabel = null;
    renderInspector(null);
    updateLegendActiveState();
    updateCommunityContours();
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
    if (getNodeSelectionMode(event) === "replace") {
      state.selectedNode = event.node;
      state.selectedNodes = new Set([event.node]);
      state.deselectedRelatedNodes.clear();
      state.selectedEdge = null;
      renderInspector({ type: "node", id: draggedNode });
    }
    el.sigmaContainer.classList.add("kg-dragging");
    renderer.getGraph().setNodeAttribute(draggedNode, "highlighted", true);
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
  updateCommunityContours();
}

function updateRendererSettings() {
  if (!state.renderer) return;
  state.renderer.setSetting("renderLabels", state.settings.showLabels);
  state.renderer.setSetting("renderEdgeLabels", state.settings.showEdgeLabels);
  state.renderer.setSetting("defaultEdgeType", getEdgeType());
  updateReducers();
}

function updateCommunitySelection(selected) {
  state.filters.communities = selected;
  state.settings.colorMode = selected.length ? "community" : "label";
  state.hoveredLegendLabel = null;
  state.selectedLegendLabel = null;
  applyColorModeToExistingGraph();
  setQueryMessage(
    selected.length
      ? `${selected.length} ${selected.length === 1 ? "community" : "communities"} selected for community contours.`
      : "Community contours cleared.",
    "muted");
}

function applyColorModeToExistingGraph() {
  if (!state.graph?.order) {
    if (state.rawNodes.length) rebuildGraph();
    return;
  }

  state.rawNodes.forEach(node => {
    if (!state.graph.hasNode(node.id)) return;
    const attributes = buildNodeAttributes(node);
    state.graph.mergeNodeAttributes(node.id, {
      color: attributes.color,
      kgCommunityId: attributes.kgCommunityId,
      kgCommunityTitle: attributes.kgCommunityTitle,
      kgColorGroup: attributes.kgColorGroup,
      kgColorGroupLabel: attributes.kgColorGroupLabel,
      properties: attributes.properties,
      size: attributes.size
    });
  });

  renderLegend();
  renderStats();
  updateSearchResults();
  updateReducers();
  updateCommunityContours();

  if (state.selectedNode && state.graph.hasNode(state.selectedNode)) {
    renderInspector({ type: "node", id: state.selectedNode }, { details: state.nodeDetailsCache.get(state.selectedNode), skipDetailsLoad: true });
  } else if (state.selectedEdge && state.graph.hasEdge(state.selectedEdge)) {
    renderInspector({ type: "edge", id: state.selectedEdge });
  }
}

async function loadCommunityMemberships() {
  if (state.communityMembershipsLoaded || state.communityMembershipsLoading) return;
  state.communityMembershipsLoading = true;
  try {
    const response = await fetch(`${backendBaseUrl}/api/communities/memberships`);
    const payload = await response.json().catch(() => null);
    if (!response.ok || payload?.success === false) {
      throw new Error(payload?.error || payload?.detail || `status ${response.status}`);
    }

    buildCommunityIndexes(payload?.memberships || []);
    state.communityMembershipsLoaded = true;
    rebuildFilterOptions();
  } finally {
    state.communityMembershipsLoading = false;
  }
}

function buildCommunityIndexes(memberships) {
  state.communityByEntityId = new Map();
  state.communityByEntityKey = new Map();
  memberships.forEach(item => {
    const community = {
      communityId: item.communityId,
      communityTitle: item.communityTitle || `Community ${item.communityId}`,
      communityEntityCount: item.communityEntityCount,
      communityRelationshipCount: item.communityRelationshipCount,
      entityId: item.entityId,
      entityLabel: item.entityLabel,
      entityName: item.entityName
    };
    state.communityByEntityId.set(String(item.entityId), community);
    state.communityByEntityKey.set(buildEntityCommunityKey(item.entityLabel, item.entityName), community);
  });

  const communityIds = uniqueSorted(memberships.map(item => String(item.communityId)));
  state.colorByCommunityId = new Map(communityIds.map((id, index) => [id, communityPalette[index % communityPalette.length]]));
}

function getNodeCommunity(node) {
  if (!node || state.settings.colorMode !== "community") return null;
  return getNodeCommunityInfo(node);
}

function getNodeCommunityInfo(node) {
  if (!node) return null;
  const properties = node.properties || {};
  if (node.label !== "Document" && node.label !== "Chunk") {
    const idMatch = state.communityByEntityId.get(String(properties.id ?? ""));
    if (idMatch) return idMatch;
  }

  const name = properties.name || extractNameFromTitle(node.label, node.title);
  return state.communityByEntityKey.get(buildEntityCommunityKey(node.label, name)) || null;
}

function buildEntityCommunityKey(label, name) {
  return `${String(label || "").trim().toLowerCase()}\u001f${String(name || "").trim().toLowerCase()}`;
}

function extractNameFromTitle(label, title) {
  const text = String(title || "").trim();
  const prefix = `${label}:`;
  return text.startsWith(prefix) ? text.slice(prefix.length).trim() : text;
}

function updateReducers() {
  if (!state.renderer || !state.graph) return;
  state.renderer.setSetting("nodeReducer", nodeReducer);
  state.renderer.setSetting("edgeReducer", edgeReducer);
  refreshRenderer();
}

function updateCommunityContours() {
  clearCommunityContours();
  if (!state.renderer || !state.graph?.order || state.settings.colorMode !== "community") return;

  const groups = getCommunityContourGroups();
  if (!groups.length) {
    refreshRenderer();
    return;
  }

  try {
    state.communityContourCleanups = groups.map(group => bindWebGLLayer(
      `community-contour-${group.id}`,
      state.renderer,
      createContoursProgram(group.nodes, {
        radius: getCommunityContourRadius(group.active),
        border: {
          color: colorWithAlpha(group.color, group.active ? 0.9 : 0.68),
          thickness: group.active ? 5 : 4
        },
        levels: [
          {
            color: "#00000000",
            threshold: 0.5
          }
        ]
      })
    ));
  } catch (error) {
    state.communityContourCleanups = [];
    console.warn("Community contour layers unavailable.", error);
    setQueryMessage(`Community contour layers unavailable: ${error.message}`, "muted");
  }

  refreshRenderer();
}

function clearCommunityContours() {
  if (!state.communityContourCleanups?.length) return;
  state.communityContourCleanups.forEach(cleanup => {
    try {
      cleanup?.();
    } catch {
    }
  });
  state.communityContourCleanups = [];
}

function getCommunityContourGroups() {
  const groups = new Map();
  const activeLegendLabel = getActiveLegendLabel();
  const focusedCommunityId = activeLegendLabel?.startsWith("community:")
    ? activeLegendLabel.slice("community:".length)
    : null;
  const selectedCommunityIds = focusedCommunityId
    ? [focusedCommunityId]
    : state.filters.communities;

  if (!selectedCommunityIds.length) {
    return [];
  }

  const selectedCommunitySet = new Set(selectedCommunityIds);

  state.graph.forEachNode((node, data) => {
    if (!data.kgCommunityId || !selectedCommunitySet.has(data.kgCommunityId) || isNodeHidden(node, data)) return;
    const group = groups.get(data.kgCommunityId) || {
      id: data.kgCommunityId,
      color: data.color || state.colorByCommunityId.get(data.kgCommunityId) || communityPalette[0],
      active: true,
      nodes: []
    };
    group.nodes.push(node);
    groups.set(data.kgCommunityId, group);
  });

  return Array.from(groups.values())
    .filter(group => group.nodes.length >= 2)
    .sort((left, right) => right.nodes.length - left.nodes.length)
    .slice(0, 12);
}

function getCommunityContourRadius(active = false) {
  const densityScale = getGraphDensityScale();
  const base = active ? 118 : 96;
  return Math.max(72, Math.min(active ? 170 : 142, base / Math.max(0.62, densityScale)));
}

function nodeReducer(node, data) {
  const reduced = { ...data };
  if (isNodeHidden(node, data)) {
    reduced.hidden = true;
    return reduced;
  }
  const isSelected = isNodeSelected(node);
  const isHovered = state.hoveredNode === node;
  const activeLegendLabel = getActiveLegendLabel();
  const matchesLegendHighlight = activeLegendLabel && data.kgColorGroup === activeLegendLabel;
  const isLegendNeighbor = activeLegendLabel && isNodeAdjacentToLegendGroup(node, activeLegendLabel);
  const anchors = getActiveAnchorNodes();
  const hasAnchors = anchors.length > 0;
  const isNeighbor = hasAnchors && anchors.some(anchor => anchor === node || state.graph.hasEdge(anchor, node) || state.graph.hasEdge(node, anchor));
  const isDeselectedRelated = isNodeDeselectedFromNeighborhood(node) && !isSelected;
  const isHighlightedNeighbor = isNeighbor && !isDeselectedRelated;
  const matchesSearch = state.filters.search && nodeMatchesSearch(node, data, state.filters.search);
  const baseColor = data.color || palette[0];
  if (state.settings.highlightNeighborhood && hasAnchors && !isHighlightedNeighbor) {
    reduced.color = colorWithAlpha(baseColor, 0.14);
    reduced.label = "";
  }
  if (state.settings.highlightNeighborhood && activeLegendLabel && !hasAnchors && !matchesLegendHighlight && !isLegendNeighbor) {
    reduced.color = colorWithAlpha(baseColor, 0.14);
    reduced.label = "";
  }
  if (isLegendNeighbor && !matchesLegendHighlight) {
    reduced.color = baseColor;
    reduced.label = data.label;
    reduced.size = data.size + 2;
  }
  if (matchesSearch) {
    reduced.color = baseColor;
    reduced.label = data.label;
    reduced.size = Math.max(data.size + 4, 15);
    reduced.forceLabel = true;
    reduced.highlighted = true;
  }
  if (isHovered && !isDeselectedRelated) {
    reduced.label = data.label;
    reduced.highlighted = true;
    reduced.size = data.size + 5;
    reduced.forceLabel = true;
    reduced.zIndex = 10;
  } else if (isSelected) {
    reduced.color = baseColor;
    reduced.label = data.label;
    reduced.highlighted = true;
    reduced.size = data.size + 5;
    reduced.forceLabel = true;
    reduced.zIndex = 10;
  }
  if (matchesLegendHighlight) {
    reduced.color = baseColor;
    reduced.label = data.label;
    reduced.highlighted = true;
    reduced.size = data.size + 5;
    reduced.forceLabel = true;
    reduced.zIndex = 10;
  }
  if (!state.settings.showLabels && !matchesSearch && !isSelected && !isHovered && !matchesLegendHighlight && !isLegendNeighbor) reduced.label = "";
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
  const anchors = getActiveAnchorNodes();
  const hasAnchors = anchors.length > 0;
  const activeLegendLabel = getActiveLegendLabel();
  const touchesDeselectedRelated = isNodeDeselectedFromNeighborhood(source) || isNodeDeselectedFromNeighborhood(target);
  const touchesAnchor = hasAnchors && anchors.some(anchor => source === anchor || target === anchor);
  const matchesLegendEndpoint = activeLegendLabel
    && (state.graph.getNodeAttribute(source, "kgColorGroup") === activeLegendLabel || state.graph.getNodeAttribute(target, "kgColorGroup") === activeLegendLabel);
  if (state.selectedEdge === edge) {
    reduced.color = "#111827";
    reduced.size = 4;
  } else if (state.settings.highlightNeighborhood && hasAnchors && (!touchesAnchor || touchesDeselectedRelated)) {
    reduced.color = "#e2e8f0";
    reduced.label = "";
  } else if (touchesAnchor) {
    reduced.color = "#2563eb";
    reduced.size = 2.6;
  } else if (state.settings.highlightNeighborhood && activeLegendLabel && !matchesLegendEndpoint) {
    reduced.color = "#e2e8f0";
    reduced.label = "";
  } else if (matchesLegendEndpoint) {
    reduced.color = "#2563eb";
    reduced.size = 2.6;
  }
  if (!state.settings.showEdgeLabels) reduced.label = "";
  return reduced;
}

function isNodeHidden(node, data) {
  if (isNodeSelected(node) || state.hoveredNode === node) return false;
  const activeLegendLabel = getActiveLegendLabel();
  if (activeLegendLabel && data.kgColorGroup === activeLegendLabel) return false;
  if (state.colorByLabel.size && !state.filters.nodeTypes.includes(data.kgLabel)) return true;
  if (state.filters.minDegree && (data.degree || 0) < state.filters.minDegree) return true;
  if (activeLegendLabel && !isNodeAdjacentToLegendGroup(node, activeLegendLabel)) return true;
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

function isNodeAdjacentToLegendGroup(node, groupId) {
  if (!state.graph?.hasNode(node) || !groupId) return false;
  let related = false;
  state.graph.forEachNode((candidate, attributes) => {
    if (related || attributes.kgColorGroup !== groupId) return;
    related = candidate === node || state.graph.hasEdge(candidate, node) || state.graph.hasEdge(node, candidate);
  });
  return related;
}

function isEdgeHidden(data) {
  return Boolean(getAvailableEdgeTypes().length && !state.filters.edgeTypes.includes(data.relationship));
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
  const spread = getLayoutSpreadScale();
  if (layout === "circular") circular.assign(state.graph, { scale: Math.max(1, (state.graph.order / 6) * spread) });
  if (layout === "random") random.assign(state.graph, { scale: Math.max(1, (state.graph.order / 5) * spread) });
  if (layout === "circlepack") circlepack.assign(state.graph, { hierarchyAttributes: ["kgLabel"], center: 0, scale: Math.max(1, (state.graph.order / 4) * spread) });
  if (layout === "grid") assignGridLayout(state.graph);
  if (layout === "force") {
    random.assign(state.graph, { scale: Math.max(1, (state.graph.order / 4) * spread) });
    forceAtlas2.assign(state.graph, { iterations: state.graph.order < 80 ? 180 : 120, settings: { gravity: 0.35 * getForceControlMultiplier("centerPull"), scalingRatio: 18 * spread, slowDown: 10, barnesHutOptimize: state.graph.order > 80 } });
  }
  if (animate && state.renderer) state.renderer.getCamera().animatedReset({ duration: 250 });
}

function getLayoutSpreadScale() {
  const nodeCount = state.graph?.order || state.rawNodes.length || 1;
  const densityScale = getGraphDensityScale();
  const countPressure = Math.min(1.8, Math.sqrt(nodeCount / 120));
  const viewportPressure = 1 / Math.max(0.55, densityScale);
  const spacing = getForceControlPower("spacing", 0.45, 4.5, 135);
  return Math.max(0.7, Math.min(3.4, countPressure * viewportPressure * spacing));
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
    scheduleWorkerLayoutSettle(layout);
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
  clearWorkerLayoutSettleTimer();
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
  if (options.autoSettled && state.lastWorkerLayout) {
    setQueryMessage(`${getWorkerLayoutLabel(state.lastWorkerLayout)} layout settled and stopped. Move a force slider or press Start to settle it again.`, "muted");
    updateCommunityContours();
  }
}

function scheduleWorkerLayoutSettle(layout) {
  clearWorkerLayoutSettleTimer();
  const duration = getWorkerLayoutSettleDuration(layout);
  state.layoutSettleTimer = window.setTimeout(() => {
    if (state.currentWorkerLayout !== layout || !state.layoutWorker?.isRunning?.()) return;
    stopWorkerLayout({ autoSettled: true });
    recordGraphHistory();
  }, duration);
}

function clearWorkerLayoutSettleTimer() {
  if (!state.layoutSettleTimer) return;
  window.clearTimeout(state.layoutSettleTimer);
  state.layoutSettleTimer = null;
}

function getWorkerLayoutSettleDuration(layout) {
  const nodeCount = state.graph?.order || state.rawNodes.length || 0;
  if (layout === "noverlap") return Math.min(4800, Math.max(2200, 1600 + nodeCount * 8));
  if (layout === "force") return Math.min(7200, Math.max(3600, 2600 + nodeCount * 12));
  return Math.min(8200, Math.max(4200, 3200 + nodeCount * 12));
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
  const spread = getLayoutSpreadScale();
  const centerPull = getForceControlPower("centerPull", 0.02, 3.2, 30);
  const repel = getForceControlPower("spacing", 0.45, 4.5, 135);
  const linkPull = getForceControlPower("linkPull", 0.12, 3.8, 55);
  if (layout === "force") {
    return { settings: { attraction: 0.0002 * linkPull, repulsion: 0.28 * spread * repel, gravity: 0.00005 * centerPull } };
  }

  if (layout === "forceatlas2") {
    return { settings: { gravity: 0.28 * centerPull, scalingRatio: (26 * spread * repel) / Math.sqrt(linkPull), edgeWeightInfluence: Math.min(2, Math.max(0, linkPull - 0.3)), slowDown: 10, barnesHutOptimize: state.graph.order > 80 } };
  }

  if (layout === "noverlap") {
    return { settings: { margin: 12 * spread * repel, ratio: 1.65, expansion: 1.35 } };
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
    el.layoutStatusText.innerHTML = `<strong>Current:</strong> <span class="kg-layout-running">${escapeHtml(getWorkerLayoutLabel(state.currentWorkerLayout))} is settling</span>`;
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
    ? `${isRunning ? "Stop" : "Start"} ${getWorkerLayoutLabel(layout)} layout settling`
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
  const nodeTypes = uniqueSorted(state.rawNodes.map(node => node.label));
  const edgeTypes = getAvailableEdgeTypes();
  const communities = getAvailableCommunityOptions();
  const communityValues = communities.map(option => option.value);
  const hadAllNodeTypesSelected = hasAllValuesSelected(state.filterOptions.nodeTypes, state.filters.nodeTypes);
  const hadAllEdgeTypesSelected = hasAllValuesSelected(state.filterOptions.edgeTypes, state.filters.edgeTypes);
  if (!state.filterDefaultsInitialized.nodeTypes) {
    state.filters.nodeTypes = [...nodeTypes];
    state.filterDefaultsInitialized.nodeTypes = true;
  } else if (hadAllNodeTypesSelected) {
    state.filters.nodeTypes = [...nodeTypes];
  }
  if (!state.filterDefaultsInitialized.edgeTypes) {
    state.filters.edgeTypes = [...edgeTypes];
    state.filterDefaultsInitialized.edgeTypes = true;
  } else if (hadAllEdgeTypesSelected) {
    state.filters.edgeTypes = [...edgeTypes];
  }
  if (!state.filterDefaultsInitialized.communities) {
    state.filters.communities = [];
    state.filterDefaultsInitialized.communities = true;
  } else {
    state.filters.communities = state.filters.communities.filter(value => communityValues.includes(value));
  }
  state.filterOptions = { nodeTypes: [...nodeTypes], edgeTypes: [...edgeTypes], communities: [...communityValues] };
  rebuildCheckboxDropdown({
    list: el.nodeTypeFilterList,
    summary: el.nodeTypeFilterSummary,
    values: nodeTypes,
    selectedValues: state.filters.nodeTypes,
    onChange: selected => updateFilters({ nodeTypes: selected })
  });
  rebuildCheckboxDropdown({
    list: el.edgeTypeFilterList,
    summary: el.edgeTypeFilterSummary,
    values: edgeTypes,
    selectedValues: state.filters.edgeTypes,
    onChange: selected => updateFilters({ edgeTypes: selected })
  });
  rebuildCheckboxDropdown({
    list: el.communityFilterList,
    summary: el.communityFilterSummary,
    values: communityValues,
    labels: new Map(communities.map(option => [option.value, option.label])),
    colors: new Map(communities.map(option => [option.value, state.colorByCommunityId.get(option.value) || communityPalette[0]])),
    selectedValues: state.filters.communities,
    onChange: selected => updateCommunitySelection(selected)
  });
}

function getAvailableEdgeTypes() {
  return uniqueSorted(state.rawEdges.map(edge => edge.label));
}

function getAvailableCommunityOptions() {
  const options = new Map();
  state.rawNodes.forEach(node => {
    const community = getNodeCommunityInfo(node);
    if (!community) return;
    const value = String(community.communityId);
    if (!options.has(value)) {
      options.set(value, `Community ${community.communityId}: ${community.communityTitle}`);
    }
  });

  return Array.from(options.entries())
    .map(([value, label]) => ({ value, label }))
    .sort((left, right) => left.label.localeCompare(right.label));
}

function hasAllValuesSelected(values, selectedValues) {
  return Boolean(values.length && selectedValues.length === values.length && values.every(value => selectedValues.includes(value)));
}

function rebuildCheckboxDropdown({ list, summary, values, labels = null, colors = null, selectedValues, onChange }) {
  if (!list || !summary) return;
  const selected = selectedValues.filter(value => values.includes(value));
  list.innerHTML = "";
  values.forEach(value => {
    const labelText = labels?.get(value) || value;
    list.appendChild(createCheckboxOption(labelText, value, selected.includes(value), () => {
      const next = Array.from(list.querySelectorAll('input[data-filter-value]:checked')).map(input => input.dataset.filterValue);
      updateCheckboxDropdownSummary(summary, next, values.length, labels);
      onChange(next);
    }, colors?.get(value)));
  });
  updateCheckboxDropdownSummary(summary, selected, values.length, labels);
  if (selected.length !== selectedValues.length) onChange(selected);
}

function createCheckboxOption(labelText, value, checked, onChange, color = null) {
  const label = document.createElement("label");
  label.className = "kg-checkbox-option";
  label.title = labelText;
  if (color) {
    label.classList.add("kg-community-checkbox-option");
    label.style.setProperty("--kg-community-color", color);
  }
  const checkbox = document.createElement("input");
  checkbox.type = "checkbox";
  checkbox.checked = checked;
  if (color) checkbox.style.accentColor = color;
  if (value) checkbox.dataset.filterValue = value;
  checkbox.addEventListener("change", () => onChange(checkbox.checked));
  const text = document.createElement("span");
  text.textContent = labelText;
  label.appendChild(checkbox);
  label.appendChild(text);
  return label;
}

function updateCheckboxDropdownSummary(summary, selected, totalCount, labels = null) {
  if (totalCount > 0 && selected.length === totalCount) {
    summary.textContent = "All selected";
    summary.title = labels ? selected.map(value => labels.get(value) || value).join(", ") : selected.join(", ");
    return;
  }
  if (!selected.length) {
    summary.textContent = "None selected";
    summary.title = "None selected";
    return;
  }
  const selectedLabels = labels ? selected.map(value => labels.get(value) || value) : selected;
  summary.textContent = selected.length === 1 ? selectedLabels[0] : `${selected.length} selected`;
  summary.title = selectedLabels.join(", ");
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
  el.searchResults.hidden = !state.filters.search;
  if (!state.filters.search) {
    return;
  }
  if (!state.rawNodes.length) {
    el.searchResults.textContent = "Run a query to search graph nodes.";
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

function isShiftPressed(event) {
  return Boolean(
    state.isShiftKeyDown
    || event?.event?.original?.shiftKey
    || event?.event?.originalEvent?.shiftKey
    || event?.event?.shiftKey
    || event?.original?.shiftKey
    || event?.originalEvent?.shiftKey
  );
}

function isCtrlPressed(event) {
  return Boolean(
    state.isCtrlKeyDown
    || event?.event?.original?.ctrlKey
    || event?.event?.originalEvent?.ctrlKey
    || event?.event?.ctrlKey
    || event?.original?.ctrlKey
    || event?.originalEvent?.ctrlKey
  );
}

function getNodeSelectionMode(event) {
  if (isCtrlPressed(event)) return "remove";
  if (isShiftPressed(event)) return "add";
  return "replace";
}

function updateSelectionCursorState() {
  if (!el.sigmaContainer) return;
  el.sigmaContainer.classList.toggle("kg-select-add", state.isShiftKeyDown && !state.isCtrlKeyDown);
  el.sigmaContainer.classList.toggle("kg-select-remove", state.isCtrlKeyDown);
}

function isNodeSelected(node) {
  return state.selectedNodes?.has(node) || state.selectedNode === node;
}

function isNodeDeselectedFromNeighborhood(node) {
  return Boolean(state.deselectedRelatedNodes?.has(node));
}

function getSelectedNodeIds() {
  const selected = Array.from(state.selectedNodes || []).filter(node => state.graph?.hasNode(node));
  if (state.selectedNode && state.graph?.hasNode(state.selectedNode) && !selected.includes(state.selectedNode)) {
    selected.push(state.selectedNode);
  }
  return selected;
}

function getActiveAnchorNodes() {
  const selected = getSelectedNodeIds();
  if (state.hoveredNode && state.graph?.hasNode(state.hoveredNode) && (!isNodeDeselectedFromNeighborhood(state.hoveredNode) || !selected.length)) {
    return [state.hoveredNode];
  }
  return selected;
}

function selectNode(node, options = {}) {
  if (!state.graph?.hasNode(node)) return;
  clearInspectorHoverState();
  const mode = options.mode || (options.additive ? "add" : "replace");
  if (mode === "add") {
    state.deselectedRelatedNodes.delete(node);
    if (!state.selectedNodes.size && state.selectedNode && state.graph.hasNode(state.selectedNode)) {
      state.selectedNodes.add(state.selectedNode);
    }
    if (!state.selectedNodes.has(node)) {
      state.selectedNodes.add(node);
    }
    state.selectedNode = node;
  } else if (mode === "remove") {
    if (!state.selectedNodes.size && state.selectedNode && state.graph.hasNode(state.selectedNode)) {
      state.selectedNodes.add(state.selectedNode);
    }
    state.selectedNodes.delete(node);
    if (getSelectedNodeIds().length || state.graph.neighbors(node).some(neighbor => isNodeSelected(neighbor))) {
      state.deselectedRelatedNodes.add(node);
    }
    state.selectedNode = state.selectedNode === node ? Array.from(state.selectedNodes).at(-1) || null : state.selectedNode;
  } else {
    state.selectedNodes = new Set([node]);
    state.deselectedRelatedNodes.clear();
    state.selectedNode = node;
  }
  state.selectedEdge = null;
  state.selectedLegendLabel = null;
  renderInspector(state.selectedNode ? { type: "node", id: state.selectedNode } : null);
  updateLegendActiveState();
  refreshRenderer();
  if (options.center !== false && state.selectedNode) requestAnimationFrame(() => centerNode(state.selectedNode));
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
  const selected = getSelectedNodeIds();
  const nodeToCenter = state.selectedNode && state.graph?.hasNode(state.selectedNode) ? state.selectedNode : selected[0];
  if (nodeToCenter) {
    centerNode(nodeToCenter);
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
    selectedNodes: getSelectedNodeIds(),
    deselectedRelatedNodes: Array.from(state.deselectedRelatedNodes || []).filter(node => state.graph?.hasNode(node)),
    selectedEdge: state.selectedEdge,
    selectedLegendLabel: state.selectedLegendLabel,
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
  state.selectedNodes = new Set((snapshot.selectedNodes || []).filter(node => state.graph?.hasNode(node)));
  state.deselectedRelatedNodes = new Set((snapshot.deselectedRelatedNodes || []).filter(node => state.graph?.hasNode(node)));
  state.selectedNode = snapshot.selectedNode && state.graph?.hasNode(snapshot.selectedNode) ? snapshot.selectedNode : Array.from(state.selectedNodes).at(-1) || null;
  if (state.selectedNode) state.selectedNodes.add(state.selectedNode);
  state.selectedEdge = snapshot.selectedEdge && state.graph?.hasEdge(snapshot.selectedEdge) ? snapshot.selectedEdge : null;
  state.selectedLegendLabel = snapshot.selectedLegendLabel && hasLegendGroup(snapshot.selectedLegendLabel) ? snapshot.selectedLegendLabel : null;

  if (state.selectedNode) renderInspector({ type: "node", id: state.selectedNode });
  else if (state.selectedEdge) renderInspector({ type: "edge", id: state.selectedEdge });
  else renderInspector(null);

  updateLegendActiveState();
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
    && sameStringArray(left.selectedNodes || [], right.selectedNodes || [])
    && sameStringArray(left.deselectedRelatedNodes || [], right.deselectedRelatedNodes || [])
    && left.selectedEdge === right.selectedEdge
    && (left.selectedLegendLabel || null) === (right.selectedLegendLabel || null)
    && left.camera.x === right.camera.x
    && left.camera.y === right.camera.y
    && left.camera.ratio === right.camera.ratio
    && left.camera.angle === right.camera.angle;
}

function sameStringArray(left, right) {
  if (left.length !== right.length) return false;
  return left.every((value, index) => value === right[index]);
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
  const addAction = chunkNodeId
    ? ""
    : '<button type="button" class="kg-add-chunk-btn" data-add-chunk-to-graph>Add to graph</button>';
  return `<article class="kg-chunk-card"${chunkNodeAttribute} data-chunk-id="${escapeHtml(chunk.id)}" data-document-id="${escapeHtml(chunk.documentId)}" data-chunk-index="${escapeHtml(chunk.chunkIndex)}" data-document-title="${escapeHtml(documentTitle)}" tabindex="0"><div class="kg-chunk-text">${escapeHtml(chunk.content)}</div><footer><span>${escapeHtml(meta)}</span><span class="kg-chunk-document-ref">Document: ${documentReference}</span>${addAction}</footer></article>`;
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
  el.inspectorContent.querySelectorAll("[data-add-chunk-to-graph]").forEach(button => {
    button.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();
      addChunkCardToGraph(button.closest(".kg-chunk-card"));
    });
  });
}

function addChunkCardToGraph(card) {
  if (!card || !state.graph) return;
  const chunk = findChunkFromDataset(card.dataset);
  if (!chunk) return;

  const chunkNodeId = ensureChunkNodeInGraph(chunk);
  ensureChunkContextEdges(chunk, chunkNodeId);
  card.dataset.chunkNodeId = chunkNodeId;
  card.classList.add("is-in-graph");
  const button = card.querySelector("[data-add-chunk-to-graph]");
  if (button) {
    button.textContent = "Added";
    button.disabled = true;
  }

  updateGraphAfterExpansion([chunkNodeId]);
  setInspectorHoveredNode(chunkNodeId);
}

function findChunkFromDataset(dataset) {
  const details = state.selectedNode ? state.nodeDetailsCache.get(state.selectedNode) : null;
  const chunks = details?.chunks || [];
  return chunks.find(chunk => String(chunk.id ?? "") === String(dataset.chunkId ?? ""))
    || chunks.find(chunk => String(chunk.documentId ?? "") === String(dataset.documentId ?? "")
      && String(chunk.chunkIndex ?? "") === String(dataset.chunkIndex ?? ""));
}

function ensureChunkNodeInGraph(chunk) {
  const existing = findChunkNodeId(chunk);
  if (existing) return existing;

  const nodeId = `chunk:${chunk.id}`;
  const title = `Chunk ${chunk.chunkIndex}`;
  const node = {
    id: nodeId,
    label: "Chunk",
    title,
    properties: {
      id: String(chunk.id),
      document_id: String(chunk.documentId),
      document_title: chunk.documentTitle || chunk.documentFileName || `Document ${chunk.documentId}`,
      chunk_index: String(chunk.chunkIndex),
      content: chunk.content,
      expanded_from_inspector: "true"
    }
  };
  addGraphNode(node, state.selectedNode);
  return nodeId;
}

function ensureChunkContextEdges(chunk, chunkNodeId) {
  const documentNodeId = ensureDocumentNodeInGraph(chunk);
  if (documentNodeId) addGraphEdge(documentNodeId, chunkNodeId, "HAS_CHUNK", { expanded_from_inspector: "true" });
  if (state.selectedNode && state.graph.hasNode(state.selectedNode) && state.selectedNode !== chunkNodeId && state.graph.getNodeAttribute(state.selectedNode, "kgLabel") !== "Document") {
    addGraphEdge(chunkNodeId, state.selectedNode, "MENTIONS", { expanded_from_inspector: "true" });
  }
}

function ensureDocumentNodeInGraph(chunk) {
  const existing = findDocumentNodeId(chunk.documentId);
  if (existing) return existing;

  const nodeId = `doc:${chunk.documentId}`;
  addGraphNode({
    id: nodeId,
    label: "Document",
    title: `Document: ${chunk.documentTitle || chunk.documentFileName || chunk.documentId}`,
    properties: {
      id: String(chunk.documentId),
      title: chunk.documentTitle || chunk.documentFileName || `Document ${chunk.documentId}`,
      document_date: chunk.documentDate || "",
      expanded_from_inspector: "true"
    }
  }, state.selectedNode);
  return nodeId;
}

function addGraphNode(node, anchorNodeId) {
  if (state.graph.hasNode(node.id)) return;
  ensureLabelColor(node.label);
  state.rawNodes.push(node);
  state.degreeById.set(node.id, 0);
  state.graph.addNode(node.id, buildNodeAttributes(node));
  placeExpandedNode(node.id, anchorNodeId);
}

function addGraphEdge(source, target, label, properties = {}) {
  if (!state.graph.hasNode(source) || !state.graph.hasNode(target)) return;
  const edgeId = `expanded:${source}->${target}:${label}`;
  if (state.graph.hasEdge(edgeId)) return;
  const edge = { id: edgeId, source, target, label, properties };
  state.rawEdges.push(edge);
  state.graph.addDirectedEdgeWithKey(edgeId, source, target, buildEdgeAttributes(edge));
  bumpNodeDegree(source);
  bumpNodeDegree(target);
}

function bumpNodeDegree(nodeId) {
  const degree = (state.degreeById.get(nodeId) || 0) + 1;
  state.degreeById.set(nodeId, degree);
  if (!state.graph.hasNode(nodeId)) return;
  state.graph.setNodeAttribute(nodeId, "degree", degree);
  state.graph.setNodeAttribute(nodeId, "size", calculateNodeSize(degree));
}

function placeExpandedNode(nodeId, anchorNodeId) {
  if (!state.graph.hasNode(nodeId)) return;
  const anchor = anchorNodeId && state.graph.hasNode(anchorNodeId) ? state.graph.getNodeAttributes(anchorNodeId) : null;
  const baseX = Number(anchor?.x ?? 0);
  const baseY = Number(anchor?.y ?? 0);
  const count = state.rawNodes.filter(node => node.properties?.expanded_from_inspector === "true").length;
  const angle = count * 2.399963229728653;
  const distance = Math.max(0.16, Math.sqrt(Math.max(state.graph.order, 1)) * 0.035);
  state.graph.setNodeAttribute(nodeId, "x", baseX + Math.cos(angle) * distance);
  state.graph.setNodeAttribute(nodeId, "y", baseY + Math.sin(angle) * distance);
}

function ensureLabelColor(label) {
  if (state.colorByLabel.has(label)) return;
  state.colorByLabel.set(label, palette[state.colorByLabel.size % palette.length]);
}

function updateGraphAfterExpansion(nodeIdsToHighlight = []) {
  rebuildFilterOptions();
  renderLegend();
  renderStats();
  updateSearchResults();
  updateReducers();
  updateCommunityContours();
  el.exportBtn.disabled = false;
  nodeIdsToHighlight.forEach(nodeId => state.graph.hasNode(nodeId) && state.graph.setNodeAttribute(nodeId, "highlighted", true));
  refreshRenderer();
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
  for (const group of getLegendGroups()) {
    const item = document.createElement("button");
    item.type = "button";
    item.className = `kg-legend-item${group.isCommunity ? " kg-legend-community" : ""}`;
    item.dataset.kgLabel = group.id;
    item.setAttribute("aria-pressed", state.selectedLegendLabel === group.id ? "true" : "false");
    item.title = `Highlight ${group.label} nodes`;
    item.innerHTML = `<span class="kg-legend-swatch" style="background:${group.color}"></span><span class="kg-legend-label">${escapeHtml(group.label)}</span><span class="kg-legend-short">${escapeHtml(group.shortLabel || group.label)}</span>`;
    item.addEventListener("mouseenter", () => setLegendHover(group.id));
    item.addEventListener("mouseleave", () => clearLegendHover(group.id));
    item.addEventListener("focus", () => setLegendHover(group.id));
    item.addEventListener("blur", () => clearLegendHover(group.id));
    item.addEventListener("click", () => toggleLegendSelection(group.id));
    el.legend.appendChild(item);
  }
  updateLegendActiveState();
}

function getLegendGroups() {
  if (!state.graph) {
    return Array.from(state.colorByLabel.entries()).map(([label, color]) => ({ id: `label:${label}`, label, color }));
  }

  const groups = new Map();
  state.graph.forEachNode((node, data) => {
    if (data.kgCommunityId) return;
    if (!data.kgColorGroup || groups.has(data.kgColorGroup)) return;
    groups.set(data.kgColorGroup, {
      id: data.kgColorGroup,
      label: data.kgColorGroupLabel || data.kgLabel || data.kgColorGroup,
      shortLabel: data.kgCommunityId ? `C${data.kgCommunityId}` : data.kgLabel || data.kgColorGroup,
      isCommunity: Boolean(data.kgCommunityId),
      color: data.color || palette[0]
    });
  });

  return Array.from(groups.values()).sort((left, right) => left.label.localeCompare(right.label));
}

function hasLegendGroup(groupId) {
  return getLegendGroups().some(group => group.id === groupId);
}

function getActiveLegendLabel() {
  return state.hoveredLegendLabel || state.selectedLegendLabel;
}

function setLegendHover(label) {
  if (state.hoveredLegendLabel === label) return;
  state.hoveredLegendLabel = label;
  updateLegendActiveState();
  updateCommunityContours();
  refreshRenderer();
}

function clearLegendHover(label) {
  if (state.hoveredLegendLabel !== label) return;
  state.hoveredLegendLabel = null;
  updateLegendActiveState();
  updateCommunityContours();
  refreshRenderer();
}

function toggleLegendSelection(label) {
  clearInspectorHoverState();
  state.selectedLegendLabel = state.selectedLegendLabel === label ? null : label;
  state.selectedNode = null;
  state.selectedNodes.clear();
  state.deselectedRelatedNodes.clear();
  state.selectedEdge = null;
  renderInspector(null);
  updateLegendActiveState();
  updateCommunityContours();
  refreshRenderer();
  recordGraphHistory();
}

function updateLegendActiveState() {
  if (!el.legend) return;
  const activeLegendLabel = getActiveLegendLabel();
  el.legend.querySelectorAll(".kg-legend-item").forEach(item => {
    const label = item.dataset.kgLabel || "";
    const isSelected = state.selectedLegendLabel === label;
    item.classList.toggle("is-active", activeLegendLabel === label);
    item.classList.toggle("is-selected", isSelected);
    item.setAttribute("aria-pressed", isSelected ? "true" : "false");
  });
}

function renderStats() {
  const visibleNodes = getVisibleNodeCount();
  const visibleEdges = getVisibleEdgeCount();
  const density = state.rawNodes.length > 1 ? (state.rawEdges.length / (state.rawNodes.length * (state.rawNodes.length - 1))).toFixed(3) : "0.000";
  const communityStat = state.settings.colorMode === "community" && state.graph
    ? `<span>${new Set(state.rawNodes.map(node => getNodeCommunity(node)?.communityId).filter(Boolean)).size} communities</span>`
    : "";
  el.stats.innerHTML = `<span>${state.rawNodes.length} nodes</span><span>${state.rawEdges.length} edges</span><span>${visibleNodes} visible nodes</span><span>${visibleEdges} visible edges</span>${communityStat}<span>density ${density}</span>`;
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

function debounce(callback, delayMs) {
  let timeoutId = null;
  return (...args) => {
    window.clearTimeout(timeoutId);
    timeoutId = window.setTimeout(() => callback(...args), delayMs);
  };
}
