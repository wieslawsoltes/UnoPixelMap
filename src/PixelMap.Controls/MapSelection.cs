namespace PixelMap.Controls;

/// <summary>The geographic location and label of the currently inspected map marker.</summary>
public readonly record struct MapSelection(double Latitude, double Longitude, string Label);
