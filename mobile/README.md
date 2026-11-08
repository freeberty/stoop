# Stoop mobile (Flutter + Mapbox) - Phase 1 spike

**Status: unvalidated.** This was written without a Flutter SDK, pub.dev access or Mapbox tokens, so it has
never been analysed, compiled or run. Treat it as a starting point. Expect to fix API-signature drift in
`mapbox_maps_flutter` (the package changes often).

What it contains:
* `lib/api.dart` - REST client for `/api/v1/discovery/communities` (GeoJSON), community details, join.
* `lib/map_adapter.dart` - the abstraction the UI uses, plus `MapboxAdapter` (GeoJSON source with clustering,
  circle layers, tap handling). Swap it for another renderer without touching screens.
* `lib/main.dart` - map screen: category chips, a year range slider (context dates), preview card.

Run (after `flutter create .` to generate platform folders):
```
flutter pub get
flutter run --dart-define=MAPBOX_TOKEN=pk.xxx --dart-define=API_URL=http://10.0.2.2:8080
```
The working, tested reference client is `../web` (MapLibre GL JS). The brief asks to propose a web
fallback if Flutter Web + Mapbox falls short; that fallback is what `../web` is.
