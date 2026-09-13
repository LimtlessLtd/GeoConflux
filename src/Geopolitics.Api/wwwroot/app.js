/**
 * Dashboard client.
 *
 * The page runs in one of two modes, decided at load time:
 *
 *   live   - an ASP.NET Core backend is present. Incidents come from the REST API and updates
 *            arrive over SignalR, with polling as a fallback.
 *   static - no backend (for example GitHub Pages, which serves files only). Data comes from a
 *            snapshot that a real pipeline run exported at build time.
 *
 * Three rules shape this file:
 *  - Realtime is an optimisation, never the source of truth. If the hub is unavailable the
 *    dashboard degrades to polling, and if the API is unavailable it degrades to the snapshot.
 *  - Every URL is relative. The site is served from a subpath on GitHub Pages, so a leading "/"
 *    would resolve to the domain root and break every request.
 *  - Nothing is presented as more certain than it is. Demo data is labelled wherever it appears,
 *    and the static mode says plainly that it is a recording rather than a live system.
 */

/**
 * The logic below that does not need a browser lives in ./lib, so that it can be tested.
 *
 * Everything in this file reaches for the DOM, the network, or the globe, and is verified by
 * loading the page. Everything in ./lib is a pure function of its arguments — escaping, time
 * bases, provenance wording, filtering, source selection — and is covered by tests/dashboard.
 * The split is drawn at that line and nowhere else: a helper moves out when it stops needing a
 * document, not because it looked tidier elsewhere.
 *
 * The wrapper below is now redundant, because a module already has its own scope. It is kept so
 * that this file's structure and indentation still match the version that was verified by hand.
 */
import { escapeHtml } from './lib/html.js';
import { asCount, colourFor, humanise, plural } from './lib/format.js';
import {
  exactTimeHint, formatDate, formatRelative, formatTime, setRunBasis, statesOwnTime,
} from './lib/time.js';
import { confidenceChip, modelOpinion } from './lib/classification.js';
import { canHideDemoNotice, provenanceChip, provenanceSummary } from './lib/provenance.js';
import { selectVisibleIncidents } from './lib/incidents.js';
import { resolveDataSource as resolveSourceOrder } from './lib/datasource.js';
import {
  coverageSummary, gapNotes, hasCoverage, lexiconNote, orderByPrecision, precisionLabel,
} from './lib/coverage.js';

