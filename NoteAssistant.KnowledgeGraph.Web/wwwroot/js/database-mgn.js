const backendBaseUrl = window.noteAssistantDatabaseMgn?.backendBaseUrl || "http://localhost:5070";

const state = {
  images: [],
  entities: [],
  selectedImageUrl: null,
  selectedImageFileName: null,
  activeType: null,
  selectedEntityIds: new Set(),
  suggestionsByEntityId: new Map(),
  applyingSuggestions: false,
  search: ""
};

const el = {
  status: document.getElementById("dbMgnStatus"),
  uploadForm: document.getElementById("entityImageUploadForm"),
  imageFile: document.getElementById("entityImageFile"),
  uploadMessage: document.getElementById("entityImageUploadMessage"),
  refreshImagesBtn: document.getElementById("refreshEntityImagesBtn"),
  imageList: document.getElementById("entityImageList"),
  entitySearch: document.getElementById("entitySearchInput"),
  visualMode: document.getElementById("entityVisualMode"),
  selectedImage: document.getElementById("selectedEntityImage"),
  tabs: document.getElementById("entityTypeTabs"),
  entityList: document.getElementById("entityVisualEntityList"),
  selectionDialog: document.getElementById("entitySelectionDialog"),
  selectVisibleBtn: document.getElementById("selectVisibleEntitiesBtn"),
  deselectVisibleBtn: document.getElementById("deselectVisibleEntitiesBtn"),
  selectMissingBtn: document.getElementById("selectMissingVisualBtn"),
  parallelismInput: document.getElementById("entityVisualParallelismInput"),
  suggestBtn: document.getElementById("suggestVisualBtn"),
  applySuggestionsBtn: document.getElementById("applySuggestedVisualBtn")
};

el.uploadForm?.addEventListener("submit", uploadImage);
el.refreshImagesBtn?.addEventListener("click", loadImages);
el.entitySearch?.addEventListener("input", () => {
  state.search = el.entitySearch.value.trim().toLowerCase();
  renderEntities();
});
el.visualMode?.addEventListener("change", () => {
  state.suggestionsByEntityId.clear();
  renderEntities();
});
el.selectVisibleBtn?.addEventListener("click", () => selectEntities(getVisibleEntities().map(entity => entity.id)));
el.deselectVisibleBtn?.addEventListener("click", () => deselectEntities(getVisibleEntities().map(entity => entity.id)));
el.selectMissingBtn?.addEventListener("click", () => selectEntities(getVisibleEntities().filter(entity => !getAssignedUrl(entity, getVisualMode())).map(entity => entity.id)));
el.suggestBtn?.addEventListener("click", suggestVisuals);
el.applySuggestionsBtn?.addEventListener("click", applySuggestions);

await initialize();

async function initialize() {
  setStatus("Loading");
  await Promise.all([loadImages(), loadEntities()]);
  setStatus("Ready");
}

async function loadImages() {
  const response = await fetch(`${backendBaseUrl}/api/entity-visual-assets`);
  const payload = await response.json().catch(() => null);
  if (response.ok && payload?.success !== false) {
    state.images = payload.assets || [];
  } else {
    state.images = [];
    setStatus(payload?.error || "Image metadata unavailable");
  }
  renderImages();
}

async function loadEntities() {
  const response = await fetch(`${backendBaseUrl}/api/entities/visual-management`);
  const payload = await response.json().catch(() => null);
  if (!response.ok || payload?.success === false) {
    throw new Error(payload?.error || payload?.detail || `Could not load entities (${response.status}).`);
  }

  state.entities = payload.entities || [];
  state.activeType = state.activeType || getEntityTypes()[0] || null;
  renderTabs();
  renderEntities();
}

async function uploadImage(event) {
  event.preventDefault();
  const file = el.imageFile?.files?.[0];
  if (!file) {
    setUploadMessage("Choose an image file first.", "error");
    return;
  }

  const formData = new FormData();
  formData.append("file", file);
  setUploadMessage("Uploading...", "muted");
  const response = await fetch("/Home/UploadEntityImage", {
    method: "POST",
    body: formData
  });
  const payload = await response.json().catch(() => null);
  if (!response.ok) {
    setUploadMessage(payload?.error || `Upload failed (${response.status}).`, "error");
    return;
  }

  await upsertAsset(payload.fileName, payload.url, "");
  setUploadMessage(`Uploaded ${payload.fileName}. Add a description below so suggestions can use it.`, "success");
  el.imageFile.value = "";
  await loadImages();
  const uploaded = state.images.find(image => image.url === payload.url) || payload;
  selectImage(uploaded);
}

