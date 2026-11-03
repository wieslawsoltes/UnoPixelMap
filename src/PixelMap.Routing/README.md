# Route planning

`RoutePlanner.PlanAsync` visits 2–24 stops in the supplied order and returns each leg's geometry, distance, duration and maneuver list. `PlanChoicesAsync` asks OSRM for up to two alternatives on two-stop road routes and returns the recommended path first. Multi-stop routes preserve their visit order. `RouteTrack.GuidanceAt` returns the current and next maneuver with remaining provider metres. OSRM steps retain before/after bearings, turn modifiers, and roundabout exit numbers. A `RouteUnavailableException` describes an unavailable source, an unsnappable stop or a disconnected network. Callers can inject an `IRouteProvider` to replace a mode. `RouteTrack` samples the path in provider-reported metres and projects a vehicle onto the reachable part of its itinerary.

| Mode | Geometry and time source | Coverage |
| --- | --- | --- |
| Walk, Bike, Drive | FOSSGIS OSRM, using independent foot, bike and car networks | Worldwide where OSM has routable paths. Endpoints must snap within 500 m. |
| Train | Connected rail, tram, subway, light rail, narrow gauge and monorail line geometry from OpenFreeMap | Local alignments with stops within 220 m of tracks. Time is a simulation estimate at 11 m/s. |
| Boat | Mapped ferry lines and explicitly classified fairways where supplied | Local alignments with stops within 350 m of a line. Time is a simulation estimate at 5 m/s. |
| Flight | A shortest great-circle course between each pair of stops | Worldwide. Time is a cruise estimate at 220 m/s. |

Rail and boat previews trace physical infrastructure. Service calendars, passenger transfers, access permissions, track-switch positions, and vessel clearances require an operational data provider. The preview only joins matching mapped vertices, checks connectivity for all stops, and searches a local corridor of up to 48 tiles at zoom 12–14. A river polygon alone provides no evidence that vessels can navigate it. Flight previews have no airline timetable, terrain clearance, or air-traffic procedure data.

## Data and attribution

Road requests use the public FOSSGIS instance with at least one second between request starts per application. Requests contain the ordered coordinates and originate only when planning a trip. Consumers must display attribution and the map correction link, observe the service's fair-use policy, and use another instance for heavy traffic. [FOSSGIS service and policy](https://routing.openstreetmap.de/about.html) · [OSRM HTTP API](https://project-osrm.org/docs/v5.24.0/api/) · [Fix the map](https://www.openstreetmap.org/fixthemap)

The three offline road examples preserve OSRM directions from Wall Street to City Hall Park, retrieved on 2026-11-03. The subway and Pier 11–DUMBO ferry examples retain the geographic alignments from the included Lower Manhattan vector tiles. Offline examples remain selectable with `RouteDemos.ForMode`. Other rail and ferry trips within the seed area can be calculated locally. [OpenFreeMap](https://openfreemap.org/) · [© OpenMapTiles](https://openmaptiles.org/) · [© OpenStreetMap contributors, ODbL](https://www.openstreetmap.org/copyright)
