using PixelMap.Data;
using PixelMap.Rendering;
using PixelMap.Routing;
using SkiaSharp;

internal static class PerspectiveChecks
{
    internal static void Run()
    {
        const int w=800,h=500;
        double worst=0, anchorWorst=0, dragWorst=0;
        foreach(double latitude in new[]{-70d,0,70})
        foreach(double bearing in new[]{-167d,0,57})
        foreach(double pitch in new[]{0d,25,55,72})
        foreach(double altitude in new[]{0d,80,2500})
        {
            var camera=new MapCamera(latitude,179.994,15,bearing,pitch,true,altitude);
            foreach(var (x,y) in new[]{(130d,180d),(400d,250d),(615d,320d)})
            {
                if (!camera.TryUnproject(x,y,w,h,out var location))continue;
                Assert(camera.TryProject(location.Latitude,location.Longitude,0,w,h,out var screen),"Ground intersection was outside the frustum");
                worst=Math.Max(worst,Math.Abs(screen.X-x)+Math.Abs(screen.Y-y));
                var turn=camera.TransformAt(.3,13,3,x,y,w,h);
                var s=turn.Project(location.Latitude,location.Longitude,w,h);
                anchorWorst=Math.Max(anchorWorst,Math.Abs(s.X-x)+Math.Abs(s.Y-y));
                var drag=camera.PanFromTo(x,y,x+23,y-9,w,h);
                s=drag.Project(location.Latitude,location.Longitude,w,h);
                dragWorst=Math.Max(dragWorst,Math.Abs(s.X-x-23)+Math.Abs(s.Y-y+9));
            }
            var target=camera.ProjectAtAltitude(latitude,179.994,altitude,w,h);
            Assert(Math.Abs(target.X-w*.5)+Math.Abs(target.Y-h*.5)<1e-5,"Airborne camera focus moved off the optical axis");
        }
        Console.WriteLine($"Perspective projection worst residual {worst:F7}px; orbit {anchorWorst:F7}px; drag {dragWorst:F7}px");
        Assert(worst<.01 && anchorWorst<.05 && dragWorst<.05,"Perspective projection/anchor error exceeded subpixel budget");
        var flight=new MapCamera(52,179.99,21,85,65,true,9000);
        var tiles=flight.VisibleTiles(w,h);
        Assert(tiles.Length<200 && tiles.Max(t=>t.Z)<14,"Flight requested street-resolution planet tiles");
        var high=new MapCamera(0,0,15,0,72,true);
        Assert(!high.TryUnproject(400,0,w,h,out _) && high.TryUnproject(400,450,w,h,out _),"Horizon and finite ground frustum are not distinguished");

        RoutePoint[] crossing=[new(55.01,179.97),new(55.03,-179.98),new(54.99,-179.93)];
        var fitted=new MapCamera(55,179.99,14,24,57,true).FitRoute(crossing,w,h,45);
        foreach(var point in crossing)
        {
            Assert(fitted.TryProject(point.Latitude,point.Longitude,0,w,h,out var screen),"Fit selected invisible station");
            Assert(screen.X>=44.9 && screen.X<=w-44.9 && screen.Y>=44.9 && screen.Y<=h-44.9,"Perspective route did not fit");
        }
        foreach (bool perspective in new[] { false, true })
        foreach (double pitch in new[] { 0d, 55, 72 })
        foreach (double bearing in new[] { -130d, 27 })
        foreach (var display in new[] { (W: 390, H: 844, Area: new SKRect(16, 100, 374, 490)),
                                       (W: 1100, H: 700, Area: new SKRect(330, 40, 1080, 660)) })
        {
            var inset = new MapCamera(55, 179.99, 14, bearing, pitch, perspective, 2500)
                .FitRoute(crossing, display.W, display.H, display.Area, 24);
            Assert(inset.TargetAltitudeMeters == 0, "Route fit retained an airborne target");
            foreach (var point in crossing)
            {
                Assert(inset.TryProject(point.Latitude, point.Longitude, 0, display.W, display.H, out var screen),
                    "Inset route fit placed a station outside the frustum");
                Assert(screen.X >= display.Area.Left + 23.9 && screen.X <= display.Area.Right - 23.9 &&
                    screen.Y >= display.Area.Top + 23.9 && screen.Y <= display.Area.Bottom - 23.9,
                    "Inset route fit used a different viewport lens");
            }
        }

        var id=new TileId(14,8192,8192);var center=Geo(id,2048,2048);
        var cam=new MapCamera(center.Latitude,center.Longitude,16,21,57,true);
        var blue=Raster(id);
        var near=Model(1,80,Box(1920,1920,2150,2170));
        var tall=Model(2,160,Box(2000,2000,2200,2250));
        var map=new MapTile(id,[near,tall]);
        using var renderer=new PixelMapRenderer();using var image=new SKBitmap(w,h);using var canvas=new SKCanvas(image);
        renderer.Draw(canvas,w,h,cam,[new(id,[])],pixelSize:1,imagery:[blue]);
        foreach(var (x,y) in new[]{(1700,1700),(2350,1700),(1700,2350),(2350,2350)})
        {
            var geo=Geo(id,x,y);Assert(cam.TryProject(geo.Latitude,geo.Longitude,0,w,h,out var screen),"Fixture should be visible");
            Assert(screen.X >= 0 && screen.X < w && screen.Y >= 0 && screen.Y < h, "Imagery fixture sampled outside the bitmap");
            var sample=image.GetPixel((int)screen.X,(int)screen.Y);
            Assert(sample.Blue>sample.Red+15,$"Homographic imagery registration failed at ({x},{y}) -> ({screen.X},{screen.Y}), color {sample}");
        }
        renderer.Draw(canvas,w,h,cam,[map],pixelSize:1,imagery:[blue]);
        var one=image.GetPixelSpan().ToArray();
        renderer.Draw(canvas,w,h,cam,[map with {Features=[tall,near]}],pixelSize:1,imagery:[blue]);
        Assert(one.AsSpan().SequenceEqual(image.GetPixelSpan()),"Perspective visibility depends on feature submission order");

        // Search for clearly visible interior pixels and require single-ray replay to select
        // the same model. A click on background keeps its ground behavior.
        var roofGeo=Geo(id,2100,2180);
        Assert(cam.TryProject(roofGeo.Latitude,roofGeo.Longitude,160,w,h,out var roof),"Roof is not in the expected frustum");
        var picked=renderer.Pick(roof.X,roof.Y,w,h,cam,[map]);
        Assert(picked?.Id==2,"Perspective roof picking disagrees with the map camera");

        var courtyard=Model(3,30,Box(1700,1700,2370,2370),Box(1920,1920,2170,2170).Reverse().ToArray());
        var nadir=cam with {Bearing=0,Pitch=0};
        renderer.Draw(canvas,w,h,nadir,[new(id,[courtyard])],pixelSize:1,imagery:[blue]);
        var hole=image.GetPixel(w/2,h/2);
        Assert(hole.Blue>hole.Red+15,"Perspective courtyard opening was filled");

        // Towers crossing the eye plane must be clipped into finite fragments, retaining
        // determinism and a bounded output even when the eye is below the roof.
        var superTall=Model(44,1800,Box(1750,2100,2300,2500));
        var lowEye=cam with {Pitch=72,Zoom=17.8};
        renderer.Draw(canvas,w,h,lowEye,[new(id,[])],pixelSize:1,imagery:[blue]);
        var empty = image.GetPixelSpan().ToArray();
        renderer.Draw(canvas,w,h,lowEye,[new(id,[superTall])],pixelSize:1,imagery:[blue]);
        Assert(image.Pixels.All(p=>p.Alpha==255),"Near-plane clipping corrupted the raster");
        Assert(!empty.AsSpan().SequenceEqual(image.GetPixelSpan()), "A tower crossing the eye plane disappeared");
        if(Environment.GetEnvironmentVariable("PIXELMAP_SCREENSHOT_DIR") is {Length:>0} output)
        {
            Directory.CreateDirectory(output);
            using var png=SKImage.FromBitmap(image).Encode(SKEncodedImageFormat.Png,100);
            using var file=File.Create(Path.Combine(output,"near-plane.png"));png.SaveTo(file);
        }
        Navigation(cam, w, h);
        Console.WriteLine("Passed true perspective frustum, ray inverse, anchors, target altitude, inset route fit, homogeneous raster registration, model depth, holes, picking and navigation alignment.");
    }