async function upsertAsset(fileName, url, description) {
  const response = await fetch(`${backendBaseUrl}/api/entity-visual-assets`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ fileName, url, description })
  });
  const payload = await response.json().catch(() => null);
  if (!response.ok || payload?.success === false) {
    throw new Error(payload?.error || `Could not save image metadata (${response.status}).`);
  }
  return payload.assets?.[0];
}

function renderImages() {
  el.imageList.innerHTML = "";
  if (!state.images.length) {
    el.imageList.innerHTML = '<div class="db-mgn-empty">No images uploaded yet.</div>';
    return;
  }

  const fragment = document.createDocumentFragment();
  state.images.forEach(image => {
    const item = document.createElement("article");
    item.className = "db-mgn-image-item";
    item.classList.toggle("selected", image.url === state.selectedImageUrl);
    item.title = image.fileName;
    item.innerHTML = `
      <button type="button" class="db-mgn-image-pick" data-pick>
        <img src="${escapeHtml(image.url)}" alt="" loading="lazy" />
        <span>${escapeHtml(image.fileName)}</span>
      </button>
      <label>Description<textarea data-description rows="3" placeholder="Describe what this visual represents">${escapeHtml(image.description || "")}</textarea></label>
      <div class="db-mgn-image-actions">
        <button class="btn btn-sm btn-outline-secondary" type="button" data-save-description>Save description</button>
        <button class="db-mgn-icon-btn db-mgn-danger-btn" type="button" title="Delete image" aria-label="Delete image" data-delete-image>${iconSvg("trash")}</button>
      </div>`;
    item.querySelector("[data-pick]").addEventListener("click", () => selectImage(image));
    item.querySelector("[data-save-description]").addEventListener("click", () => saveAssetDescription(image, item.querySelector("[data-description]").value));
    item.querySelector("[data-delete-image]").addEventListener("click", () => deleteImage(image));
    fragment.appendChild(item);
  });
  el.imageList.appendChild(fragment);
}

async function deleteImage(image) {
  const confirmed = window.confirm(`Delete ${image.fileName}? Entities using this image will have their visual URL cleared.`);
  if (!confirmed) return;

  setStatus("Deleting image");
  const response = await fetch("/Home/DeleteEntityImage", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ fileName: image.fileName, url: image.url })
  });
  const payload = await response.json().catch(() => null);
  if (!response.ok || payload?.success === false) {
    setStatus(payload?.error || `Delete failed (${response.status})`);
    return;
  }

  if (state.selectedImageUrl === image.url) {
    state.selectedImageUrl = null;
    state.selectedImageFileName = null;
    el.selectedImage.textContent = "Select an uploaded image.";
  }
  [...state.suggestionsByEntityId.entries()].forEach(([entityId, suggestion]) => {
    if (suggestion.url === image.url) state.suggestionsByEntityId.delete(entityId);
  });
  state.images = state.images.filter(item => item.url !== image.url);
  await Promise.all([loadImages(), loadEntities()]);
  setStatus("Image deleted");
}

async function saveAssetDescription(image, description) {
  setStatus("Saving image description");
  const response = await fetch(`${backendBaseUrl}/api/entity-visual-assets/description`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ assetId: image.id, description })
  });
  const payload = await response.json().catch(() => null);
  if (!response.ok || payload?.success === false) {
    setStatus(payload?.error || `Save failed (${response.status})`);
    return;
  }
  const updated = payload.assets?.[0];
  if (updated) state.images = state.images.map(item => item.id === updated.id ? updated : item);
  setStatus("Image description saved");
  renderImages();
}

function selectImage(image) {
  state.selectedImageUrl = image.url;
  state.selectedImageFileName = image.fileName;
  el.selectedImage.innerHTML = `<span>Selected:</span> <strong>${escapeHtml(image.fileName)}</strong> <code>${escapeHtml(image.url)}</code>`;
  renderImages();
  renderEntities();
}

