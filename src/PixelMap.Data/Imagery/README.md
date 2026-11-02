# Earth imagery

EOxCloudless 2016 provides an almost cloud-free, natural-colour mosaic assembled from Sentinel-2 observations taken in 2016 and 2017. The 10 m ground sampling distance supports regional and city-scale views. UnoPixelMap requests 256 × 256 Web Mercator JPEGs through zoom 14 and magnifies them for closer camera positions. Imagery dates and building-model dates differ. [Mosaic production](https://eox.at/2017/08/sentinel-2-global-cloudless-mosaic/)

The world overview and nine Lower Manhattan tiles are packaged for an offline first view. All ten JPEG payloads are unchanged from the public 2016 service. The map projects the imagery into the selected camera view. The remaining visible tiles stream from EOX on demand. Cached tiles are held in memory, with four concurrent fetches and a 4 MiB encoded-tile budget. [WMTS capabilities](https://tiles.maps.eox.at/wmts/1.0.0/WMTSCapabilities.xml)

EOxCloudless https://cloudless.eox.at by EOX IT Services GmbH (Contains modified Copernicus Sentinel data 2016 & 2017). [EOX::Maps](https://maps.eox.at/) · [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) · [2016 attribution and license terms](https://cloudless.eox.at/license-non-commercial) · [Tile service and fair use](https://cloudless.eox.at/documentation/faq)
