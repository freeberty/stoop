import { Map as MapLibreMap, Marker, NavigationControl, setWorkerUrl, type GeoJSONSource, type MapMouseEvent } from 'maplibre-gl';
import workerUrl from 'maplibre-gl/dist/maplibre-gl-worker.mjs?url';
import 'maplibre-gl/dist/maplibre-gl.css';
import type { Category, Feature, FeatureCollection } from './api';

export const CATEGORY_COLORS: Record<Category, string> = {
  DigitalNomads: '#2563eb', Alumni: '#7c3aed', Conference: '#db2777', Photography: '#0891b2', Gaming: '#16a34a',
  Technology: '#4f46e5', Arts: '#ea580c', Music: '#c026d3', Sports: '#dc2626', Outdoors: '#65a30d',
  Food: '#d97706', Travel: '#0d9488', History: '#92400e', Social: '#e11d48', Other: '#64748b',
};

export interface Viewport { west: number; south: number; east: number; north: number; zoom: number }
export interface LngLat { lng: number; lat: number }

/**
 * Everything the app needs from a map. Mapbox GL JS (or the Flutter map) can replace MapLibre by
 * implementing this interface; nothing else in the app talks to the map library.
 */
export interface MapAdapter {
  onViewportChange(cb: (v: Viewport) => void): void;
  onSelect(cb: (feature: Feature) => void): void;
  /** A click on empty map (not on a community). */
  onBackgroundClick(cb: (at: LngLat) => void): void;
  /** While set, every map click picks a location instead of selecting communities. */
  setPickMode(cb: ((at: LngLat) => void) | null): void;
  /** Draggable pin for a community being created. */
  showDraftPin(at: LngLat, onMove: (at: LngLat) => void): void;
  clearDraftPin(): void;
  setData(fc: FeatureCollection): void;
  setSelected(id: string | null): void;
  /** Emphasise a community while its list card is hovered. */
  setHovered(id: string | null): void;
  flyTo(lng: number, lat: number, zoom?: number): void;
  center(): { lng: number; lat: number };
  getViewport(): Viewport;
  locateUser(): Promise<void>;
}

// MapLibre 6 ships its worker as a separate module; point it at the bundler-emitted asset.
setWorkerUrl(workerUrl);

const SOURCE = 'communities';
const EMPTY: FeatureCollection = { type: 'FeatureCollection', features: [], count: 0, truncated: false };

export class MapLibreAdapter implements MapAdapter {
  private map: MapLibreMap;
  private ready: Promise<void>;
  private pendingData: FeatureCollection = EMPTY;
  private selectCb: (f: Feature) => void = () => {};
  private bgCb: (at: LngLat) => void = () => {};
  private pickCb: ((at: LngLat) => void) | null = null;
  private draftPin: Marker | null = null;
  private draftMove: (at: LngLat) => void = () => {};
  private viewCb: (v: Viewport) => void = () => {};

  constructor(container: HTMLElement, styleUrl: string, fontStack: string) {
    this.map = new MapLibreMap({
      container, style: styleUrl, center: [-9.14, 38.72], zoom: 10.5, pitch: 0, maxPitch: 70,
      attributionControl: { compact: true }, dragRotate: true, touchPitch: true,
    });
    this.map.addControl(new NavigationControl({ visualizePitch: true }), 'bottom-right');

    this.ready = new Promise((resolve) => this.map.on('load', () => { this.addLayers(fontStack); resolve(); }));
    this.map.on('moveend', () => this.viewCb(this.getViewport()));
  }