function renderTabs() {
  el.tabs.innerHTML = "";
  getEntityTypes().forEach(type => {
    const count = state.entities.filter(entity => entity.label === type).length;
    const button = document.createElement("button");
    button.type = "button";
    button.className = "db-mgn-tab";
    button.classList.toggle("active", type === state.activeType);
    button.textContent = `${type} (${count})`;
    button.addEventListener("click", () => {
      state.activeType = type;
      renderTabs();
      renderEntities();
    });
    el.tabs.appendChild(button);
  });
}

function getEntityTypes() {
  return [...new Set(state.entities.map(entity => entity.label).filter(Boolean))].sort((a, b) => a.localeCompare(b));
}

function getVisibleEntities() {
  return state.entities.filter(entity => {
    if (state.activeType && entity.label !== state.activeType) return false;
    if (!state.search) return true;
    return `${entity.label} ${entity.name} ${entity.description || ""}`.toLowerCase().includes(state.search);
  });
}

function renderEntities() {
  el.entityList.innerHTML = "";
  const mode = getVisualMode();
  const filtered = getVisibleEntities();

  if (!filtered.length) {
    el.entityList.innerHTML = '<div class="db-mgn-empty">No matching entities.</div>';
    return;
  }

  const table = document.createElement("div");
  table.className = "db-mgn-entity-table";
  table.innerHTML = `
    <div class="db-mgn-entity-table-header" role="row">
      <div class="db-mgn-selection-header"><input type="checkbox" data-selection-menu aria-label="Selection options" title="Selection options" /></div>
      <div>Visual</div>
      <div>Entity Name</div>
      <div>Description</div>
      <div>Visual URLs</div>
    </div>`;
  const selectionMenu = table.querySelector("[data-selection-menu]");
  selectionMenu.addEventListener("click", event => {
    event.preventDefault();
    openSelectionDialog(selectionMenu);
  });
  updateSelectionMenuState(selectionMenu, filtered);
  const fragment = document.createDocumentFragment();
  filtered.forEach(entity => {
    const row = document.createElement("article");
    row.className = "db-mgn-entity-row";
    const imageUrl = entity.imageUrl || entity.image_url || "";
    const pictogramUrl = entity.pictogramUrl || entity.pictogram_url || "";
    const suggestion = state.suggestionsByEntityId.get(entity.id);
    const previewUrl = suggestion?.url || getPreviewUrl(entity);
    const hasPreview = Boolean(previewUrl);
    row.innerHTML = `
      <label class="db-mgn-entity-check"><input type="checkbox" data-select-entity ${state.selectedEntityIds.has(entity.id) ? "checked" : ""} /></label>
      <div class="db-mgn-entity-preview ${suggestion ? "has-suggestion" : ""}">
        <div class="db-mgn-preview-frame ${hasPreview ? "" : "empty"}">${hasPreview ? `<img src="${escapeHtml(previewUrl)}" alt="" loading="lazy" />` : "No visual"}</div>
        <div class="db-mgn-preview-actions">
          <button class="db-mgn-icon-btn" type="button" title="Assign selected ${escapeHtml(mode)}" aria-label="Assign selected ${escapeHtml(mode)}" data-assign>${iconSvg("pencil")}</button>
          <button class="db-mgn-icon-btn db-mgn-copilot-btn" type="button" title="Suggest visual" aria-label="Suggest visual" data-suggest-one>${iconSvg("copilot")}</button>
          <button class="db-mgn-icon-btn db-mgn-clear-visual-btn" type="button" title="Clear image and pictogram" aria-label="Clear image and pictogram" data-clear>${iconSvg("eraser")}</button>
        </div>
        ${suggestion ? `<div class="db-mgn-suggestion-note">${escapeHtml(suggestion.reason)}</div><div class="db-mgn-suggestion-actions"><button class="btn btn-sm btn-primary" type="button" data-accept-suggestion>Accept</button><button class="btn btn-sm btn-outline-secondary" type="button" data-cancel-suggestion>Cancel</button></div>` : ""}
      </div>
      <div class="db-mgn-entity-name"><strong>${escapeHtml(entity.name)}</strong><em>${escapeHtml(entity.label)}</em></div>
      <div class="db-mgn-description-cell">
        <textarea data-entity-description rows="3" placeholder="Entity description used for visual suggestions">${escapeHtml(entity.description || "")}</textarea>
        <button class="db-mgn-icon-btn" type="button" title="Save description" aria-label="Save description" data-save-entity-description>${iconSvg("save")}</button>
      </div>
      <div class="db-mgn-url-cell" title="img url: ${escapeHtml(imageUrl || "not set")}\npicto url: ${escapeHtml(pictogramUrl || "not set")}"><div><strong>img url:</strong> ${imageUrl ? escapeHtml(imageUrl) : "not set"}</div><div><strong>picto url:</strong> ${pictogramUrl ? escapeHtml(pictogramUrl) : "not set"}</div></div>
      `;
    row.querySelector("[data-select-entity]").addEventListener("change", event => {
      if (event.target.checked) state.selectedEntityIds.add(entity.id);
      else state.selectedEntityIds.delete(entity.id);
      updateSelectionMenuState(selectionMenu, filtered);
    });
    row.querySelector("[data-save-entity-description]").addEventListener("click", () => saveEntityDescription(entity, row.querySelector("[data-entity-description]").value));
    row.querySelector("[data-suggest-one]").addEventListener("click", () => suggestVisuals([entity.id]));
    row.querySelector("[data-accept-suggestion]")?.addEventListener("click", () => applySuggestionForEntity(entity.id));
    row.querySelector("[data-cancel-suggestion]")?.addEventListener("click", () => cancelSuggestion(entity.id));
    row.querySelector("[data-assign]").addEventListener("click", () => assignVisual(entity, mode));
      row.querySelector("[data-clear]").addEventListener("click", () => clearAllVisuals(entity));
    fragment.appendChild(row);
  });
  table.appendChild(fragment);
  el.entityList.appendChild(table);
}

