import 'dart:async';
import 'package:mapbox_maps_flutter/mapbox_maps_flutter.dart';
import 'api.dart';

/// Everything the UI needs from a map renderer.
abstract class CommunityMap {
  Stream<Viewport> get viewportChanges;
  Stream<String> get selections; // community id
  Future<void> setData(String geoJson);
  Future<void> flyTo(double lng, double lat, double zoom);
}

/// Mapbox implementation: one clustered GeoJSON source, circle layers. UNVALIDATED against the current SDK.
class MapboxAdapter implements CommunityMap {
  MapboxAdapter(this._map) {
    _init();
  }
  final MapboxMap _map;
  final _view = StreamController<Viewport>.broadcast();
  final _sel = StreamController<String>.broadcast();
  bool _ready = false;
  String? _pending;

  @override Stream<Viewport> get viewportChanges => _view.stream;
  @override Stream<String> get selections => _sel.stream;

  Future<void> _init() async {
    await _map.style.addSource(GeoJsonSource(id: 'communities', data: '{"type":"FeatureCollection","features":[]}', cluster: true, clusterRadius: 55, clusterMaxZoom: 14));
    await _map.style.addLayer(CircleLayer(id: 'clusters', sourceId: 'communities', filter: ['has', 'point_count'], circleColor: 0xFF4F46E5, circleRadius: 18, circleStrokeWidth: 3, circleStrokeColor: 0xFFFFFFFF));
    await _map.style.addLayer(SymbolLayer(id: 'cluster-count', sourceId: 'communities', filter: ['has', 'point_count'], textField: '{point_count_abbreviated}', textColor: 0xFFFFFFFF, textSize: 13));
    await _map.style.addLayer(CircleLayer(id: 'points', sourceId: 'communities', filter: ['!', ['has', 'point_count']], circleColor: 0xFF2563EB, circleRadius: 8, circleStrokeWidth: 2.5, circleStrokeColor: 0xFFFFFFFF));
    _ready = true;
    if (_pending != null) await setData(_pending!);
    _map.setOnMapTapListener((ctx) async {
      final hits = await _map.queryRenderedFeatures(RenderedQueryGeometry.fromScreenCoordinate(ctx.touchPosition), RenderedQueryOptions(layerIds: ['points']));
      final props = hits.firstOrNull?.queriedFeature.feature['properties'] as Map?;
      if (props != null) _sel.add(props['id'] as String);
    });
    _map.subscribe((e) async => _emitViewport(), [MapEvents.MAP_IDLE]);
  }

  Future<void> _emitViewport() async {
    final cam = await _map.getCameraState();
    final b = await _map.coordinateBoundsForCamera(CameraOptions(center: cam.center, zoom: cam.zoom, bearing: cam.bearing, pitch: cam.pitch));
    _view.add(Viewport(b.southwest.coordinates.lng.toDouble(), b.southwest.coordinates.lat.toDouble(), b.northeast.coordinates.lng.toDouble(), b.northeast.coordinates.lat.toDouble(), cam.zoom.round()));
  }

  @override
  Future<void> setData(String geoJson) async {
    if (!_ready) { _pending = geoJson; return; }
    final src = await _map.style.getSource('communities') as GeoJsonSource;
    await src.updateGeoJSON(geoJson);
  }

  @override
  Future<void> flyTo(double lng, double lat, double zoom) =>
      _map.flyTo(CameraOptions(center: Point(coordinates: Position(lng, lat)), zoom: zoom), MapAnimationOptions(duration: 900));
}