  private addLayers(font: string) {
    const m = this.map;
    m.addSource(SOURCE, { type: 'geojson', data: this.pendingData as never, cluster: true, clusterMaxZoom: 14, clusterRadius: 55, promoteId: 'id' });

    m.addLayer({
      id: 'clusters', type: 'circle', source: SOURCE, filter: ['has', 'point_count'],
      paint: {
        'circle-color': ['step', ['get', 'point_count'], '#38bdf8', 10, '#3b82f6', 40, '#6366f1', 150, '#7c3aed'],
        'circle-radius': ['step', ['get', 'point_count'], 16, 10, 20, 40, 26, 150, 32],
        'circle-stroke-width': 3, 'circle-stroke-color': 'rgba(255,255,255,0.85)', 'circle-opacity': 0.92,
      },
    });
    m.addLayer({
      id: 'cluster-count', type: 'symbol', source: SOURCE, filter: ['has', 'point_count'],
      layout: { 'text-field': ['get', 'point_count_abbreviated'], 'text-font': [font], 'text-size': 13, 'text-allow-overlap': true },
      paint: { 'text-color': '#ffffff' },
    });

    const colorMatch: unknown[] = ['match', ['get', 'category']];
    for (const [cat, color] of Object.entries(CATEGORY_COLORS)) colorMatch.push(cat, color);
    colorMatch.push('#64748b');
    m.addLayer({
      id: 'community-points', type: 'circle', source: SOURCE, filter: ['!', ['has', 'point_count']],
      paint: {
        'circle-color': colorMatch as never,
        'circle-radius': ['interpolate', ['linear'], ['zoom'], 10, 6, 16, 11],
        'circle-stroke-width': 2.5, 'circle-stroke-color': '#ffffff',
      },
    });
    m.addLayer({
      id: 'community-hover', type: 'circle', source: SOURCE, filter: ['==', ['get', 'id'], ''],
      paint: {
        'circle-color': colorMatch as never,
        'circle-radius': ['interpolate', ['linear'], ['zoom'], 10, 10, 16, 16],
        'circle-stroke-width': 3, 'circle-stroke-color': '#ffffff',
      },
    });
    m.addLayer({
      id: 'community-selected', type: 'circle', source: SOURCE, filter: ['==', ['get', 'id'], ''],
      paint: { 'circle-color': 'rgba(0,0,0,0)', 'circle-radius': ['interpolate', ['linear'], ['zoom'], 10, 13, 16, 20], 'circle-stroke-width': 4, 'circle-stroke-color': '#0f172a' },
    });

    m.on('click', 'clusters', async (e: MapMouseEvent) => {
      if (this.pickCb) return;
      const f = m.queryRenderedFeatures(e.point, { layers: ['clusters'] })[0];
      if (!f) return;
      const zoom = await (m.getSource(SOURCE) as GeoJSONSource).getClusterExpansionZoom(f.properties!.cluster_id as number);
      m.easeTo({ center: (f.geometry as unknown as { coordinates: [number, number] }).coordinates, zoom: zoom + 0.3, duration: 600 });
    });
    m.on('click', 'community-points', (e: MapMouseEvent) => {
      if (this.pickCb) return;
      const f = m.queryRenderedFeatures(e.point, { layers: ['community-points'] })[0];
      if (!f) return;
      e.originalEvent.stopPropagation();
      const feature: Feature = {
        type: 'Feature', id: String(f.properties!.id),
        geometry: f.geometry as Feature['geometry'], properties: f.properties as unknown as Feature['properties'],
      };
      this.selectCb(feature);
    });
    m.on('click', (e: MapMouseEvent) => {
      if (this.pickCb) { this.pickCb({ lng: e.lngLat.lng, lat: e.lngLat.lat }); return; }
      const hit = m.queryRenderedFeatures(e.point, { layers: ['community-points', 'clusters'] });
      if (hit.length === 0) this.bgCb({ lng: e.lngLat.lng, lat: e.lngLat.lat });
    });
    for (const layer of ['clusters', 'community-points']) {
      m.on('mouseenter', layer, () => { m.getCanvas().style.cursor = 'pointer'; });
      m.on('mouseleave', layer, () => { m.getCanvas().style.cursor = ''; });
    }
    this.viewCb(this.getViewport());
  }

  onViewportChange(cb: (v: Viewport) => void) { this.viewCb = cb; this.ready.then(() => cb(this.getViewport())); }
  onSelect(cb: (f: Feature) => void) { this.selectCb = cb; }
  onBackgroundClick(cb: (at: LngLat) => void) { this.bgCb = cb; }

  setPickMode(cb: ((at: LngLat) => void) | null) {
    this.pickCb = cb;
    this.map.getCanvas().style.cursor = cb ? 'crosshair' : '';
  }

  showDraftPin(at: LngLat, onMove: (at: LngLat) => void) {
    this.draftMove = onMove;
    if (!this.draftPin) {
      const node = document.createElement('div');
      node.className = 'draft-pin';
      node.setAttribute('aria-label', 'New community location');
      const pin = new Marker({ element: node, draggable: true, anchor: 'bottom' });
      pin.on('dragend', () => { const p = pin.getLngLat(); this.draftMove({ lng: p.lng, lat: p.lat }); });
      this.draftPin = pin.setLngLat([at.lng, at.lat]).addTo(this.map);
    } else this.draftPin.setLngLat([at.lng, at.lat]);
  }

  clearDraftPin() { this.draftPin?.remove(); this.draftPin = null; }

  setData(fc: FeatureCollection) {
    this.pendingData = fc;
    this.ready.then(() => (this.map.getSource(SOURCE) as GeoJSONSource).setData(fc as never));
  }

  setHovered(id: string | null) {
    this.ready.then(() => this.map.setFilter('community-hover', ['==', ['get', 'id'], id ?? '']));
  }

  setSelected(id: string | null) {
    this.ready.then(() => this.map.setFilter('community-selected', ['==', ['get', 'id'], id ?? '']));
  }

  flyTo(lng: number, lat: number, zoom = 14) { this.map.flyTo({ center: [lng, lat], zoom, speed: 1.4, essential: true }); }
  center() { const c = this.map.getCenter(); return { lng: c.lng, lat: c.lat }; }

  getViewport(): Viewport {
    const b = this.map.getBounds();
    let west = b.getWest(), east = b.getEast();
    if (east - west >= 360) { west = -180; east = 180; }
    else { const wrap = (x: number) => ((((x + 180) % 360) + 360) % 360) - 180; west = wrap(west); east = wrap(east); }
    const clampLat = (v: number) => Math.max(-90, Math.min(90, v));
    return { west, east, south: clampLat(b.getSouth()), north: clampLat(b.getNorth()), zoom: Math.round(this.map.getZoom()) };
  }

  /** User-initiated only. The position is used to move the camera and is never sent to the server. */
  locateUser(): Promise<void> {
    return new Promise((resolve, reject) => {
      if (!navigator.geolocation) return reject(new Error('Geolocation is not available in this browser.'));
      navigator.geolocation.getCurrentPosition(
        (pos) => { this.flyTo(pos.coords.longitude, pos.coords.latitude, 13); resolve(); },
        (err) => reject(new Error(err.code === err.PERMISSION_DENIED ? 'Location permission was denied.' : 'Could not determine your location.')),
        { enableHighAccuracy: false, timeout: 8000, maximumAge: 60_000 },
      );
    });
  }
}