async function saveEntityDescription(entity, description) {
  setStatus("Saving entity description");
  const response = await fetch(`${backendBaseUrl}/api/entities/description`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ entityId: entity.id, description })
  });
  const payload = await response.json().catch(() => null);
  if (!response.ok || payload?.success === false) {
    setStatus(payload?.error || `Save failed (${response.status})`);
    return;
  }
  updateEntity(payload.entities?.[0]);
  setStatus("Entity description saved");
}

async function assignVisual(entity, mode) {
  if (!state.selectedImageUrl) {
    setStatus("Select an image first");
    return;
  }
  await updateVisual(entity.id, mode, state.selectedImageUrl);
}

async function clearVisual(entity, mode) {
  await updateVisual(entity.id, mode, null);
}

async function clearAllVisuals(entity) {
  setStatus("Clearing visual");
  await updateVisual(entity.id, "image", null, { render: false });
  await updateVisual(entity.id, "pictogram", null);
}

async function updateVisual(entityId, mode, url, options = {}) {
  setStatus("Saving");
  const response = await fetch(`${backendBaseUrl}/api/entities/visual`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ entityId, visualKind: mode, url })
  });
  const payload = await response.json().catch(() => null);
  if (!response.ok || payload?.success === false) {
    setStatus(payload?.error || `Save failed (${response.status})`);
    return;
  }
  updateEntity(payload.entities?.[0], { render: options.render !== false });
  setStatus("Saved");
}

function updateEntity(updated, options = {}) {
  if (!updated) return;
  state.entities = state.entities.map(entity => entity.id === updated.id ? updated : entity);
  if (options.render !== false) renderEntities();
}

function selectEntities(ids) {
  ids.forEach(id => state.selectedEntityIds.add(id));
  closeSelectionDialog();
  renderEntities();
}

function deselectEntities(ids) {
  ids.forEach(id => state.selectedEntityIds.delete(id));
  closeSelectionDialog();
  renderEntities();
}

function openSelectionDialog(anchor) {
  if (!el.selectionDialog) return;
  if (typeof el.selectionDialog.showModal !== "function") {
    el.selectionDialog.setAttribute("open", "");
    return;
  }

  if (!el.selectionDialog.open) {
    el.selectionDialog.showModal();
  }
  const anchorBox = anchor.getBoundingClientRect();
  el.selectionDialog.style.left = `${Math.max(12, anchorBox.left)}px`;
  el.selectionDialog.style.top = `${Math.min(window.innerHeight - 120, anchorBox.bottom + 8)}px`;
}

function closeSelectionDialog() {
  if (!el.selectionDialog?.open) return;
  if (typeof el.selectionDialog.close === "function") el.selectionDialog.close();
  else el.selectionDialog.removeAttribute("open");
}

