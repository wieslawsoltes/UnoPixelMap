using PixelMap.Audio;
using PixelMap.Data;
using PixelMap.Routing;
using PixelMap.Simulation;

static void Check(bool condition,string message)
{
    if(!condition)throw new InvalidOperationException(message);
}

foreach(var mode in Enum.GetValues<TravelMode>())
{
    var route=RouteDemos.ForMode(mode);
    var sim=new JourneySession();sim.Load(route);
    var track=new RouteTrack(route);
    Check(sim.State==JourneyState.Ready && new RoutePoint(sim.Latitude,sim.Longitude).DistanceTo(route.Geometry[0])<.02,"Ready pose");
    sim.Preview(); for(int i=0;i<90;i++)sim.Tick(1d/60);
    Check(sim.Progress>0 && sim.SpeedMetersPerSecond>0,"Preview advances");
    sim.Pause();var clock=sim.AnimationSeconds;var d=sim.DistanceMeters;sim.Tick(1);Check(sim.DistanceMeters==d&&sim.AnimationSeconds==clock,"Frozen simulation");
    sim.Seek(.75);Check(Math.Abs(sim.DistanceMeters-route.DistanceMeters*.75)<1e-5 && track.Sample(route.DistanceMeters*.75).Point.DistanceTo(new(sim.Latitude,sim.Longitude))<.01,"Provider distance seek");
    sim.Resume();sim.PlaybackRate=32;for(int i=0;i<200 && sim.Active;i++) sim.Tick(.1);
    Check(sim.State==JourneyState.Finished,"Preview arrives");
    int gates=0;sim.Cue+=c=>{if(c==JourneyCue.Checkpoint)gates++;};
    sim.Drive();sim.SetCruise(true);
    for(int i=0;i<10000 && sim.Active;i++)sim.Tick(.1);
    Check(sim.State==JourneyState.Finished && gates==sim.CheckpointsTotal && sim.Score==gates*100+250,$"{mode} checkpoint game completion");
    Console.WriteLine($"{mode}: {route.DistanceMeters:F0} m, {gates} game gates, {sim.Score} points");
}
var demo=RouteDemos.ForMode(TravelMode.Drive);
JourneySession Steer(int count,double dt)
{
    var s=new JourneySession();s.Load(demo);s.Drive();s.SetInput(1,.4,0);
    for(int i=0;i<count;i++)s.Tick(dt);
    return s;
}
var a=Steer(180,1d/60);var b=Steer(45,1d/15);
Check(Math.Abs(a.SpeedMetersPerSecond-b.SpeedMetersPerSecond)<1e-8 && Math.Abs(a.Longitude-b.Longitude)<1e-8,"Fixed-step determinism");
Check(a.OffRouteMeters>1 && a.SpeedMetersPerSecond>2,"Analog inputs change physical heading");
var blocked=new AlwaysBlocked();a.World=blocked;
int contacts=0;a.Cue+=c=>{if(c==JourneyCue.Contact)contacts++;};
var old=a.RunDistanceMeters;for(int i=0;i<20;i++)a.Tick(.05);
Check(blocked.Calls>0 && a.RunDistanceMeters==old && contacts==1,"Collision cooldown and position");
a.Respawn();Check(a.SpeedMetersPerSecond==0 && a.OffRouteMeters==0 && a.Progress==0,"Recovery");
a.SelectVehicle(VehicleProfile.Car with { Id="electric", MaxSpeed=35 });
Check(a.Vehicle.Id=="electric" && a.State==JourneyState.Ready,"Custom vehicle profile");
a.Load(null);a.Drive();Check(!a.Active && a.Route is null,"Unloaded routes are inert");
var audio=new AudioProbe();using var sound=new JourneySound(audio);
var session=Steer(10,1d/60);sound.Update(session);Check(audio.Changes==0,"Quiet by default");
session.Muted=false;sound.Update(session);sound.Update(session);Check(audio.Changes==1 && audio.Enabled,"Mixer coalesces stable parameters");
sound.Play(session,JourneyCue.Horn);Check(audio.Plays==1,"Horn");
session.Pause();sound.Update(session);Check(!audio.Running,"Audio pauses with transport");

var world=new VectorCollisionWorld();
var ring=new[]{new TilePoint(1950,1950),new TilePoint(2150,1950),new TilePoint(2150,2150),new TilePoint(1950,2150)};
var building=new MapFeature("building","","","Tower",1,4096,GeometryType.Polygon,[ring],120,0,false,0,0,"");
world.Update([new MapTile(new(14,8192,8192),[building])]);
RoutePoint At(int x,int y)=>new(Math.Atan(Math.Sinh(Math.PI*(1-2*(8192+y/4096d)/16384)))*180/Math.PI,(8192+x/4096d)/16384*360-180);
Check(world.Blocks(VehicleProfile.Car,At(1900,2050),At(2000,2050),0),"Mapped footprint contact");
Check(!world.Blocks(VehicleProfile.Plane,At(1900,2050),At(2000,2050),200),"Fly above roofs");
world.Update([new MapTile(new(14,8192,8192),[building with { Layer="water" }])]);
Check(world.Blocks(VehicleProfile.Boat,At(2050,2050),At(1900,2050),0),"Shoreline contact");
WorldChecks.Run();
Console.WriteLine("Fixed-step, manual steering, obstacle, shoreline, recovery and audio checks passed.");

sealed class AlwaysBlocked:IJourneyWorld
{
    public int Calls;
    public bool Blocks(VehicleProfile v,RoutePoint f,RoutePoint t,double altitude){Calls++;return true;}
}
sealed class AudioProbe:IJourneyAudioDevice
{
    public bool Enabled, Running;
    public int Changes, Plays;
    public void Enable(bool enabled)=>Enabled=enabled;
    public void Update(string instrument,double speed,double load,bool running){Changes++;Running=running;}
    public void Play(string instrument,JourneyCue cue)=>Plays++;
    public void Close()=>Enabled=false;
}