    private static void Navigation(MapCamera camera, int width, int height)
    {
        var start = camera.Unproject(140, 340, width, height);
        var finish = camera.Unproject(660, 180, width, height);
        RoutePoint a = new(start.Latitude, start.Longitude), b = new(finish.Latitude, finish.Longitude);
        var plan = Plan(TravelMode.Drive, [a, b]);
        using var renderer = new PixelMapRenderer();
        using var image = new SKBitmap(width, height);
        using var canvas = new SKCanvas(image);
        canvas.Clear(SKColors.Transparent);
        renderer.DrawNavigation(canvas, width, height, camera, plan, pixelSize: 1);
        var midpoint = a.InterpolateTo(b, .5);
        var screen = camera.Project(midpoint.Latitude, midpoint.Longitude, width, height);
        Assert(NearColor(screen, 0xff65cfee), "Perspective route line missed its geographic midpoint");
        canvas.Clear(SKColors.Transparent);
        renderer.DrawNavigation(canvas, width, height, camera, plan,
            new VehiclePose(new(80, 100), 0, Moving: false, Progress: .5), pixelSize: 1);
        var before = a.InterpolateTo(b, .40);
        var after = a.InterpolateTo(b, .60);
        Assert(NearColor(camera.Project(before.Latitude, before.Longitude, width, height), 0xff609d92),
            "Perspective progress ended before its physical station");
        Assert(NearColor(camera.Project(after.Latitude, after.Longitude, width, height), 0xff65cfee),
            "Perspective progress passed its physical station");

        var flight = camera with { TargetAltitudeMeters = 500 };
        var pose = new VehiclePose(midpoint, 45, AltitudeMeters: 500, Moving: false);
        var airbornePlan = Plan(TravelMode.Flight, [a, b]);
        canvas.Clear(SKColors.Transparent);
        renderer.DrawNavigation(canvas, width, height, flight, airbornePlan, pixelSize: 1);
        var baseline = image.GetPixelSpan().ToArray();
        renderer.DrawNavigation(canvas, width, height, flight, airbornePlan, pose, pixelSize: 1);
        Assert(flight.TryProject(midpoint.Latitude, midpoint.Longitude, 500, width, height, out var plane),
            "Airborne sprite fixture is outside the frustum");
        int changed = 0;
        for (int y = (int)plane.Y - 20; y <= plane.Y + 20; y++)
        for (int x = (int)plane.X - 20; x <= plane.X + 20; x++)
        {
            int offset = (y * width + x) * 4;
            if (!baseline.AsSpan(offset, 4).SequenceEqual(image.GetPixelSpan().Slice(offset, 4))) changed++;
        }
        Assert(changed > 30, "Vehicle was displaced from its real altitude projection");

        // Clearing the map's depth pass must also clear visibility after changing from a
        // ground camera to an airborne target, even before any model tiles have arrived.
        var highFlight = flight with { TargetAltitudeMeters = 5000 };
        var centered = new RoutePoint(highFlight.Latitude, highFlight.Longitude);
        var sample = new VehiclePose(centered, 45, AltitudeMeters: 5000, Moving: false);
        using var actual = new SKBitmap(width, height);
        using var expected = new SKBitmap(width, height);
        using var actualCanvas = new SKCanvas(actual);
        using var expectedCanvas = new SKCanvas(expected);
        renderer.Draw(actualCanvas, width, height, camera,
            [new(new TileId(14, 8192, 8192), [Model(900, 30, Box(1800, 1800, 2300, 2300))])], pixelSize: 1);
        renderer.Draw(actualCanvas, width, height, highFlight, [], pixelSize: 1);
        using var fresh = new PixelMapRenderer();
        fresh.Draw(expectedCanvas, width, height, highFlight, [], pixelSize: 1);
        renderer.DrawNavigation(actualCanvas, width, height, highFlight, airbornePlan, sample, pixelSize: 1);
        fresh.DrawNavigation(expectedCanvas, width, height, highFlight, airbornePlan, sample, pixelSize: 1);
        Assert(actual.GetPixelSpan().SequenceEqual(expected.GetPixelSpan()), "Empty depth retained an obsolete camera lens");

        bool NearColor((double X, double Y) point, uint color)
        {
            for (int y = (int)point.Y - 3; y <= point.Y + 3; y++)
            for (int x = (int)point.X - 3; x <= point.X + 3; x++)
                if ((uint)x < width && (uint)y < height && (uint)image.GetPixel(x, y) == color) return true;
            return false;
        }
    }