function updateSelectionMenuState(input, visibleEntities) {
  const visibleIds = visibleEntities.map(entity => entity.id);
  const selectedCount = visibleIds.filter(id => state.selectedEntityIds.has(id)).length;
  input.checked = visibleIds.length > 0 && selectedCount === visibleIds.length;
  input.indeterminate = selectedCount > 0 && selectedCount < visibleIds.length;
}

async function suggestVisuals(entityIdsOverride = null) {
  const entityIds = entityIdsOverride || [...state.selectedEntityIds];
  if (!entityIds.length) {
    setStatus("Select entities first");
    return;
  }
  setStatus("Suggesting visuals");
  const response = await fetch(`${backendBaseUrl}/api/entities/visual-suggestions`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ visualKind: getVisualMode(), entityIds, parallelism: getLlmParallelism() })
  });
  const payload = await response.json().catch(() => null);
  if (!response.ok || payload?.success === false) {
    setStatus(payload?.error || `Suggestion failed (${response.status})`);
    return;
  }
  const nextSuggestions = entityIdsOverride ? new Map(state.suggestionsByEntityId) : new Map();
  (payload.suggestions || []).forEach(suggestion => nextSuggestions.set(suggestion.entityId, suggestion));
  state.suggestionsByEntityId = nextSuggestions;
  setStatus(`${state.suggestionsByEntityId.size} suggestions ready`);
  renderEntities();
}

async function applySuggestions() {
  const mode = getVisualMode();
  const suggestions = [...state.suggestionsByEntityId.values()].filter(suggestion => state.selectedEntityIds.has(suggestion.entityId));
  if (!suggestions.length) {
    setStatus("No suggestions for selected entities");
    return;
  }
  setStatus("Applying suggestions");
  for (const suggestion of suggestions) {
    await updateVisual(suggestion.entityId, mode, suggestion.url);
  }
  setStatus(`Applied ${suggestions.length} suggestions`);
}

async function applySuggestionForEntity(entityId) {
  const suggestion = state.suggestionsByEntityId.get(entityId);
  if (!suggestion) {
    setStatus("No suggestion for this entity");
    return;
  }

  await updateVisual(entityId, getVisualMode(), suggestion.url);
  state.suggestionsByEntityId.delete(entityId);
  renderEntities();
}

function cancelSuggestion(entityId) {
  state.suggestionsByEntityId.delete(entityId);
  renderEntities();
}

function getPreviewUrl(entity) {
  return entity.imageUrl || entity.image_url || entity.pictogramUrl || entity.pictogram_url || "";
}

function getLlmParallelism() {
  const value = Number.parseInt(el.parallelismInput?.value || "2", 10);
  return Math.max(1, Math.min(8, Number.isFinite(value) ? value : 2));
}

function getAssignedUrl(entity, mode) {
  return mode === "image" ? entity.imageUrl || entity.image_url : entity.pictogramUrl || entity.pictogram_url;
}

function getVisualMode() {
  return el.visualMode?.value || "image";
}

function setStatus(text) {
  el.status.textContent = text;
}

function setUploadMessage(text, kind) {
  el.uploadMessage.className = `db-mgn-message db-mgn-message-${kind}`;
  el.uploadMessage.textContent = text;
}

function escapeHtml(value) {
  return String(value ?? "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/\"/g, "&quot;").replace(/'/g, "&#39;");
}

function iconSvg(name) {
  if (name === "pencil") return '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20h4l10.5-10.5a2.1 2.1 0 0 0-3-3L5 17v3Z"/><path d="m13.5 7.5 3 3"/></svg>';
  if (name === "save") return '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 4h12l2 2v14H5z"/><path d="M8 4v6h8V4M8 20v-7h8v7"/></svg>';
  if (name === "eraser") return '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m7 21-4-4 9.5-9.5 4 4L7 21Z"/><path d="m14 5 5 5M7 21h13M11.5 17.5 6.5 12.5"/></svg>';
  if (name === "trash") return '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="m6 6 1 15h10l1-15"/><path d="M10 11v6M14 11v6"/></svg>';
  return '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3 4 7v10l8 4 8-4V7z"/><path d="M8.5 9.5h7M8.5 14.5h7M12 7v10"/></svg>';
}
