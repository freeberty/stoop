import 'dart:convert';
import 'package:http/http.dart' as http;

const apiUrl = String.fromEnvironment('API_URL', defaultValue: 'http://10.0.2.2:8080');

class Viewport {
  Viewport(this.west, this.south, this.east, this.north, this.zoom);
  final double west, south, east, north;
  final int zoom;
}

class CommunityFeature {
  CommunityFeature(this.id, this.name, this.category, this.locationName, this.memberCount, this.start, this.end, this.accessMode, this.lng, this.lat);
  final String id, name, category, accessMode;
  final String? locationName, start, end;
  final int memberCount;
  final double lng, lat;
}

class Api {
  Api({http.Client? client}) : _http = client ?? http.Client();
  final http.Client _http;
  String? token;

  /// Returns the raw GeoJSON text (fed straight to the map source) and the parsed features.
  Future<(String, List<CommunityFeature>)> viewport(Viewport v, {String? category, int? fromYear, int? toYear, bool includeUndated = false}) async {
    final q = <String, String>{
      'west': v.west.toStringAsFixed(3), 'south': v.south.toStringAsFixed(3),
      'east': v.east.toStringAsFixed(3), 'north': v.north.toStringAsFixed(3), 'zoom': '${v.zoom}',
      if (category != null) 'category': category,
      if (fromYear != null && toYear != null) ...{'from': '$fromYear-01-01', 'to': '$toYear-12-31', if (includeUndated) 'includeUndated': 'true'},
    };
    final res = await _http.get(Uri.parse('$apiUrl/api/v1/discovery/communities').replace(queryParameters: q),
        headers: {if (token != null) 'authorization': 'Bearer $token'});
    if (res.statusCode != 200) throw Exception('Server error ${res.statusCode}');
    final json = jsonDecode(res.body) as Map<String, dynamic>;
    final features = (json['features'] as List).map((f) {
      final p = f['properties'] as Map<String, dynamic>;
      final c = f['geometry']['coordinates'] as List;
      return CommunityFeature(p['id'], p['name'], p['category'], p['locationName'], p['memberCount'], p['contextStartDate'], p['contextEndDate'], p['accessMode'], (c[0] as num).toDouble(), (c[1] as num).toDouble());
    }).toList();
    return (res.body, features);
  }

  Future<void> join(String id) async {
    final res = await _http.post(Uri.parse('$apiUrl/api/v1/communities/$id/join'), headers: {if (token != null) 'authorization': 'Bearer $token'});
    if (res.statusCode >= 300) throw Exception(jsonDecode(res.body)['detail'] ?? 'Could not join');
  }
}
