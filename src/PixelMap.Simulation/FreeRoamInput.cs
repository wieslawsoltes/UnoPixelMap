namespace PixelMap.Simulation;

/// <summary>Continuous game controls in the range −1 to 1, with Brake in 0 to 1.
/// Forward and strafe use the look direction for walking and free flight. Vehicle
/// steering uses Turn. Vertical controls world-up flight motion and aircraft lift.</summary>
public readonly record struct FreeRoamInput(double Forward = 0, double Strafe = 0,
    double Turn = 0, double Vertical = 0, double Brake = 0, bool Jump = false, bool Boost = false);
