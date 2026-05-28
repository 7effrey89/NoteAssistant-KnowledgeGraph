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
      <button class="btn btn-sm btn-outline-secondary" type="button" data-save-description>Save description</button>`;
    item.querySelector("[data-pick]").addEventListener("click", () => selectImage(image));
    item.querySelector("[data-save-description]").addEventListener("click", () => saveAssetDescription(image, item.querySelector("[data-description]").value));
    fragment.appendChild(item);
  });
  el.imageList.appendChild(fragment);
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

  const fragment = document.createDocumentFragment();
  filtered.forEach(entity => {
    const row = document.createElement("article");
    row.className = "db-mgn-entity-row";
    const imageUrl = entity.imageUrl || entity.image_url || "";
    const pictogramUrl = entity.pictogramUrl || entity.pictogram_url || "";
    const suggestion = state.suggestionsByEntityId.get(entity.id);
    row.innerHTML = `
      <label class="db-mgn-entity-check"><input type="checkbox" data-select-entity ${state.selectedEntityIds.has(entity.id) ? "checked" : ""} /></label>
      <div class="db-mgn-entity-main">
        <strong>${escapeHtml(entity.name)}</strong>
        <span>${escapeHtml(entity.label)}</span>
        <textarea data-entity-description rows="2" placeholder="Entity description used for visual suggestions">${escapeHtml(entity.description || "")}</textarea>
        <small>Image: ${imageUrl ? escapeHtml(imageUrl) : "not set"}</small>
        <small>Pictogram: ${pictogramUrl ? escapeHtml(pictogramUrl) : "not set"}</small>
        ${suggestion ? `<small class="db-mgn-suggestion">Suggested: ${escapeHtml(suggestion.url)} - ${escapeHtml(suggestion.reason)}</small>` : ""}
      </div>
      <div class="db-mgn-entity-actions">
        <button class="btn btn-sm btn-outline-secondary" type="button" data-save-entity-description>Save description</button>
        <button class="btn btn-sm btn-outline-primary" type="button" data-suggest-one>Suggest</button>
        <button class="btn btn-sm btn-primary" type="button" data-apply-one ${suggestion ? "" : "disabled"}>Apply suggestion</button>
        <button class="btn btn-sm btn-success" type="button" data-assign>Assign ${escapeHtml(mode)}</button>
        <button class="btn btn-sm btn-outline-secondary" type="button" data-clear>Clear ${escapeHtml(mode)}</button>
      </div>`;
    row.querySelector("[data-select-entity]").addEventListener("change", event => {
      if (event.target.checked) state.selectedEntityIds.add(entity.id);
      else state.selectedEntityIds.delete(entity.id);
    });
    row.querySelector("[data-save-entity-description]").addEventListener("click", () => saveEntityDescription(entity, row.querySelector("[data-entity-description]").value));
    row.querySelector("[data-suggest-one]").addEventListener("click", () => suggestVisuals([entity.id]));
    row.querySelector("[data-apply-one]").addEventListener("click", () => applySuggestionForEntity(entity.id));
    row.querySelector("[data-assign]").addEventListener("click", () => assignVisual(entity, mode));
    row.querySelector("[data-clear]").addEventListener("click", () => clearVisual(entity, mode));
    fragment.appendChild(row);
  });
  el.entityList.appendChild(fragment);
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

async function updateVisual(entityId, mode, url) {
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
  updateEntity(payload.entities?.[0]);
  setStatus("Saved");
}

function updateEntity(updated) {
  if (!updated) return;
  state.entities = state.entities.map(entity => entity.id === updated.id ? updated : entity);
  renderEntities();
}

function selectEntities(ids) {
  ids.forEach(id => state.selectedEntityIds.add(id));
  renderEntities();
}

function deselectEntities(ids) {
  ids.forEach(id => state.selectedEntityIds.delete(id));
  renderEntities();
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