    private static RoutePlan Plan(TravelMode mode, RoutePoint[] points)
    {
        var start = new RouteStop("Start", points[0]);
        var finish = new RouteStop("Finish", points[^1]);
        double distance = points[0].DistanceTo(points[^1]);
        return new(mode, [start, finish], [new(start, finish, points, distance, TimeSpan.FromMinutes(3), [])], "Fixture", false);
    }
    private static MapFeature Model(ulong id,double height,params TilePoint[][] rings)=>new("building","","","",id,4096,GeometryType.Polygon,rings,height,0,false,0,0,"");
    private static TilePoint[] Box(int x0,int y0,int x1,int y1)=>[new(x0,y0),new(x1,y0),new(x1,y1),new(x0,y1)];
    private static RoutePoint Geo(TileId id,double x,double y)
    {
        int n=1<<id.Z;return new(Math.Atan(Math.Sinh(Math.PI*(1-2*(id.Y+y/4096)/n)))*180/Math.PI,(id.X+x/4096)*360/n-180);
    }
    private static ImageryTile Raster(TileId id)
    {
        using var b=new SKBitmap(256,256,SKColorType.Bgra8888,SKAlphaType.Opaque);b.Erase(new SKColor(36,95,128));
        using var i=SKImage.FromBitmap(b);using var bytes=i.Encode(SKEncodedImageFormat.Png,100);return new(id,bytes.ToArray());
    }
    private static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
