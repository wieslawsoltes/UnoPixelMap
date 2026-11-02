# Lower Manhattan initial view

The z0 world tile supplies a global overview. The nine z14 tiles cover x=4822–4824 and y=6159–6161. They contain real OpenStreetMap geometry from OpenFreeMap's `20261025_080012_pt` planet snapshot. The initial view reads these resources without a network request. Other tiles stream from the live TileJSON catalog. To refresh this snapshot, run `python3 refresh.py` and update the snapshot identifier here.

The extract omits house numbers, aerodrome labels and unnamed POIs. Named points of interest keep their native and English names, class, subclass, rank and layer. The retained layers preserve the provider's coordinates, attributes, and building model heights. Height availability varies with source coverage.

[OpenFreeMap](https://openfreemap.org/) · [© OpenMapTiles](https://www.openmaptiles.org/) · Data from [© OpenStreetMap contributors](https://www.openstreetmap.org/copyright). OpenStreetMap data is provided under the [Open Database License](https://opendatacommons.org/licenses/odbl/1-0/). OpenMapTiles schema attribution is required under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