(() => {
  'use strict';

  /**
   * The provenance chip for one record, already escaped. Empty for a polled feed item, which is the
   * unremarkable case — chipping everything would cost the DEMO label the attention it exists to
   * command.
   */
  const chipFor = (record) => {
    const chip = provenanceChip(record);
    if (!chip) {
      return '';
    }

    return `<span class="${chip.className}" title="${escapeHtml(chip.title)}">${escapeHtml(chip.text)}</span>`;
  };

  const POLL_INTERVAL_MS = 15000;
  const MAX_FEED_ITEMS = 60;
  const REPLAY_STEP_MS = 900;
  const SIGNALR_CLIENT_URL = 'https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.7/signalr.min.js';

  /**
   * How far above an incident the camera settles when flying to it, in metres.
   *
   * Not as close as the imagery now allows, and deliberately so. Most incidents are placed at a
   * gazetteer centroid for a named region — "Red Sea" resolves to a point in the middle of the Red
   * Sea — so diving to street level would render a precision the data does not have. This is a
   * regional framing that shows the coastline and surrounding context; a visitor who wants more
   * detail can zoom, which is now worth doing.
   */
  const FLY_TO_RANGE_METRES = 4.0e5;

  /** How long a remote basemap gets to load before the offline texture is used instead. */
  const BASEMAP_TIMEOUT_MS = 12000;

  const BASEMAP_STORAGE_KEY = 'geoconflux.basemap';

  /**
   * Selectable basemaps.
   *
   * Esri's public map services are used because they serve high-resolution imagery to zoom 23 with
   * no API key, which keeps the "runs without credentials" property the rest of the project depends
   * on. Google's tiles would need a key and their terms do not permit this use. Cesium's own world
   * imagery and 3D terrain would need an ion token, which is the same problem.
   *
   * `offline` is the texture bundled with the CesiumJS distribution. It is coarse, but it is the
   * only one that survives a tile host being unreachable, so it is the fallback rather than a
   * decorative extra.
   */
  const BASEMAPS = {
    satellite: {
      label: 'Satellite',
      url: 'https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer',

      // Imagery alone shows terrain but not jurisdiction. For a geopolitical view the borders and
      // place names are the point, so they are layered on top rather than offered separately.
      referenceUrl: 'https://services.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer',
    },
    streets: {
      label: 'Map',
      url: 'https://services.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer',
      referenceUrl: null,
    },
    terrain: {
      label: 'Terrain',
      url: 'https://services.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer',
      referenceUrl: null,
    },
    offline: {
      label: 'Offline',
      url: null,
      referenceUrl: null,
    },
  };

  const dom = {
    incidentList: document.querySelector('#incidentList'),
    feedList: document.querySelector('#feedList'),
    details: document.querySelector('#details'),
    incidentCount: document.querySelector('#incidentCount'),
    feedCount: document.querySelector('#feedCount'),
    status: document.querySelector('#connectionStatus'),
    dot: document.querySelector('#connectionDot'),
    fallback: document.querySelector('#globeFallback'),
    demoNotice: document.querySelector('#demoNotice'),
    demoNoticeText: document.querySelector('#demoNoticeText'),
    severityFilter: document.querySelector('#severityFilter'),
    typeFilter: document.querySelector('#typeFilter'),
    locatedOnly: document.querySelector('#locatedOnly'),
    replayButton: document.querySelector('#replayButton'),
    feedHint: document.querySelector('#feedHint'),
    aboutModeText: document.querySelector('#aboutModeText'),
    submitForm: document.querySelector('#submitForm'),
    submitSource: document.querySelector('#submitSource'),
    submitTitle: document.querySelector('#submitTitle'),
    submitContent: document.querySelector('#submitContent'),
    submitLocation: document.querySelector('#submitLocation'),
    submitButton: document.querySelector('#submitButton'),
    submitStatus: document.querySelector('#submitStatus'),
    submitDisabled: document.querySelector('#submitDisabled'),
    chokepointList: document.querySelector('#chokepointList'),
    coverageList: document.querySelector('#coverageList'),
    coverageGaps: document.querySelector('#coverageGaps'),
    chokepointCount: document.querySelector('#chokepointCount'),
    chokepointMethod: document.querySelector('#chokepointMethod'),
    basemapFilter: document.querySelector('#basemapFilter'),
    basemapNote: document.querySelector('#basemapNote'),
    orientationControl: document.querySelector('#orientationControl'),
    northUp: document.querySelector('#northUp'),
    compassRose: document.querySelector('.compass-rose'),
    analyticsWindow: document.querySelector('#analyticsWindow'),
    analyticsPeriod: document.querySelector('#analyticsPeriod'),
    analyticsBody: document.querySelector('#analyticsBody'),
  };

  /** Full, authoritative state. Replay renders a subset of this rather than mutating it. */
  const incidents = new Map();
  let feed = [];
  const entities = new Map();

  /** What each live marker was drawn from, so an unchanged marker is left alone. */
  const entitySignatures = new Map();

  let viewer = null;
  let selectedIncidentId = null;
  let pollTimer = null;
  let dataSource = null;

  /** When active, only these incidents render, with counts as they stood at that point. */
  const replay = { active: false, revealed: new Set(), counts: new Map(), played: new Set(), timer: null };

  /**
   * Analytics windows the backend supports, and the reports already fetched for them.
   *
   * Cached per token because a report is a point-in-time answer: re-fetching on every tab switch
   * would make the same window silently change its numbers as the visitor clicked around, which
   * reads as instability rather than as freshness.
   */
  const ANALYTICS_WINDOWS = [
    { token: '24h', label: 'Last 24 hours' },
    { token: '7d', label: 'Last 7 days' },
    { token: '30d', label: 'Last 30 days' },
    { token: '90d', label: 'Last 90 days' },
  ];
  const analyticsCache = new Map();
  let analyticsLoaded = false;

  function setStatus(state, message) {
    dom.dot.dataset.state = state;
    dom.status.textContent = message;
  }

  // ---------------------------------------------------------------- data sources

  /** Reads from the running backend. */
  const apiSource = {
    mode: 'live',
    async probe() {
      const response = await fetch('./api/incidents?take=1', { headers: { Accept: 'application/json' } });

      // A static host with an SPA fallback answers this with 200 and an HTML page. Requiring JSON
      // stops the dashboard adopting a "backend" that only ever returns index.html.
      return response.ok
        && (response.headers.get('content-type') ?? '').includes('application/json');
    },
    async loadIncidents() {
      const response = await fetch('./api/incidents?take=200', { headers: { Accept: 'application/json' } });
      if (!response.ok) throw new Error(`Incident endpoint returned ${response.status}`);
      return response.json();
    },
    async loadObservations() {
      const response = await fetch('./api/observations?take=60', { headers: { Accept: 'application/json' } });
      return response.ok ? response.json() : [];
    },
    async loadEvidence(incidentId) {
      const response = await fetch(`./api/observations/by-incident/${incidentId}`, { headers: { Accept: 'application/json' } });
      if (!response.ok) throw new Error(`Evidence endpoint returned ${response.status}`);
      return response.json();
    },
    async loadChokepoints() {
      // Thirty days, matching the window the snapshot exporter uses, so the panel reads the same
      // either side of the live/static divide.
      const response = await fetch('./api/spatial/chokepoints?windowHours=720', { headers: { Accept: 'application/json' } });
      if (!response.ok) return { method: '', chokepoints: [] };
      const payload = await response.json();
      return { method: payload.method ?? '', chokepoints: payload.chokepoints ?? [] };
    },
    async loadAnalytics(token) {
      const response = await fetch(`./api/analytics?window=${encodeURIComponent(token)}`, { headers: { Accept: 'application/json' } });
      if (!response.ok) throw new Error(`Analytics endpoint returned ${response.status}`);
      return response.json();
    },
    async loadCoverage() {
      const response = await fetch('./api/analytics/coverage', { headers: { Accept: 'application/json' } });
      return response.ok ? response.json() : null;
    },
  };

  /** Reads the snapshot a real pipeline run exported at build time. */
  const staticSource = {
    mode: 'static',
    meta: null,
    async probe() {
      const response = await fetch('./data/meta.json', { headers: { Accept: 'application/json' } });
      if (!response.ok) return false;
      this.meta = await response.json();
      return true;
    },
    async loadIncidents() {
      const response = await fetch('./data/incidents.json', { headers: { Accept: 'application/json' } });
      if (!response.ok) throw new Error(`Snapshot incidents returned ${response.status}`);
      return response.json();
    },
    async loadObservations() {
      const response = await fetch('./data/observations.json', { headers: { Accept: 'application/json' } });
      return response.ok ? response.json() : [];
    },
    async loadEvidence(incidentId) {
      const response = await fetch(`./data/evidence/${incidentId}.json`, { headers: { Accept: 'application/json' } });
      if (!response.ok) throw new Error(`Snapshot evidence returned ${response.status}`);
      return response.json();
    },
    async loadChokepoints() {
      const response = await fetch('./data/chokepoints.json', { headers: { Accept: 'application/json' } });

      // An older snapshot simply has no such file, which is an empty panel rather than an error.
      if (!response.ok) return { method: '', chokepoints: [] };
      return { method: this.meta?.spatialMethod ?? '', chokepoints: await response.json() };
    },
    async loadAnalytics(token) {
      // One file per window, written at build time by the same service the API calls.
      const response = await fetch(`./data/analytics/${encodeURIComponent(token)}.json`, { headers: { Accept: 'application/json' } });
      if (!response.ok) throw new Error(`Snapshot analytics returned ${response.status}`);
      return response.json();
    },
    async loadCoverage() {
      // A snapshot published before this panel existed has no such file, which leaves the panel
      // empty rather than failing the load.
      const response = await fetch('./data/coverage.json', { headers: { Accept: 'application/json' } });
      return response.ok ? response.json() : null;
    },
  };

  /**
   * Prefers the live backend, because it is strictly more capable. The snapshot is the fallback,
   * which is what makes the same build work both locally and on a static host.
   */
  async function resolveDataSource() {
    return resolveSourceOrder([apiSource, staticSource]);
  }

  // ---------------------------------------------------------------- globe

  async function initialiseGlobe() {
    if (!window.Cesium) {
      dom.fallback.hidden = false;
      hideOrientationControl();
      return;
    }

    try {
      viewer = new Cesium.Viewer('cesiumContainer', {
        animation: false,
        baseLayerPicker: false,
        geocoder: false,
        homeButton: false,
        infoBox: false,
        navigationHelpButton: false,
        sceneModePicker: false,
        timeline: false,
        fullscreenButton: false,
        selectionIndicator: false,

        // Imagery is attached below rather than here, so a tile host that is slow or unreachable
        // costs detail instead of costing the globe.
        baseLayer: false,
      });

      // What shows through before tiles arrive, and in the gaps if they never do. Matching the
      // panel colour makes the load read as the page filling in rather than as a broken globe.
      viewer.scene.globe.baseColor = Cesium.Color.fromCssColorString('#0d141f');
      viewer.scene.globe.showGroundAtmosphere = true;
      viewer.camera.setView({ destination: Cesium.Cartesian3.fromDegrees(30, 24, 22000000) });
      viewer.selectedEntityChanged.addEventListener((entity) => {
        const id = entity?.properties?.incidentId?.getValue();
        if (id) selectIncident(id);
      });

      // Every frame, because the heading changes continuously while the globe is being dragged and
      // there is no event that reports it reliably. The handler exits immediately unless the whole
      // degree moved, so the per-frame cost is a comparison.
      viewer.scene.postRender.addEventListener(updateCompass);
      dom.northUp.addEventListener('click', orientNorthUp);
      updateCompass();

      await applyBasemap(preferredBasemap());
    } catch (error) {
      // A globe failure must not take the rest of the dashboard with it.
      console.error('Globe initialisation failed', error);
      dom.fallback.hidden = false;
      hideOrientationControl();
      viewer = null;
    }
  }

  /** There is nothing to orient without a globe, and an inert control is worse than no control. */
  function hideOrientationControl() {
    if (dom.orientationControl) dom.orientationControl.hidden = true;
  }

  /**
   * Puts north back at the top without otherwise moving the camera.
   *
   * Heading and roll are reset; position and pitch are left alone. The request was to orient the
   * view, not to return home, so a visitor who has flown to a strait and tilted the camera keeps
   * both and only gets their bearings back. Cesium measures heading and pitch in the local
   * east-north-up frame at the camera, so holding pitch and zeroing heading means exactly "same
   * place, same tilt, north upward".
   */
  function orientNorthUp() {
    if (!viewer) return;

    const camera = viewer.camera;

    camera.flyTo({
      destination: Cesium.Cartesian3.clone(camera.positionWC),
      orientation: { heading: 0, pitch: camera.pitch, roll: 0 },
      duration: 0.6,
    });
  }

  /** The bearing the needle is currently drawn at, so an unchanged heading costs no DOM writes. */
  let renderedHeading = null;

  /**
   * Points the needle at north and says, in the button's own tooltip, which way the camera faces.
   * <p>
   * Called from every rendered frame, so it does nothing at all unless the whole-degree bearing has
   * actually moved. Rotating by the negative of the heading is what keeps the needle pointing at
   * north rather than at the camera: face east and north is to the left.
   */
  function updateCompass() {
    if (!viewer || !dom.compassRose) return;

    const heading = Math.round(Cesium.Math.toDegrees(viewer.camera.heading));
    const bearing = ((heading % 360) + 360) % 360;

    if (bearing === renderedHeading) return;
    renderedHeading = bearing;

    dom.compassRose.style.transform = `rotate(${-bearing}deg)`;
    dom.northUp.classList.toggle('is-aligned', bearing === 0);
    dom.northUp.title = bearing === 0
      ? 'North is already at the top'
      : `The view faces ${bearing}° — click to put north at the top`;
  }

  /** The visitor's last choice, when there was one and it still exists. */
  function preferredBasemap() {
    try {
      const stored = window.localStorage?.getItem(BASEMAP_STORAGE_KEY);
      if (stored && BASEMAPS[stored]) return stored;
    } catch {
      // Storage can be unavailable (private mode, file://). A default is a fine outcome.
    }

    return 'satellite';
  }

  function rememberBasemap(key) {
    try {
      window.localStorage?.setItem(BASEMAP_STORAGE_KEY, key);
    } catch {
      // Not worth reporting: the choice simply will not persist.
    }
  }

  /**
   * Swaps the globe's imagery, falling back to the bundled offline texture if the remote host is
   * slow or unreachable.
   *
   * The fallback is the reason this is worth its length. The imagery is a third-party dependency
   * fetched at runtime, and the rest of this project is built so that an unavailable dependency
   * degrades one thing rather than breaking the page. A visitor on a blocked network gets the coarse
   * globe the site had before, plus a note saying why, instead of a blank sphere.
   */
  async function applyBasemap(key) {
    if (!viewer) return;

    const basemap = BASEMAPS[key] ?? BASEMAPS.satellite;
    let layers = null;

    if (basemap.url) {
      try {
        layers = await withTimeout(loadArcGisLayers(basemap), BASEMAP_TIMEOUT_MS);
      } catch (error) {
        console.warn(`Basemap "${key}" could not be loaded; falling back to the offline texture.`, error);
        setBasemapNote('Detailed imagery is unavailable — showing the offline basemap.');
      }
    }

    if (!layers) {
      layers = [await loadOfflineProvider()];
    } else {
      setBasemapNote('');
    }

    // Replaced only once the new imagery is in hand, so a failed swap never leaves a blank globe.
    viewer.imageryLayers.removeAll();
    layers.forEach((provider) => viewer.imageryLayers.addImageryProvider(provider));
    rememberBasemap(key);
  }

  async function loadArcGisLayers(basemap) {
    const options = {
      // Nothing in this dashboard queries the basemap, and leaving picking on makes every click on
      // the globe issue an identify request to a third-party service.
      enablePickFeatures: false,
    };

    const providers = [await Cesium.ArcGisMapServerImageryProvider.fromUrl(basemap.url, options)];

    if (basemap.referenceUrl) {
      providers.push(await Cesium.ArcGisMapServerImageryProvider.fromUrl(basemap.referenceUrl, options));
    }

    return providers;
  }

  /** Natural Earth II, bundled with the CesiumJS distribution. Coarse, but always available. */
  function loadOfflineProvider() {
    return Cesium.TileMapServiceImageryProvider.fromUrl(
      'https://cesium.com/downloads/cesiumjs/releases/1.126/Build/Cesium/Assets/Textures/NaturalEarthII',
    );
  }

  function setBasemapNote(message) {
    if (!dom.basemapNote) return;
    dom.basemapNote.textContent = message;
    dom.basemapNote.hidden = message.length === 0;
  }

  /** Bounds a promise, so an unresponsive host delays the globe rather than blocking it forever. */
  function withTimeout(promise, milliseconds) {
    let timer = null;

    return Promise.race([
      promise.finally(() => clearTimeout(timer)),
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error(`Timed out after ${milliseconds}ms`)), milliseconds);
      }),
    ]);
  }

  function syncGlobe(visible) {
    if (!viewer) return;

    const wanted = new Set(visible.filter((incident) => incident.location).map((incident) => incident.id));

    for (const [id, entity] of entities) {
      if (!wanted.has(id)) {
        viewer.entities.remove(entity);
        entities.delete(id);
        entitySignatures.delete(id);
      }
    }

    visible.forEach((incident) => {
      if (!incident.location) return;

      const sources = countFor(incident);

      // renderIncidents runs on every replay tick and on every selection. Recreating unchanged
      // markers each time made them flicker and aborted any flyTo already in flight, because its
      // target entity was deleted mid-flight.
      const isSelected = incident.id === selectedIncidentId;
      const signature = [
        incident.severity, incident.title, sources, isSelected,
        incident.location.latitude, incident.location.longitude,
      ].join('|');

      if (entities.has(incident.id) && entitySignatures.get(incident.id) === signature) return;

      const existing = entities.get(incident.id);
      if (existing) viewer.entities.remove(existing);

      const entity = viewer.entities.add({
        id: `incident-${incident.id}`,
        position: Cesium.Cartesian3.fromDegrees(incident.location.longitude, incident.location.latitude),
        point: {
          color: Cesium.Color.fromCssColorString(colourFor(incident.severity)),
          // Corroborated incidents read as more substantial without implying extra certainty.
          pixelSize: (isSelected ? 4 : 0) + 10 + Math.min(8, sources * 2),
          outlineColor: isSelected ? Cesium.Color.fromCssColorString('#64dfdf') : Cesium.Color.WHITE,
          outlineWidth: isSelected ? 3 : 1,
        },
        label: {
          // Only the selected incident is labelled. Labelling them all produced overlapping text
          // wherever incidents cluster, which is exactly where the map matters most.
          show: isSelected,
          text: incident.title.length > 46 ? `${incident.title.slice(0, 45)}…` : incident.title,
          font: '600 12px system-ui, sans-serif',
          fillColor: Cesium.Color.WHITE,

          // A filled backing rather than an outline alone. Over satellite imagery the label sits on
          // whatever happens to be underneath it — a white rooftop, a sunlit street — and an
          // outlined glyph disappears into it. This is the same reason map labels have halos.
          showBackground: true,
          backgroundColor: Cesium.Color.fromCssColorString('rgba(7, 11, 18, 0.78)'),
          backgroundPadding: new Cesium.Cartesian2(7, 5),
          outlineColor: Cesium.Color.BLACK,
          outlineWidth: 2,
          style: Cesium.LabelStyle.FILL_AND_OUTLINE,
          pixelOffset: new Cesium.Cartesian2(0, -24),
          scaleByDistance: new Cesium.NearFarScalar(1.0e6, 1.0, 2.0e7, 0.55),
        },
        properties: { incidentId: incident.id },
      });

      entities.set(incident.id, entity);
      entitySignatures.set(incident.id, signature);
    });
  }

  // ---------------------------------------------------------------- rendering

  /** Evidence count, which during a replay reflects the run's progress rather than its end state. */
  function countFor(incident) {
    return replay.active
      ? (replay.counts.get(incident.id) ?? 0)
      : (incident.observationCount ?? 0);
  }

  function visibleIncidents() {
    return selectVisibleIncidents(incidents.values(), {
      severity: dom.severityFilter.value,
      type: dom.typeFilter.value,
      locatedOnly: dom.locatedOnly.checked,
    }, replay);
  }

  function renderIncidents() {
    const visible = visibleIncidents();
    dom.incidentCount.textContent = visible.length;
    dom.incidentList.replaceChildren();

    if (visible.length === 0) {
      const empty = document.createElement('p');
      empty.className = 'empty-state';
      empty.textContent = replay.active
        ? 'Waiting for the first observation of the run…'
        : 'No incidents match the current filters.';
      dom.incidentList.append(empty);
    }

    visible.forEach((incident) => {
      const sources = countFor(incident);
      const button = document.createElement('button');
      button.type = 'button';
      button.className = `incident-button${incident.id === selectedIncidentId ? ' is-selected' : ''}`;
      button.style.borderLeftColor = colourFor(incident.severity);
      button.innerHTML = `
        <h3>${escapeHtml(incident.title)}</h3>
        <div class="incident-meta">
          <span class="sev sev-${escapeHtml(incident.severity)}">${escapeHtml(incident.severity)}</span>
          <span>${escapeHtml(incident.eventType)}</span>
          <span title="${escapeHtml(exactTimeHint(incident.occurredAt))}">${escapeHtml(formatRelative(incident.occurredAt, incident.isDemo))}</span>
        </div>
        <div class="incident-conf">${confidenceChip(incident.classificationConfidence, incident.classificationMethod)}</div>
        <div class="incident-sub">
          ${incident.location ? escapeHtml(incident.location.name) : 'Location unresolved'}
          · ${sources} source${sources === 1 ? '' : 's'}
          ${sources > 1 ? '<span class="corr-chip">CORRELATED</span>' : ''}
          ${chipFor(incident)}
        </div>`;
      button.addEventListener('click', () => selectIncident(incident.id, true));
      dom.incidentList.append(button);
    });

    syncGlobe(visible);
    refreshTypeOptions();
  }

  function refreshTypeOptions() {
    // During a replay the dropdown must only offer what has actually appeared, or it reveals which
    // categories the run is going to produce before the run produces them.
    const pool = [...incidents.values()]
      .filter((incident) => !replay.active || replay.revealed.has(incident.id));
    const types = [...new Set(pool.map((incident) => incident.eventType))].sort();
    const current = dom.typeFilter.value;

    // Compare the actual values: a set that changes without changing size would slip past a
    // count-only check and leave the filter offering a category that no longer exists.
    const rendered = [...dom.typeFilter.options].slice(1).map((option) => option.value);
    if (rendered.length === types.length && rendered.every((value, index) => value === types[index])) {
      return;
    }

    dom.typeFilter.replaceChildren(new Option('All', ''));
    types.forEach((type) => dom.typeFilter.append(new Option(type, type)));
    dom.typeFilter.value = types.includes(current) ? current : '';
  }

  function renderFeed() {
    dom.feedCount.textContent = feed.length;
    dom.feedList.replaceChildren();

    if (feed.length === 0) {
      const empty = document.createElement('p');
      empty.className = 'empty-state';
      empty.textContent = replay.active ? 'Starting the run…' : 'No observations received yet.';
      dom.feedList.append(empty);
      return;
    }

    feed.forEach((observation) => {
      const item = document.createElement('button');
      item.type = 'button';
      // Assigned through className, not innerHTML, so no HTML parsing happens. Escaping here would
      // corrupt the class name rather than protect anything.
      item.className = `feed-item status-${String(observation.status ?? '').replace(/[^A-Za-z]/g, '')}`;
      item.innerHTML = `
        <div class="feed-head">
          <span class="feed-source">${escapeHtml(observation.sourceName)}</span>
          <span class="feed-time">${escapeHtml(formatRelative(observation.receivedAt, observation.isDemo))}</span>
        </div>
        <div class="feed-title">${escapeHtml(observation.title ?? observation.summary ?? 'Untitled observation')}</div>
        <div class="feed-meta">
          <span class="pill pill-${escapeHtml(observation.status)}">${escapeHtml(observation.status)}</span>
          <span>${escapeHtml(observation.eventType)}</span>
          ${observation.location
            ? `<span>${escapeHtml(observation.location.name)}</span>`
            : '<span class="muted">no coordinates</span>'}
          ${chipFor(observation)}
          ${observation.kind === 'Manual' ? '<span class="manual-chip">USER-SUBMITTED · UNVERIFIED</span>' : ''}
        </div>
        ${observation.status === 'Duplicate'
          ? ''
          : `<div class="feed-conf">${confidenceChip(observation.classificationConfidence, observation.classificationMethod)}
             ${observation.detectedLanguage && observation.detectedLanguage !== 'en'
               ? `<span class="lang-chip" title="Language of the original text, identified during enrichment.">${escapeHtml(observation.detectedLanguage)} → en</span>`
               : ''}</div>`}
        ${observation.status === 'Duplicate'
          ? '<div class="feed-note">Rejected as an exact re-delivery. Kept for audit.</div>'
          : ''}
        ${observation.locationResolutionNote
          ? `<div class="feed-note">${escapeHtml(observation.locationResolutionNote)}</div>`
          : ''}`;
      item.addEventListener('click', () => {
        if (observation.incidentId) selectIncident(observation.incidentId, true);
      });
      dom.feedList.append(item);
    });
  }

  async function selectIncident(id, flyTo = false) {
    const incident = incidents.get(id);
    if (!incident) return;

    selectedIncidentId = id;
    renderIncidents();

    const location = incident.location
      ? `${escapeHtml(incident.location.name)}${incident.location.countryCode ? ` · ${escapeHtml(incident.location.countryCode)}` : ''}
         <span class="coords">${incident.location.latitude.toFixed(3)}, ${incident.location.longitude.toFixed(3)}</span>`
      : '<span class="muted">Unresolved — no trustworthy coordinates were available</span>';

    const sources = countFor(incident);

    dom.details.innerHTML = `
      <div class="detail-badges">
        <span class="badge sev-${escapeHtml(incident.severity)}">${escapeHtml(incident.severity)}</span>
        <span class="badge">${escapeHtml(incident.eventType)}</span>
        ${incident.isDemo ? '<span class="badge demo-chip">DEMO DATA</span>' : (provenanceChip(incident) ? '<span class="badge collected-chip">COLLECTED</span>' : '')}
      </div>
      <h3>${escapeHtml(incident.title)}</h3>
      <p>${escapeHtml(incident.summary)}</p>
      <dl class="detail-grid">
        <dt>Location</dt><dd>${location}</dd>
        <dt>Reported</dt>
        <dd>
          ${escapeHtml(formatTime(incident.occurredAt))}
          <span class="muted">· ${escapeHtml(formatRelative(incident.occurredAt, incident.isDemo))}</span>
          <div class="muted assessment-note">
            The time the source itself gave. For a news report that is when it was published; for a
            structured feed it is the event date the provider recorded. It is not when this system
            received it — the evidence below carries both.
          </div>
        </dd>
        <dt>Evidence</dt><dd>${sources} correlated observation${sources === 1 ? '' : 's'}</dd>
        <dt>Assessment</dt>
        <dd>
          ${confidenceChip(incident.classificationConfidence, incident.classificationMethod)}
          <div class="muted assessment-note">
            Confidence in the classification, from the best-supported evidence linked here. It is not
            a probability that the event occurred.
          </div>
        </dd>
      </dl>
      <div class="evidence"><p class="muted">Loading evidence…</p></div>`;

    if (flyTo && viewer) {
      const entity = entities.get(id);

      if (entity) {
        viewer.flyTo(entity, {
          duration: 0.8,
          offset: new Cesium.HeadingPitchRange(0, Cesium.Math.toRadians(-90), FLY_TO_RANGE_METRES),
        }).catch(() => {});
      }
    }

    await renderEvidence(id);
  }

  async function renderEvidence(incidentId) {
    const container = dom.details.querySelector('.evidence');
    if (!container) return;

    try {
      const loaded = await dataSource.loadEvidence(incidentId);
      if (selectedIncidentId !== incidentId) return; // Selection moved on while we were fetching.

      // The snapshot stores an incident's complete evidence. Mid-replay only part of it has been
      // played, so showing all of it would contradict the count rendered directly above.
      const observations = replay.active
        ? loaded.filter((observation) => replay.played.has(observation.id))
        : loaded;

      container.innerHTML = `
        <h4>Source evidence</h4>
        ${observations.length === 0 ? '<p class="muted">No observations are linked to this incident.</p>' : ''}
        <ul class="evidence-list">
          ${observations.map((observation) => `
            <li>
              <span class="evidence-source">${escapeHtml(observation.sourceName)}</span>
              <span class="pill pill-${escapeHtml(observation.status)}">${escapeHtml(observation.status)}</span>
              <div>${escapeHtml(observation.title ?? '')}</div>
              <div class="muted evidence-times">
                ${statesOwnTime(observation)
                  ? `<span title="The time this source gave for the event.">Reported ${escapeHtml(formatTime(observation.occurredAt))}
                       · ${escapeHtml(formatRelative(observation.occurredAt, observation.isDemo))}</span>`
                  : '<span class="muted">This source stated no time of its own.</span>'}
                <span title="When this system received the report.">Received ${escapeHtml(formatTime(observation.receivedAt))}</span>
              </div>
              ${observation.status === 'Duplicate'
                ? ''
                : `<div class="evidence-conf">
                     ${confidenceChip(observation.classificationConfidence, observation.classificationMethod)}
                     ${observation.detectedLanguage && observation.detectedLanguage !== 'en'
                       ? `<span class="lang-chip">${escapeHtml(observation.detectedLanguage)} → en</span>`
                       : ''}
                   </div>`}
              ${observation.severityRationale
                ? `<div class="rationale">${escapeHtml(observation.severityRationale)}</div>`
                : ''}
              ${observation.status === 'Duplicate'
                ? ''
                : modelOpinion(observation.modelSeverity, observation.severity)}
              ${Array.isArray(observation.entities) && observation.entities.length > 0
                ? `<div class="entities" title="Named actors reported by the enrichment stage. Claims about the text, not verified facts.">
                     ${observation.entities.slice(0, 8).map((entity) =>
                       `<span class="entity-chip">${escapeHtml(entity.name)}<em>${escapeHtml(entity.type)}</em></span>`).join('')}
                   </div>`
                : ''}
            </li>`).join('')}
        </ul>`;
    } catch (error) {
      console.error(error);
      container.innerHTML = '<p class="muted">Evidence could not be loaded.</p>';
    }
  }

  // ---------------------------------------------------------------- recorded replay

  /**
   * Plays the exported run back in the order it happened, revealing incidents as the observations
   * that created them arrive. This is explicitly a recording being replayed on demand, not a
   * simulation of live activity, which is why it only ever runs when the visitor asks for it.
   */
  function startReplay(recorded) {
    stopReplay();

    const ordered = [...recorded].sort((left, right) => new Date(left.receivedAt) - new Date(right.receivedAt));
    if (ordered.length === 0) return;

    replay.active = true;
    replay.revealed.clear();
    replay.counts.clear();
    replay.played.clear();
    feed = [];
    selectedIncidentId = null;

    dom.details.innerHTML = '<p class="empty-state">Replaying the recorded run…</p>';
    dom.replayButton.textContent = 'Stop replay';
    renderIncidents();
    renderFeed();

    let index = 0;

    const step = () => {
      if (index >= ordered.length) {
        finishReplay(recorded);
        return;
      }

      const observation = ordered[index++];
      feed.unshift(observation);
      if (feed.length > MAX_FEED_ITEMS) feed.length = MAX_FEED_ITEMS;
      replay.played.add(observation.id);

      // A duplicate is recorded as evidence but adds nothing to an incident, so the count it would
      // have contributed deliberately does not move.
      if (observation.incidentId && observation.status !== 'Duplicate') {
        replay.revealed.add(observation.incidentId);
        replay.counts.set(observation.incidentId, (replay.counts.get(observation.incidentId) ?? 0) + 1);
      }

      setStatus('replaying', `Replaying recorded run — ${index} of ${ordered.length} observations`);
      renderFeed();
      renderIncidents();

      replay.timer = window.setTimeout(step, REPLAY_STEP_MS);
    };

    step();
  }

  function finishReplay(recorded) {
    // Must cancel the pending step, not just forget its handle. Dropping the handle would leave the
    // timer running, so stopping a replay part-way would keep appending observations behind the
    // visitor's back and overwrite the status line.
    stopReplay();
    replay.revealed.clear();
    replay.counts.clear();
    replay.played.clear();
    feed = recorded.slice(0, MAX_FEED_ITEMS);

    dom.replayButton.textContent = 'Replay the recorded run';
    setStatus('snapshot', snapshotStatusText());
    renderFeed();
    renderIncidents();

    // The drawer was rendered with the run's partial counts and evidence, so it has to be rebuilt
    // against the final state or it will sit beside the list contradicting it.
    if (selectedIncidentId) selectIncident(selectedIncidentId);
  }

  function stopReplay() {
    if (replay.timer !== null) {
      window.clearTimeout(replay.timer);
      replay.timer = null;
    }
    replay.active = false;
  }

  /**
   * Says what this page is built from, counting rather than asserting.
   *
   * The banner used to have two states, demo or not, which was adequate while the only data source
   * was a recorded stream. With live feeds a snapshot can be either or both, and a single blanket
   * label would misdescribe half the page whichever label it picked. Live reporting must not be
   * dismissed as a demo, and recorded demo records must never be passed off as reporting.
   */
  function describeProvenance(observations) {
    const summary = provenanceSummary(observations);

    if (summary.reveal) {
      dom.demoNotice.hidden = false;
      dom.demoNotice.querySelector('strong').textContent = summary.label;
    }

    dom.demoNoticeText.textContent = summary.text;
  }

  function snapshotStatusText() {
    const generatedAt = staticSource.meta?.generatedAt;
    return `Static snapshot — pipeline run of ${formatDate(generatedAt)}`;
  }

  // ---------------------------------------------------------------- live updates

  function upsertIncident(incident) {
    incidents.set(incident.id, incident);
    renderIncidents();
    if (incident.id === selectedIncidentId) selectIncident(incident.id);
  }

  function pushObservation(observation) {
    feed.unshift(observation);
    if (feed.length > MAX_FEED_ITEMS) feed.length = MAX_FEED_ITEMS;
    renderFeed();
  }

  /**
   * Renders activity around each watched passage.
   * <p>
   * Everything shown here is a count of what was recorded inside a stated radius. There is no score
   * and no colour scale, because a passage with three nearby reports is not thereby "elevated" — the
   * moment this panel ranks by anything other than what it measured, it starts asserting an
   * assessment the system has not made.
   */
  /**
   * Draws the per-theatre coverage statement.
   *
   * Every decision about wording lives in lib/coverage.js and is tested there; this builds elements
   * and nothing else. The caveat is rendered for every theatre including the well-covered ones,
   * because a caveat that only appears on the bad news reads as an excuse rather than as a
   * description of the data.
   */
  function renderCoverage(report) {
    dom.coverageList.replaceChildren();
    dom.coverageGaps.replaceChildren();

    if (!hasCoverage(report)) {
      const empty = document.createElement('p');
      empty.className = 'feed-hint';
      empty.textContent = 'No coverage report is available from this data source.';
      dom.coverageList.append(empty);
      return;
    }

    report.theatres.forEach((theatre) => {
      const card = document.createElement('article');
      card.className = asCount(theatre.placedCount) > 0 ? 'coverage is-active' : 'coverage';

      const heading = document.createElement('h3');
      heading.textContent = theatre.theatre;
      card.append(heading);

      const summary = document.createElement('p');
      summary.className = 'coverage-summary';
      summary.textContent = coverageSummary(theatre);
      card.append(summary);

      const precisions = orderByPrecision(theatre.byPrecision);

      if (precisions.length > 0) {
        const meta = document.createElement('div');
        meta.className = 'coverage-meta';

        precisions.forEach((entry) => {
          const chip = document.createElement('span');
          chip.className = `coverage-chip coverage-${String(entry.category).toLowerCase()}`;
          chip.textContent = `${asCount(entry.count)} ${precisionLabel(entry.category)}`;
          meta.append(chip);
        });

        card.append(meta);
      }

      const lexicon = document.createElement('p');
      lexicon.className = 'coverage-lexicon';
      lexicon.textContent = lexiconNote(theatre);
      card.append(lexicon);

      const caveat = document.createElement('p');
      caveat.className = 'coverage-caveat';
      caveat.textContent = theatre.caveat ?? '';
      card.append(caveat);

      dom.coverageList.append(card);
    });

    gapNotes(report).forEach((note) => {
      const paragraph = document.createElement('p');
      paragraph.className = 'feed-hint';
      paragraph.textContent = note;
      dom.coverageGaps.append(paragraph);
    });
  }

  function renderChokepoints(analysis) {
    const entries = analysis?.chokepoints ?? [];
    dom.chokepointList.replaceChildren();
    dom.chokepointCount.textContent = entries.filter((entry) => entry.incidentCount > 0).length;
    dom.chokepointMethod.textContent = analysis?.method
      ? `Distances: ${analysis.method}.`
      : '';

    if (entries.length === 0) {
      const empty = document.createElement('p');
      empty.className = 'feed-hint';
      empty.textContent = 'No chokepoint analysis is available from this data source.';
      dom.chokepointList.append(empty);
      return;
    }

    entries.forEach((entry) => {
      const card = document.createElement('article');
      card.className = entry.incidentCount > 0 ? 'chokepoint is-active' : 'chokepoint';

      const heading = document.createElement('h3');
      heading.textContent = entry.name;
      card.append(heading);

      const meta = document.createElement('div');
      meta.className = 'chokepoint-meta';

      const count = document.createElement('span');
      count.className = entry.incidentCount > 0 ? 'chokepoint-count' : 'chokepoint-count chokepoint-quiet';
      count.textContent = entry.incidentCount === 1
        ? '1 incident recorded'
        : `${entry.incidentCount} incidents recorded`;
      meta.append(count);

      const radius = document.createElement('span');
      radius.textContent = `within ${entry.watchRadiusKilometres} km`;
      meta.append(radius);

      if (entry.nearestIncidentKilometres !== null && entry.nearestIncidentKilometres !== undefined) {
        const nearest = document.createElement('span');
        nearest.textContent = `nearest ${entry.nearestIncidentKilometres} km`;
        meta.append(nearest);
      }

      (entry.severityCounts ?? []).forEach((severity) => {
        const chip = document.createElement('span');
        chip.className = `sev sev-${severity.severity}`;
        chip.textContent = `${severity.count} ${severity.severity}`;
        meta.append(chip);
      });

      card.append(meta);

      const description = document.createElement('p');
      description.textContent = entry.description;
      card.append(description);

      if ((entry.incidents ?? []).length > 0) {
        const list = document.createElement('ul');
        list.className = 'chokepoint-incidents';

        entry.incidents.forEach((nearby) => {
          const item = document.createElement('li');
          const link = document.createElement('button');
          link.type = 'button';
          link.textContent = nearby.incident.title;
          link.addEventListener('click', () => selectIncident(nearby.incident.id, true));
          item.append(link);

          const distance = document.createElement('span');
          distance.className = 'chokepoint-distance';
          distance.textContent = `${nearby.distanceKilometres} km`;
          item.append(distance);
          list.append(item);
        });

        card.append(list);
      }

      dom.chokepointList.append(card);
    });
  }


  // ---------------------------------------------------------------- analytics

  const SCORE_BAND_COLOUR = {
    quiet: '#64dfdf',
    moderate: '#ffd166',
    elevated: '#ff9f1c',
    high: '#ef476f',
  };

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  function statTile(label, value, hint) {
    const tile = element('div', 'stat');
    tile.append(element('span', 'stat-value', value));
    tile.append(element('span', 'stat-label', label));
    if (hint) tile.append(element('span', 'stat-hint', hint));
    return tile;
  }

  /**
   * A labelled horizontal bar list.
   *
   * Every row is drawn against the largest count rather than against the total, so a breakdown
   * whose classes are wildly uneven still shows the small ones as visible bars instead of as
   * invisible slivers.
   */
  function barList(rows, colourFor) {
    const list = element('div', 'bars');
    const largest = rows.reduce((max, row) => Math.max(max, row.count), 0);

    rows.forEach((row) => {
      const item = element('div', 'bar-row');
      item.append(element('span', 'bar-label', row.label));

      const track = element('span', 'bar-track');
      const fill = element('span', 'bar-fill');
      fill.style.width = `${largest === 0 ? 0 : Math.max(2, (row.count / largest) * 100)}%`;
      if (colourFor) fill.style.background = colourFor(row);
      track.append(fill);
      item.append(track);

      item.append(element('span', 'bar-count', row.count));
      list.append(item);
    });

    return list;
  }

  /**
   * The incidents-over-time chart, as inline SVG.
   *
   * Hand-drawn rather than pulled from a charting library, because the page must work with no
   * external script host reachable — the same constraint that drives the basemap fallback.
   * Elevated incidents are stacked inside each bar rather than drawn as a second series: the
   * question is what share of a period was serious, and two adjacent bars answer a different one.
   */
  function timeseriesChart(buckets) {
    const NS = 'http://www.w3.org/2000/svg';
    const width = 300;
    const height = 68;
    const gap = buckets.length > 40 ? 0.5 : 1.5;
    const barWidth = Math.max(1, (width - (gap * (buckets.length - 1))) / buckets.length);
    const peak = buckets.reduce((max, bucket) => Math.max(max, bucket.count), 0);
    const total = buckets.reduce((sum, bucket) => sum + bucket.count, 0);

    const svg = document.createElementNS(NS, 'svg');
    svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
    svg.setAttribute('class', 'timeseries');
    svg.setAttribute('role', 'img');
    svg.setAttribute('preserveAspectRatio', 'none');
    svg.setAttribute(
      'aria-label',
      `${total} incident${total === 1 ? '' : 's'} across ${buckets.length} time buckets, peak ${peak}.`,
    );

    // Drawn first, so bars sit on it. Without a baseline the bars float and a quiet stretch reads
    // as missing data rather than as a period in which nothing was recorded.
    const axis = document.createElementNS(NS, 'rect');
    axis.setAttribute('x', '0');
    axis.setAttribute('y', (height - 1).toFixed(2));
    axis.setAttribute('width', String(width));
    axis.setAttribute('height', '1');
    axis.setAttribute('class', 'ts-axis');
    svg.append(axis);

    buckets.forEach((bucket, index) => {
      const x = index * (barWidth + gap);

      // An empty bucket still gets a tick, so its position on the axis is visible.
      const full = peak === 0 ? 0 : (bucket.count / peak) * (height - 4);
      const drawn = bucket.count === 0 ? 1 : Math.max(2, full);
      const elevatedHeight = bucket.count === 0 ? 0 : drawn * (bucket.elevated / bucket.count);

      const base = document.createElementNS(NS, 'rect');
      base.setAttribute('x', x.toFixed(2));
      base.setAttribute('y', (height - drawn).toFixed(2));
      base.setAttribute('width', barWidth.toFixed(2));
      base.setAttribute('height', drawn.toFixed(2));
      base.setAttribute('class', bucket.count === 0 ? 'ts-empty' : 'ts-bar');
      base.append(titleFor(bucket));
      svg.append(base);

      if (elevatedHeight > 0) {
        const elevated = document.createElementNS(NS, 'rect');
        elevated.setAttribute('x', x.toFixed(2));
        elevated.setAttribute('y', (height - elevatedHeight).toFixed(2));
        elevated.setAttribute('width', barWidth.toFixed(2));
        elevated.setAttribute('height', elevatedHeight.toFixed(2));
        elevated.setAttribute('class', 'ts-elevated');
        svg.append(elevated);
      }
    });

    return svg;

    function titleFor(bucket) {
      const node = document.createElementNS(NS, 'title');

      // Against the run, not against now: a bucket boundary is part of a window the backend computed
      // when the report was generated, so it means nothing measured from the visitor's clock.
      // formatRelative supplies the suffix itself — appending another one here is what used to make
      // these tooltips read "2d ago ago".
      const when = formatRelative(bucket.start, true) || formatTime(bucket.start);
      node.textContent = `${bucket.count} incident${bucket.count === 1 ? '' : 's'}`
        + `${bucket.elevated > 0 ? `, ${bucket.elevated} high or critical` : ''} — ${when}`;
      return node;
    }
  }

  /** The score card: the number, what it is made of, and what it is not. */
  function scoreCard(score) {
    const card = element('section', 'score-card');
    const colour = SCORE_BAND_COLOUR[score.band] ?? 'var(--accent)';

    const head = element('div', 'score-head');
    const value = element('span', 'score-value', score.value.toFixed(1));
    value.style.color = colour;
    head.append(value);

    const heading = element('div', 'score-heading');
    heading.append(element('h3', null, 'Geopolitical Activity Score'));

    const band = element('span', 'score-band', score.band);
    band.style.color = colour;
    heading.append(band);
    heading.append(element(
      'span',
      'score-rate',
      `${score.weightedPerDay} weighted incidents/day over `
        + `${score.incidentsScored} incident${score.incidentsScored === 1 ? '' : 's'}`,
    ));
    head.append(heading);
    card.append(head);

    const notice = element('p', 'score-notice');
    notice.append(element('strong', null, 'Heuristic. '));
    notice.append(document.createTextNode(score.notice));
    card.append(notice);

    if (score.truncated) {
      card.append(element(
        'p',
        'score-warning',
        'Computed over a capped sample of this window, so it is a partial figure.',
      ));
    }

    const details = element('details', 'score-detail');
    details.append(element('summary', null, 'How this number is built'));

    // Listed rather than charted, deliberately. Four of these are multipliers and the fifth is a
    // rate in incidents per day, so drawing them on one axis would invite a comparison between
    // quantities that share no unit — the bar for "confidence 0.61" would simply look smaller than
    // the bar for "7.71 per day", which means nothing.
    const breakdown = element('dl', 'score-components');

    score.components.forEach((component) => {
      const term = element('dt');
      term.append(element('span', 'score-component-label', component.label));
      term.append(element('span', 'score-component-value', component.value));
      breakdown.append(term);
      breakdown.append(element('dd', null, component.description));
    });

    details.append(breakdown);
    details.append(element('pre', 'score-formula', score.formula));
    card.append(details);

    return card;
  }

  function analyticsSection(title, hint) {
    const node = element('section', 'analytics-section');
    node.append(element('h3', null, title));
    if (hint) node.append(element('p', 'analytics-hint', hint));
    return node;
  }

  function renderAnalytics(report) {
    dom.analyticsBody.replaceChildren();

    dom.analyticsPeriod.textContent = report.incidentCount === 0
      ? 'Nothing recorded in this window'
      : `${formatTime(report.from)} — ${formatTime(report.to)}`;

    dom.analyticsBody.append(scoreCard(report.score));

    const tiles = element('div', 'stat-grid');
    tiles.append(statTile('Incidents', report.incidentCount));
    tiles.append(statTile('Correlated', report.correlatedIncidentCount, 'drew on more than one report'));
    tiles.append(statTile('Observations', report.observationCount, 'including duplicates'));
    tiles.append(statTile('Satellite detections', report.satelliteObservationCount));
    dom.analyticsBody.append(tiles);

    if (report.timeseries?.length) {
      const chart = analyticsSection(
        'Incidents over time',
        'Oldest at the left. The brighter portion of each bar is the High and Critical share.',
      );
      chart.append(timeseriesChart(report.timeseries));
      chart.append(element(
        'p',
        'analytics-hint',
        `${report.timeseries.length} buckets across ${report.windowLabel.toLowerCase()}.`,
      ));
      dom.analyticsBody.append(chart);
    }

    if (report.bySeverity?.length) {
      const node = analyticsSection('By severity');
      node.append(barList(
        report.bySeverity.map((row) => ({ label: humanise(row.category), count: row.count, severity: row.category })),
        (row) => colourFor(row.severity),
      ));
      dom.analyticsBody.append(node);
    }

    if (report.byEventType?.length) {
      const node = analyticsSection('By event type');
      node.append(barList(report.byEventType.map((row) => ({ label: humanise(row.category), count: row.count }))));
      dom.analyticsBody.append(node);
    }

    if (report.byRegion?.length) {
      const node = analyticsSection(
        'By region',
        'Grouped by the country the gazetteer resolved, or by place name where a report fell outside any country.',
      );
      node.append(barList(report.byRegion.map((row) => ({
        label: row.countryCode ? `${row.name} (${row.countryCode})` : row.name,
        count: row.count,
      }))));
      dom.analyticsBody.append(node);
    }

    if (report.bySourceKind?.length) {
      const node = analyticsSection('Where the evidence came from');
      node.append(barList(report.bySourceKind.map((row) => ({ label: humanise(row.category), count: row.count }))));
      dom.analyticsBody.append(node);
    }

    if (report.byObservationStatus?.length) {
      const node = analyticsSection(
        'What the pipeline did with it',
        'Terminal state of each observation received in this window. Duplicates and failures are kept, not discarded.',
      );
      node.append(barList(report.byObservationStatus.map((row) => ({ label: humanise(row.category), count: row.count }))));
      dom.analyticsBody.append(node);
    }

    const maritime = report.maritime;

    if (maritime) {
      const node = analyticsSection(
        'Maritime activity',
        'Counts proximities, not distinct incidents: watch radii overlap, so a report between two '
          + 'passages is counted by both. Proximity is not an assessment of threat.',
      );

      node.append(element(
        'p',
        'analytics-summary',
        maritime.chokepointsWithActivity === 0
          ? `Nothing recorded within the watch radius of any of the ${maritime.chokepointsWatched} passages.`
          : `${maritime.chokepointsWithActivity} of ${maritime.chokepointsWatched} watched passages had `
            + `activity, busiest ${maritime.busiest.name} with ${maritime.busiest.incidentCount}.`,
      ));

      const active = maritime.chokepoints.filter((row) => row.incidentCount > 0);

      if (active.length > 0) {
        node.append(barList(active.map((row) => ({ label: row.name, count: row.incidentCount }))));
      }

      dom.analyticsBody.append(node);
    }
  }

  /**
   * Fetches one window, reusing an already-fetched report.
   *
   * A failure renders as a message in the panel rather than propagating: analytics are a secondary
   * view, and a snapshot published before this feature existed simply has no files to serve.
   */
  async function showAnalytics(token) {
    if (!dataSource?.loadAnalytics) {
      dom.analyticsBody.replaceChildren(element('p', 'empty-state', 'This data source does not provide analytics.'));
      return;
    }

    if (analyticsCache.has(token)) {
      renderAnalytics(analyticsCache.get(token));
      return;
    }

    dom.analyticsBody.replaceChildren(element('p', 'empty-state', 'Loading analytics…'));

    try {
      const report = await dataSource.loadAnalytics(token);
      analyticsCache.set(token, report);
      renderAnalytics(report);
    } catch (error) {
      console.error(error);
      dom.analyticsBody.replaceChildren(element(
        'p',
        'empty-state',
        'Analytics could not be loaded from this data source.',
      ));
    }
  }

  function wireAnalytics() {
    ANALYTICS_WINDOWS.forEach((choice) => {
      const option = document.createElement('option');
      option.value = choice.token;
      option.textContent = choice.label;
      dom.analyticsWindow.append(option);
    });

    dom.analyticsWindow.addEventListener('change', () => showAnalytics(dom.analyticsWindow.value));
  }

  async function loadSnapshotState() {
    const [loadedIncidents, loadedObservations, chokepoints, coverage] = await Promise.all([
      dataSource.loadIncidents(),
      dataSource.loadObservations(),

      // Best effort: a data source without chokepoint analysis leaves the panel empty rather than
      // failing the whole load, because the map and the feed do not depend on it.
      dataSource.loadChokepoints?.().catch(() => ({ method: '', chokepoints: [] }))
        ?? { method: '', chokepoints: [] },

      // Same treatment, and for the same reason.
      dataSource.loadCoverage?.().catch(() => null) ?? null,
    ]);

    incidents.clear();
    loadedIncidents.forEach((incident) => incidents.set(incident.id, incident));
    feed = loadedObservations;

    // Fail safe, and deliberately not a plain "is any record flagged?" test — see canHideDemoNotice,
    // which states why an empty database must not be mistaken for a live deployment.
    dom.demoNotice.hidden = canHideDemoNotice(
      loadedIncidents, loadedObservations, dataSource.mode, staticSource.meta,
    );
    describeProvenance(loadedObservations);

    renderIncidents();
    renderFeed();
    renderChokepoints(chokepoints);
    renderCoverage(coverage);
  }

  function startPolling(reason) {
    if (pollTimer) return;
    setStatus('polling', `${reason} — polling every ${POLL_INTERVAL_MS / 1000}s`);
    pollTimer = window.setInterval(
      () => loadSnapshotState().catch((error) => console.error(error)),
      POLL_INTERVAL_MS,
    );
  }

  function stopPolling() {
    if (!pollTimer) return;
    window.clearInterval(pollTimer);
    pollTimer = null;
  }

  /** Loads a script on demand and resolves once it is usable. */
  function loadScript(source) {
    return new Promise((resolve, reject) => {
      const element = document.createElement('script');
      element.src = source;
      element.async = true;
      element.addEventListener('load', () => resolve());
      element.addEventListener('error', () => reject(new Error(`Could not load ${source}`)));
      document.head.append(element);
    });
  }

  async function connectRealtime() {
    // Fetched here rather than in the document head so the static build, which never opens a hub
    // connection, does not download a realtime client it cannot use.
    if (!window.signalR) {
      try {
        await loadScript(SIGNALR_CLIENT_URL);
      } catch (error) {
        console.error(error);
      }
    }

    if (!window.signalR) {
      startPolling('Realtime client unavailable');
      return;
    }

    const connection = new signalR.HubConnectionBuilder()
      .withUrl('./hubs/incidents')
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    connection.on('incidentCreated', upsertIncident);
    connection.on('incidentUpdated', upsertIncident);
    connection.on('observationReceived', pushObservation);

    connection.onreconnecting(() => {
      setStatus('connecting', 'Reconnecting to the live feed…');
      startPolling('Reconnecting');
    });

    connection.onreconnected(async () => {
      stopPolling();
      // Reconnection can span a gap in messages, so re-read committed state rather than assume.
      await loadSnapshotState().catch((error) => console.error(error));
      setStatus('live', 'Live — receiving realtime updates');
    });

    connection.onclose(() => startPolling('Realtime connection closed'));

    try {
      await connection.start();
      stopPolling();
      setStatus('live', 'Live — receiving realtime updates');
    } catch (error) {
      console.error('SignalR connection failed', error);
      startPolling('Realtime unavailable');
    }
  }

  // ---------------------------------------------------------------- wiring

  function wireControls() {
    [dom.severityFilter, dom.typeFilter, dom.locatedOnly]
      .forEach((control) => control.addEventListener('change', renderIncidents));

    dom.submitForm.addEventListener('submit', submitObservation);

    dom.basemapFilter.value = preferredBasemap();
    dom.basemapFilter.addEventListener('change', () => {
      applyBasemap(dom.basemapFilter.value).catch((error) => console.error('Basemap switch failed', error));
    });

    wireAnalytics();

    document.querySelectorAll('.tab').forEach((tab) => {
      tab.addEventListener('click', () => {
        document.querySelectorAll('.tab').forEach((other) => {
          const active = other === tab;
          other.classList.toggle('is-active', active);
          other.setAttribute('aria-selected', String(active));
        });
        document.querySelectorAll('.tab-panel').forEach((panel) => {
          panel.hidden = panel.id !== `${tab.dataset.tab}Tab`;
        });

        // Fetched on first view rather than at start-up. A visitor who never opens the tab should
        // not pay for the request, and the panel is not on screen to show a result until they do.
        if (tab.dataset.tab === 'analytics' && !analyticsLoaded) {
          analyticsLoaded = true;
          showAnalytics(dom.analyticsWindow.value);
        }
      });
    });
  }

  // ---------------------------------------------------------------- manual submission

  /**
   * Queues a report through the same endpoint an ingestion adapter would use.
   *
   * Two things this deliberately does not do. It does not claim an incident was created: the API
   * answers 202 because the observation has been queued and may still turn out to be a duplicate or
   * fail enrichment, and the message says exactly that. And it does not accept coordinates — only a
   * place name — so the form cannot become a second route around the deterministic resolver.
   */
  async function submitObservation(event) {
    event.preventDefault();

    const content = dom.submitContent.value.trim();

    if (content.length === 0) {
      setSubmitStatus('error', 'A report is required.');
      dom.submitContent.focus();
      return;
    }

    dom.submitButton.disabled = true;
    setSubmitStatus('pending', 'Queueing…');

    try {
      const response = await fetch('./api/observations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          sourceName: dom.submitSource.value.trim() || null,
          title: dom.submitTitle.value.trim() || null,
          content,
          locationName: dom.submitLocation.value.trim() || null,
        }),
      });

      if (response.status === 202) {
        setSubmitStatus('ok', 'Queued. Watch the Feed tab — it may be enriched, correlated, '
          + 'or recorded as a duplicate.');
        dom.submitForm.reset();

        // The pipeline is asynchronous, so there is nothing to show yet. Move to the feed and let
        // the realtime subscription deliver the outcome rather than pretending to know it.
        document.querySelector('.tab[data-tab="feed"]')?.click();
        return;
      }

      if (response.status === 400 || response.status === 422) {
        const problem = await response.json().catch(() => null);
        const reason = problem?.errors?.submission?.[0] ?? 'The submission was rejected.';
        setSubmitStatus('error', reason);
        return;
      }

      setSubmitStatus('error', `The server responded with ${response.status}.`);
    } catch (error) {
      console.error(error);
      setSubmitStatus('error', 'The submission could not be sent. Is the application still running?');
    } finally {
      dom.submitButton.disabled = false;
    }
  }

  function setSubmitStatus(state, message) {
    dom.submitStatus.dataset.state = state;
    dom.submitStatus.textContent = message;
  }

  /** A snapshot has no backend to submit to, so the form is removed rather than left to fail. */
  function disableSubmission() {
    dom.submitForm.hidden = true;
    dom.submitDisabled.hidden = false;
  }

  /**
   * Points relative times at the recorded run instead of the visitor's clock. Must run before the
   * first render: the initial paint would otherwise stamp synthetic events as minutes old, and
   * nothing re-renders afterwards to correct it.
   */
  function applyStaticTimeBasis() {
    setRunBasis(staticSource.meta?.generatedAt);
  }

  function describeStaticMode() {
    const meta = staticSource.meta ?? {};
    const generated = formatDate(meta.generatedAt);

    // The provenance banner is set from the record counts in describeProvenance and must not be
    // overwritten here. This used to hard-code "synthetic replay data", which was accurate while
    // that was the only thing the exporter could read and became a false claim the moment live
    // feeds were switched on. The run date is appended to whatever provenance was determined.
    const live = asCount(meta.liveObservationCount);
    dom.demoNoticeText.textContent += ` · run of ${generated}`;

    dom.feedHint.textContent = live > 0
      ? 'The observations below were ingested from public feeds by a real pipeline run. Replay them '
        + 'to watch incidents form and correlate.'
      : 'The observations below are the recorded output of a real pipeline run. Replay them to '
        + 'watch incidents form and correlate.';

    dom.aboutModeText.innerHTML = `
      This page is a <strong>static snapshot</strong>. GitHub Pages serves files only, so no .NET
      process, database, or SignalR hub is running here. Everything shown was produced by a genuine
      run of the pipeline on ${escapeHtml(generated)} and exported to JSON at build time:
      <strong>${plural(asCount(meta.observationCount), 'observation')}</strong> in,
      <strong>${plural(asCount(meta.incidentCount), 'incident')}</strong> out, with
      <strong>${asCount(meta.duplicateCount)}</strong> rejected as duplicates,
      <strong>${plural(asCount(meta.correlatedIncidentCount), 'incident')}</strong> formed from
      multiple sources, and <strong>${asCount(meta.unresolvedLocationCount)}</strong> left
      deliberately unplaced because no trustworthy coordinates existed.
      <br><br>
      Times on this page are shown relative to that run, not to now. Nothing here is current.
      <br><br>
      ${escapeHtml(meta.notice ?? 'Synthetic replay data. Not live reporting.')}
      <br><br>
      Every observation passed through the <strong>AI enrichment stage</strong> and was scored by the
      <strong>trained severity model</strong>. With no provider configured — which is how this page is
      built — enrichment runs against a deterministic in-process stand-in, labelled
      <code>ai:Mock/deterministic-stub</code> wherever it appears, and it is
      <strong>not a language model</strong>. That is why so many real headlines land in
      <em>Other</em>: a keyword classifier is weak on real reporting, and the page shows that rather
      than hiding it. The severity model is a real trained classifier and its prediction is recorded
      beside each observation, never used to set the severity shown.
      <br><br>
      Running the application locally starts the live version, with the queue, background workers,
      and realtime updates all active.`;
  }

  function describeLiveMode() {
    dom.demoNoticeText.textContent = 'Synthetic replay data — not live reporting';
    dom.aboutModeText.textContent = 'This instance is running the live backend: observations are '
      + 'ingested into a bounded queue, processed by background workers, persisted, and pushed to '
      + 'this page over SignalR. The ingested data is still the synthetic replay stream, so nothing '
      + 'shown describes real-world events. No AI or ML model is involved: categories and severities '
      + 'come from a deterministic keyword classifier.';
  }

  async function start() {
    // Awaited so a basemap failure is handled before anything renders over the globe.
    await initialiseGlobe();
    wireControls();

    dataSource = await resolveDataSource();

    if (!dataSource) {
      setStatus('error', 'No data source could be reached');
      dom.details.innerHTML = '<p class="empty-state">Neither the API nor a published snapshot could '
        + 'be loaded. If you are running locally, check the application logs.</p>';
      return;
    }

    if (dataSource.mode === 'static') {
      applyStaticTimeBasis();
    }

    try {
      await loadSnapshotState();
    } catch (error) {
      console.error(error);
      setStatus('error', 'Data could not be loaded');
      dom.details.innerHTML = '<p class="empty-state">The dashboard data could not be loaded.</p>';
      return;
    }

    if (dataSource.mode === 'static') {
      describeStaticMode();
      disableSubmission();
      setStatus('snapshot', snapshotStatusText());

      const recorded = [...feed];
      dom.replayButton.hidden = false;
      dom.replayButton.addEventListener('click', () => {
        if (replay.active) {
          finishReplay(recorded);
        } else {
          startReplay(recorded);
        }
      });
      return;
    }

    describeLiveMode();
    await connectRealtime();
  }

  start();
})();
