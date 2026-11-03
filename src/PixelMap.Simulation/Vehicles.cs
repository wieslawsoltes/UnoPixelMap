using PixelMap.Routing;

namespace PixelMap.Simulation;

public enum JourneyState { Ready, Preview, Driving, Paused, Finished }
public enum MotionModel { Ground, Vessel, Aircraft, Rail, Pedestrian }
public enum JourneyCue { Depart, Checkpoint, Contact, Finish, Horn }

/// <summary>Physical quantities are SI, with angular rates in degrees per second.</summary>
public sealed record VehicleProfile(string Id, TravelMode Mode, MotionModel Motion, double MaxSpeed,
    double Acceleration, double Braking, double CoastDrag, double TurnRate, double Response, double CruiseSpeed)
{
    public static VehicleProfile ForMode(TravelMode mode) => mode switch
    {
        TravelMode.Drive => Car,
        TravelMode.Boat => Boat,
        TravelMode.Flight => Plane,
        TravelMode.Train => Train,
        TravelMode.Bike => Bike,
        TravelMode.Walk => Walker,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    public static readonly VehicleProfile Car = new("roadster", TravelMode.Drive, MotionModel.Ground, 46, 5.1, 9, .32, 105, 5.5, 15);
    public static readonly VehicleProfile Boat = new("launch", TravelMode.Boat, MotionModel.Vessel, 17, 1.8, 2.3, .23, 43, .65, 9);
    public static readonly VehicleProfile Plane = new("turboprop", TravelMode.Flight, MotionModel.Aircraft, 140, 5, 7, .36, 25, 1.1, 93);
    public static readonly VehicleProfile Train = new("railcar", TravelMode.Train, MotionModel.Rail, 50, 1.2, 1.8, .06, 0, 1, 23);
    public static readonly VehicleProfile Bike = new("bicycle", TravelMode.Bike, MotionModel.Ground, 14, 2.3, 4.5, .29, 112, 4, 6.5);
    public static readonly VehicleProfile Walker = new("hiker", TravelMode.Walk, MotionModel.Pedestrian, 3.4, 2.3, 6, 1.7, 170, 7, 1.5);
    internal bool IsValid => !string.IsNullOrWhiteSpace(Id) && Enum.IsDefined(Mode) && Enum.IsDefined(Motion) && FinitePositive(MaxSpeed) && FinitePositive(Acceleration)
        && FinitePositive(Braking) && FinitePositive(Response) && FinitePositive(CruiseSpeed) && CruiseSpeed <= MaxSpeed
        && double.IsFinite(CoastDrag) && CoastDrag >= 0 && double.IsFinite(TurnRate) && TurnRate >= 0;
    private static bool FinitePositive(double x) => double.IsFinite(x) && x > 0;
}

public interface IJourneyWorld
{
    /// <summary>Return true if the proposed move would enter a mapped obstruction.</summary>
    bool Blocks(VehicleProfile vehicle, RoutePoint from, RoutePoint to, double altitudeMeters);

    /// <summary>Test a move whose endpoint altitudes are measured above mean sea level.
    /// Worlds implementing the original contact API receive the destination altitude.</summary>
    bool Blocks(VehicleProfile vehicle, RoutePoint from, RoutePoint to, double fromAltitudeMeters, double toAltitudeMeters)
        => Blocks(vehicle, from, to, toAltitudeMeters);

    /// <summary>The supporting terrain or water surface, in metres above mean sea level.</summary>
    double SurfaceElevationMeters(VehicleProfile vehicle, RoutePoint point) => 0;
}
