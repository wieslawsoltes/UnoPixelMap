# Terrain elevation data

The default elevation provider is Mapzen Terrain Tiles, hosted in the AWS Open Data registry. Tiles use the Terrarium RGB format and a public HTTPS XYZ endpoint. The original PNG samples are decoded directly, preserving signed heights and 1/256 metre encoding precision. Source resolution and accuracy vary across the worldwide elevation mosaic. [Terrain Tiles registry](https://registry.opendata.aws/terrain-tiles/) · [Format specification](https://github.com/tilezen/joerd/blob/master/docs/formats.md)

The endpoint is `https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png`. Requests from the published GitHub Pages origin were verified on 2026-11-03 with HTTP 200 and `Access-Control-Allow-Origin: *`. `ElevationTileSource` accepts a custom HTTPS Terrarium endpoint and uses that provider for its complete elevation field.

## Offline coverage

These ten original, unmodified provider PNGs are embedded for an offline world overview and the mountainous northern Lake Garda demonstration near Riva del Garda.

| Zoom | Columns | Rows | Tiles | Encoded bytes |
| --- | --- | --- | --- | --- |
| 0 | 0 | 0 | 1 | 106,274 |
| 12 | 2170–2172 | 1458–1460 | 9 | 1,108,706 |

Resource names follow `PixelMap.Elevation.{zoom}-{column}-{row}.png`. Pixel-centre heights, bilinear sampling across neighbouring tile boundaries, wrapped longitudes and clamped Mercator latitudes share one immutable `ElevationSnapshot`. Missing high-resolution tiles and void samples fall back to loaded coarser data. The world fixture supplies low-resolution coverage outside the northern Garda cluster.

The decoded cache is bounded to 16 MiB and 96 tiles, coalesces duplicate requests, allows four fetches at once and cancels an upstream request when its last consumer leaves. PNG payloads are bounded to 2 MiB. RGB/RGBA8 PNG decoding validates checksums, dimensions and decompressed row counts without an image-rendering dependency.

## Elevation and water surfaces

Raw sampling preserves signed terrain and ocean bathymetry. Surface sampling uses indexed water geometry from the highest covering vector tile. Mapped oceans use zero metres, while land, lakes and rivers retain their signed DEM elevations. This preserves below-sea-level land and inland water. Renderer, camera, route overlays and vehicle simulation can use the same surface sampler. Underlying sources have varying spatial resolution and vertical references.

## Attribution

Display “Mapzen Terrain Tiles” with a link to the provider's complete attribution page. The mosaic includes ArcticDEM, Geoscience Australia, Austrian open-government elevation, Canadian open-government elevation, EU-DEM/Copernicus, NOAA ETOPO1, Mexico INEGI, New Zealand LINZ, Norway Kartverket, the UK Environment Agency and USGS 3DEP, GMTED2010 and SRTM. Regional contributors and applicable license terms are listed by the provider. [Complete terrain attribution and source licenses](https://github.com/tilezen/joerd/blob/master/docs/attribution.md)
