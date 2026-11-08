import 'dart:async';
import 'package:flutter/material.dart';
import 'package:mapbox_maps_flutter/mapbox_maps_flutter.dart';
import 'api.dart';
import 'map_adapter.dart';

const mapboxToken = String.fromEnvironment('MAPBOX_TOKEN');
const categories = ['DigitalNomads', 'Alumni', 'Conference', 'Photography', 'Gaming', 'Technology', 'Arts', 'Music', 'Sports', 'Outdoors', 'Food', 'Travel', 'History', 'Social', 'Other'];

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  MapboxOptions.setAccessToken(mapboxToken);
  runApp(const MaterialApp(home: MapScreen(), debugShowCheckedModeBanner: false));
}

class MapScreen extends StatefulWidget {
  const MapScreen({super.key});
  @override State<MapScreen> createState() => _MapScreenState();
}

class _MapScreenState extends State<MapScreen> {
  final api = Api();
  CommunityMap? map;
  List<CommunityFeature> features = [];
  CommunityFeature? selected;
  String? category;
  bool timeOn = false;
  RangeValues years = const RangeValues(1970, 1979);
  Viewport? last;
  Timer? debounce;
  String status = 'Move the map to discover communities';

  Future<void> reload() async {
    final v = last; if (v == null) return;
    try {
      final (json, f) = await api.viewport(v, category: category, fromYear: timeOn ? years.start.round() : null, toYear: timeOn ? years.end.round() : null);
      if (!mounted) return;
      await map?.setData(json);
      setState(() { features = f; status = '${f.length} communities in view'; });
    } catch (e) { if (mounted) setState(() => status = 'Could not load communities'); }
  }

  void onViewport(Viewport v) { last = v; debounce?.cancel(); debounce = Timer(const Duration(milliseconds: 250), reload); }

  @override
  Widget build(BuildContext context) => Scaffold(
    body: Stack(children: [
      MapWidget(
        cameraOptions: CameraOptions(center: Point(coordinates: Position(-9.14, 38.72)), zoom: 10.5),
        onMapCreated: (m) {
          final a = MapboxAdapter(m);
          map = a;
          a.viewportChanges.listen(onViewport);
          a.selections.listen((id) => setState(() => selected = features.where((f) => f.id == id).firstOrNull));
        },
      ),
      SafeArea(child: Column(children: [
        SizedBox(height: 48, child: ListView(scrollDirection: Axis.horizontal, padding: const EdgeInsets.all(6), children: [
          for (final c in [null, ...categories])
            Padding(padding: const EdgeInsets.only(right: 6), child: ChoiceChip(label: Text(c ?? 'All'), selected: category == c, onSelected: (_) { setState(() => category = c); reload(); })),
        ])),
        Card(margin: const EdgeInsets.symmetric(horizontal: 8), child: Column(children: [
          SwitchListTile(dense: true, title: Text(timeOn ? 'About ${years.start.round()} - ${years.end.round()}' : 'Any time'), value: timeOn, onChanged: (v) { setState(() => timeOn = v); reload(); }),
          if (timeOn) RangeSlider(values: years, min: 1900, max: 2040, divisions: 140, labels: RangeLabels('${years.start.round()}', '${years.end.round()}'),
              onChanged: (r) => setState(() => years = r), onChangeEnd: (_) => reload()),
        ])),
      ])),
      Positioned(left: 0, right: 0, bottom: 12, child: SafeArea(child: Column(mainAxisSize: MainAxisSize.min, children: [
        if (selected != null) Card(margin: const EdgeInsets.all(12), child: ListTile(
          title: Text(selected!.name), subtitle: Text('${selected!.locationName ?? 'Anywhere'} - ${selected!.memberCount} members'),
          trailing: FilledButton(onPressed: () async {
            try { await api.join(selected!.id); if (context.mounted) ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Joined'))); }
            catch (e) { if (context.mounted) ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('$e'))); }
          }, child: const Text('Join')))),
        Chip(label: Text(status)),
      ]))),
    ]),
  );
}
